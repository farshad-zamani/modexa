using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Modexa.Core.Download;
using Modexa.Core.Games;
using Modexa.Core.Install;
using Modexa.Core.Rpf;

namespace Modexa.Core.Prepare;

/// <summary>Thrown when a step has no download bundle configured.</summary>
public sealed class PrepareNotConfiguredException : Exception
{
    public PrepareNotConfiguredException(PrepareStep step)
        : base($"No download bundle is configured for step '{step}' yet.") { Step = step; }

    public PrepareStep Step { get; }
}

/// <summary>The downloaded pack doesn't contain what the manifest expects (its layout changed).</summary>
public sealed class PrepareLayoutException : Exception
{
    public PrepareLayoutException(string missing) : base($"The downloaded pack is missing: {missing}") { }
}

/// <summary>The pack has no Mod Runner for the detected game version.</summary>
public sealed class PrepareVersionException : Exception
{
    public PrepareVersionException(string detected, IReadOnlyList<string> available)
        : base($"No Mod Runner for version '{detected}'. Available: {string.Join(", ", available)}")
    { Detected = detected; Available = available; }

    public string Detected { get; }
    public IReadOnlyList<string> Available { get; }
}

public enum PreparePhase { Downloading, Verifying, PreparingArchive, Extracting, Installing, Done }

/// <summary>Prepare progress: the phase, its percentage, and (when known) bytes done of total and the item.</summary>
/// <param name="Cached">Downloading phase only: the copy downloaded earlier is still current and is reused.</param>
public readonly record struct PrepareProgress(PreparePhase Phase, int? Percent, long Done = 0, long Total = 0, string? Item = null,
    bool Cached = false);

/// <summary>What happened to gameconfig.xml (it lives inside mods\update\update.rpf).</summary>
public enum GameConfigOutcome
{
    NotApplicable,       // this step has no gameconfig
    Applied,             // written into mods\update\update.rpf
    NeedsModsUpdateRpf,  // mods\update\update.rpf doesn't exist yet
    UpdateRpfEncrypted,  // it exists but is still encrypted (not OPEN)
    NoGameConfigEntry,   // the archive has no common\data\gameconfig.xml to replace
    Skipped              // the user cancelled the variant choice
}

public sealed record PrepareResult(
    int FilesInstalled,
    GameConfigOutcome GameConfig,
    string? GameConfigVariant,
    string? ConfigFor,
    bool BuildMismatch,
    string? VersionWarning = null,
    bool ModsRpfCreated = false,
    ModsArchiveException? ModsRpfError = null);

/// <summary>
/// Runs a "prepare for mods" step end-to-end: resolve the edition-specific bundle, download it once
/// (cached, re-checked by size/ETag), verify the pack layout, extract only the mapped files into the
/// game folder (backing up anything overwritten), install Modexa.asi with the Mod Runner, and put
/// the chosen gameconfig.xml into an OPEN mods\update\update.rpf.
/// </summary>
public sealed class PrepareService
{
    public const string ModsUpdateRpf = "mods/update/update.rpf";
    private const string GameConfigEntry = "common/data/gameconfig.xml";

    private readonly PrepareManifest _manifest;

    public PrepareService(PrepareManifest manifest) => _manifest = manifest;

    /// <param name="chooseVariant">
    /// Asked (on the caller's context) to pick a gameconfig variant: (variants, default) -> choice or null.
    /// </param>
    public async Task<PrepareResult> RunAsync(
        GameId game,
        PrepareStep step,
        string gameFolder,
        GameEdition edition,
        int build,
        string? versionToken = null,
        IProgress<PrepareProgress>? progress = null,
        CancellationToken ct = default,
        Func<IReadOnlyList<string>, string?, Task<string?>>? chooseVariant = null)
    {
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            throw new DirectoryNotFoundException(gameFolder);

        var bundle = _manifest.Resolve(game, step, edition, build);
        if (bundle == null || string.IsNullOrWhiteSpace(bundle.Url))
            throw new PrepareNotConfiguredException(step);

        // 1) Download — only when the server copy changed since the last download.
        string packName = Path.GetFileName(Uri.UnescapeDataString(new Uri(bundle.Url!).AbsolutePath));
        var (archive, cached) = await BundleCache.GetAsync(bundle, new Progress<DownloadProgress>(p =>
            progress?.Report(new PrepareProgress(PreparePhase.Downloading, p.Percent, p.BytesReceived, p.TotalBytes ?? 0, packName))), ct);
        if (cached)
        {
            long len = new FileInfo(archive).Length;
            progress?.Report(new PrepareProgress(PreparePhase.Downloading, 100, len, len, packName, Cached: true));
        }

        // 2) Verify the pack has what we expect, resolve version folders, find gameconfig variants.
        progress?.Report(new PrepareProgress(PreparePhase.Verifying, null));
        var files = await Task.Run(() => BundleArchive.ListFiles(archive, bundle.Password), ct);
        var (maps, versionWarning) = ResolveMaps(bundle, files, versionToken);
        foreach (var map in maps)
            if (!files.Any(f => Matches(map, f)))
                throw new PrepareLayoutException(map.From);

        string? variant = null, configFolder = null, configFor = null;
        bool mismatch = false;
        var outcome = GameConfigOutcome.NotApplicable;
        if (bundle.GameConfig is { } gc)
        {
            // The config made for this exact build (or its family, e.g. "1.0.35" for 35xx).
            var pick = GameConfigVersions.Pick(files, gc, build)
                       ?? throw new PrepareLayoutException(gc.VersionsRoot ?? gc.Folder);
            configFolder = pick.Folder;
            configFor = pick.ForBuild;
            mismatch = !pick.Matches;
            var variants = GameConfigVariants(files, configFolder)
                .Where(v => !gc.Exclude.Any(x => v.Contains(x, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (variants.Count == 0) throw new PrepareLayoutException(configFolder);
            // Default: the game's own traffic ("Stock Traffic …"), unchanged.
            string? def = variants.FirstOrDefault(v => string.Equals(v, gc.Default, StringComparison.OrdinalIgnoreCase))
                          ?? variants.FirstOrDefault(v => !string.IsNullOrEmpty(gc.Default)
                                                          && v.Split('/')[^1].StartsWith(gc.Default!, StringComparison.OrdinalIgnoreCase))
                          ?? variants.FirstOrDefault(v => v.Split('/')[^1].StartsWith("Stock", StringComparison.OrdinalIgnoreCase))
                          ?? variants[0];
            // Back on the caller's (UI) context here: no ConfigureAwait(false) above this line.
            variant = chooseVariant != null ? await chooseVariant(variants, def) : def;
            if (variant == null) outcome = GameConfigOutcome.Skipped;
        }

        // 3) gameconfig.xml lives in mods\update\update.rpf: create that editable copy first if
        //    needed (copy of the game's update.rpf converted to OPEN — what OpenIV used to do).
        bool modsRpfCreated = false;
        ModsArchiveException? modsRpfError = null;
        if (variant != null && bundle.GameConfig != null && !await Task.Run(() => ModsArchives.IsReady(gameFolder), ct))
        {
            bool had = File.Exists(ModsArchives.ModsPath(gameFolder, ModsArchives.UpdateRpf));
            long size = await Task.Run(() => ModsArchives.CopySize(gameFolder, ModsArchives.UpdateRpf), ct);
            const string item = "mods\\update\\update.rpf";
            progress?.Report(new PrepareProgress(PreparePhase.PreparingArchive, 0, 0, size, item));
            long last = 0;
            var copied = new Progress<long>(n =>
            {
                if (n - last < (16 << 20) && n < size) return;
                last = n;
                progress?.Report(new PrepareProgress(PreparePhase.PreparingArchive, size > 0 ? (int)(n * 100 / size) : null, n, size, item));
            });
            try
            {
                await Task.Run(() => ModsArchives.EnsureOpen(gameFolder, ModsArchives.UpdateRpf, null, ct, copied: copied), ct);
                modsRpfCreated = !had;
            }
            catch (ModsArchiveException ex)
            {
                Diagnostics.Log.Error("Create mods update.rpf", ex);
                modsRpfError = ex; // the rest of the step still installs; the result explains what is missing
            }
        }

        // 4) Extract + install on a worker thread. The step is an installed item of its own (listed
        //    with the mods, removable on its own); a re-run updates the same record, so its saved
        //    originals stay the true originals.
        progress?.Report(new PrepareProgress(PreparePhase.Extracting, 0));
        string stagedConfig = Path.Combine(AppPaths.CacheDir, "staging", Guid.NewGuid().ToString("N") + ".xml");
        AppPaths.EnsureDir(Path.GetDirectoryName(stagedConfig)!);
        string? configSource = variant != null && bundle.GameConfig != null
            ? $"{configFolder}/{variant}/gameconfig.xml"
            : null;

        var record = InstalledModsStore.ForFolder(gameFolder).FirstOrDefault(r => r.Setup == step)
                     ?? new ModInstallRecord
                     {
                         Name = StepName(game, step),
                         ModType = Format.MxaModType.Loose,
                         Game = GameKey(game),
                         GameTitle = GameCatalog.Get(game).DisplayName,
                         GameFolder = gameFolder,
                         Setup = step,
                         Author = "WTMod.com",
                         InstalledUtc = DateTime.UtcNow
                     };
        record.Source = Path.GetFileName(archive);
        if (modsRpfCreated && !record.CreatedArchives.Contains(ModsArchives.UpdateRpf, StringComparer.OrdinalIgnoreCase))
            record.CreatedArchives.Add(ModsArchives.UpdateRpf);

        (int count, GameConfigOutcome gcOutcome) installed;
        try
        {
            installed = await Task.Run(() =>
            {
                string root = Path.GetFullPath(gameFolder) + Path.DirectorySeparatorChar;
                foreach (var dir in bundle.CreateDirs)
                    InstallDirs.EnsureFor(record, gameFolder, Path.Combine(gameFolder, dir, "_"));

                var written = new List<string>();
                int count = BundleArchive.Extract(archive, bundle.Password, key =>
                {
                    if (configSource != null && string.Equals(key, configSource, StringComparison.OrdinalIgnoreCase))
                        return stagedConfig;

                    string? rel = Destination(maps, bundle.GameConfig, key);
                    if (rel == null) return null;
                    string full = Path.GetFullPath(Path.Combine(gameFolder, rel));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null; // zip-slip guard
                    string relWin = rel.Replace('/', Path.DirectorySeparatorChar);
                    LooseFileInstaller.Track(record, relWin);
                    InstallDirs.EnsureFor(record, gameFolder, full);
                    written.Add(relWin);
                    return full;
                }, new Progress<int>(p => progress?.Report(new PrepareProgress(PreparePhase.Extracting, p))), ct);
                foreach (var rel in written) LooseFileInstaller.Stamp(record, rel);

                progress?.Report(new PrepareProgress(PreparePhase.Installing, null));
                if (bundle.OpenIv) count += InstallOpenIv(gameFolder, record);
                if (bundle.ModexaAsi && InstallModexaAsi(gameFolder, record)) count++;

                var gcOutcome = GameConfigOutcome.NotApplicable;
                if (configSource != null)
                {
                    gcOutcome = File.Exists(stagedConfig)
                        ? ApplyGameConfig(gameFolder, File.ReadAllBytes(stagedConfig), record)
                        : throw new PrepareLayoutException(configSource);
                    try { File.Delete(stagedConfig); } catch { }
                }
                if (variant != null && gcOutcome == GameConfigOutcome.Applied) record.Version = variant;
                return (count, gcOutcome);
            }, ct);
        }
        finally
        {
            // Whatever was written is on record (even after a cancel), so it can always be removed.
            if (record.Files.Count > 0 || record.ArchiveEdits.Count > 0 || record.CreatedDirs.Count > 0)
                InstalledModsStore.Add(record);
        }

        if (outcome != GameConfigOutcome.Skipped) outcome = installed.gcOutcome;
        progress?.Report(new PrepareProgress(PreparePhase.Done, 100));

        return new PrepareResult(installed.count, outcome, variant, configFor, mismatch && build > 0, versionWarning, modsRpfCreated, modsRpfError);
    }

    /// <summary>The prepare steps installed in a game folder (a GameConfig step counts once its gameconfig.xml is in place).</summary>
    public static Dictionary<PrepareStep, ModInstallRecord> InstalledSteps(string gameFolder)
    {
        var map = new Dictionary<PrepareStep, ModInstallRecord>();
        foreach (var r in InstalledModsStore.ForFolder(gameFolder))
            if (r.Setup is { } s && (s != PrepareStep.GameConfig || r.ArchiveEdits.Any(e => !e.Reverted)))
                map[s] = r;
        return map;
    }

    private static string StepName(GameId game, PrepareStep step) => step switch
    {
        PrepareStep.GameConfig => "GameConfig",
        PrepareStep.Menu => "Menyoo",
        _ => "Mod Runner"
    };

    private static string GameKey(GameId game) => game switch
    {
        GameId.GtaV or GameId.GtaVEnhanced => "GtaV",
        GameId.GtaSanAndreas => "GtaSa",
        GameId.GtaIV => "GtaIV",
        GameId.RedDeadRedemption1 => "Rdr1",
        GameId.RedDeadRedemption2 => "Rdr2",
        GameId.Cyberpunk2077 => "Cp2077",
        _ => game.ToString()
    };

    // ---- version-folder resolution ---------------------------------------------------------------

    /// <summary>
    /// Expands any <c>*</c> segment in a map's <c>from</c> to the concrete version folder present in
    /// the archive, preferring the one matching <paramref name="versionToken"/>.
    /// </summary>
    private static (List<BundleMap> maps, string? versionWarning) ResolveMaps(
        BundleEntry bundle, IReadOnlyList<string> files, string? versionToken)
    {
        var result = new List<BundleMap>();
        string? warning = null;

        foreach (var map in bundle.Maps)
        {
            if (!map.From.Contains('*')) { result.Add(map); continue; }

            var rx = new Regex("^" + Regex.Escape(map.From).Replace("\\*", "([^/]+)"), RegexOptions.IgnoreCase);
            var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // prefix -> version segment
            foreach (var f in files)
            {
                var m = rx.Match(f);
                if (m.Success && m.Groups.Count > 1) candidates[m.Value] = m.Groups[1].Value;
            }
            if (candidates.Count == 0) throw new PrepareLayoutException(map.From);

            KeyValuePair<string, string>? pick = null;
            if (!string.IsNullOrWhiteSpace(versionToken))
                foreach (var kv in candidates)
                    if (VersionMatches(kv.Value, versionToken!)) { pick = kv; break; }

            if (pick == null)
            {
                if (bundle.RequireVersionMatch && !string.IsNullOrWhiteSpace(versionToken))
                    throw new PrepareVersionException(versionToken!,
                        candidates.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                pick = candidates.First();
                if (!string.IsNullOrWhiteSpace(versionToken)) warning = pick.Value.Value;
            }

            result.Add(new BundleMap { From = pick.Value.Key, To = map.To });
        }
        return (result, warning);
    }

    private static bool VersionMatches(string folderSegment, string versionToken)
    {
        string s = NormVersion(folderSegment), t = NormVersion(versionToken);
        return s.Length > 0 && t.Length > 0 && (s.Contains(t) || t.Contains(s));
    }

    private static string NormVersion(string s)
        => new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '.').ToArray());

    /// <summary>
    /// Writes gameconfig.xml into an OPEN mods\update\update.rpf, recorded on the step's record (the
    /// original is the game's own when the copy was made for this step, else it is saved).
    /// </summary>
    public static GameConfigOutcome ApplyGameConfig(string gameFolder, byte[] xml, ModInstallRecord record)
    {
        string rpfPath = Path.Combine(gameFolder, ModsUpdateRpf.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(rpfPath)) return GameConfigOutcome.NeedsModsUpdateRpf;

        uint? enc = RpfArchive.PeekEncryption(rpfPath);
        if (enc is not (Rpf7.EncryptionOpen or Rpf7.EncryptionNone)) return GameConfigOutcome.UpdateRpfEncrypted;

        bool fresh = record.CreatedArchives.Contains(ModsArchives.UpdateRpf, StringComparer.OrdinalIgnoreCase);
        var recorder = new ArchiveEditRecorder(record);
        return ArchiveChain.Edit(gameFolder, ModsArchives.UpdateRpf, Array.Empty<string>(), ed =>
        {
            if (!ed.FileExists(GameConfigEntry)) return GameConfigOutcome.NoGameConfigEntry;
            recorder.Touch(ModsArchives.UpdateRpf, Array.Empty<string>(), ed, GameConfigEntry, originalIsGame: fresh);
            ed.SetFile(GameConfigEntry, xml);
            return GameConfigOutcome.Applied;
        }, observer: recorder.Observe(ModsArchives.UpdateRpf));
    }

    /// <summary>State of mods\update\update.rpf (the editable copy add-ons and gameconfig need).</summary>
    public static GameConfigOutcome ModsUpdateRpfState(string gameFolder)
    {
        string rpfPath = Path.Combine(gameFolder, ModsUpdateRpf.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(rpfPath)) return GameConfigOutcome.NeedsModsUpdateRpf;
        uint? enc = RpfArchive.PeekEncryption(rpfPath);
        return enc is Rpf7.EncryptionOpen or Rpf7.EncryptionNone ? GameConfigOutcome.Applied : GameConfigOutcome.UpdateRpfEncrypted;
    }

    // ---- mapping ---------------------------------------------------------------------------------

    private static bool Matches(BundleMap map, string key)
        => map.From.EndsWith('/')
            ? key.StartsWith(map.From, StringComparison.OrdinalIgnoreCase) && key.Length > map.From.Length
            : string.Equals(key, map.From, StringComparison.OrdinalIgnoreCase);

    /// <summary>Game-relative destination for an archive entry, or null when it isn't installed.</summary>
    private static string? Destination(List<BundleMap> maps, GameConfigSpec? gameConfig, string key)
    {
        // v1 bundles (no maps, no gameconfig) extract everything; a gameconfig-only bundle installs nothing else.
        if (maps.Count == 0) return gameConfig == null ? key : null;
        foreach (var map in maps)
        {
            if (!Matches(map, key)) continue;
            if (!map.From.EndsWith('/')) return map.To;
            string rel = key[map.From.Length..];
            return string.IsNullOrEmpty(map.To) ? rel : $"{map.To.TrimEnd('/')}/{rel}";
        }
        return null;
    }

    /// <summary>Variant names (relative folders) under <paramref name="folder"/> that hold a gameconfig.xml.</summary>
    public static List<string> GameConfigVariants(IEnumerable<string> files, string folder)
    {
        string prefix = folder.TrimEnd('/') + "/";
        return files
            .Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && f.EndsWith("/gameconfig.xml", StringComparison.OrdinalIgnoreCase))
            .Select(f => f[prefix.Length..^"/gameconfig.xml".Length])
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- Modexa.asi ------------------------------------------------------------------------------

    /// <summary>Copies the embedded Modexa.asi (paid-mod entitlement gate) into the game root.</summary>
    private static bool InstallModexaAsi(string gameFolder, ModInstallRecord record)
    {
        if (Assembly.GetExecutingAssembly().GetManifestResourceInfo("Modexa.Core.Assets.Modexa.asi") == null)
            return false; // public builds without the compiled gate
        LooseFileInstaller.CopyTracked(record,
            () => Assembly.GetExecutingAssembly().GetManifestResourceStream("Modexa.Core.Assets.Modexa.asi")!, "Modexa.asi");
        return true;
    }

    /// <summary>
    /// Installs the embedded OpenIV mods-folder enabler (OpenIV.asi + its ASI loader) into the game
    /// root. For GTA V Legacy this is what makes the game read from the mods\ folder. Returns the
    /// number of files written.
    /// </summary>
    private static int InstallOpenIv(string gameFolder, ModInstallRecord record)
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("Modexa.Core.Assets.OpenIVFiles.zip");
        if (res == null) return 0;
        using var zip = new ZipArchive(res, ZipArchiveMode.Read);
        string root = Path.GetFullPath(gameFolder) + Path.DirectorySeparatorChar;
        int n = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (!Path.GetFullPath(Path.Combine(gameFolder, rel)).StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            LooseFileInstaller.CopyTracked(record, entry.Open, rel);
            n++;
        }
        return n;
    }

    // ---- revert ----------------------------------------------------------------------------------

    /// <summary>
    /// After every installed item was uninstalled: removes what is left of Modexa in the game folder
    /// (archive copies it made, files tracked by older versions), restoring vanilla files.
    /// </summary>
    public static void Revert(string gameFolder) => new BackupSession(gameFolder).RevertAll();

    public static bool HasBackups(string gameFolder) => new BackupSession(gameFolder).HasBackups;
}

/// <summary>Downloads bundles once and reuses them while the server copy is unchanged.</summary>
public static class BundleCache
{
    private sealed class Meta
    {
        public string? Url { get; set; }
        public long Length { get; set; }
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
    }

    private static string Dir => Path.Combine(AppPaths.CacheDir, "bundles");

    /// <summary>
    /// The pack on disk: the earlier download when the server copy is unchanged (or the server can't
    /// be reached), else a fresh download. <c>cached</c> tells which.
    /// </summary>
    public static async Task<(string file, bool cached)> GetAsync(BundleEntry bundle, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var uri = new Uri(bundle.Url!);
        string name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name)) name = "bundle.zip";
        string file = Path.Combine(Dir, name);
        string metaFile = file + ".json";

        Meta? cached = null;
        try { if (File.Exists(metaFile)) cached = JsonSerializer.Deserialize<Meta>(await File.ReadAllTextAsync(metaFile, ct).ConfigureAwait(false)); }
        catch { }

        Meta? remote = await HeadAsync(uri, ct).ConfigureAwait(false);
        bool haveFile = File.Exists(file) && cached != null && cached.Url == bundle.Url && new FileInfo(file).Length == cached.Length;

        if (haveFile && (remote == null || SameVersion(cached!, remote)))
        {
            return (file, true); // unchanged on the server, or offline with a good local copy
        }

        await DownloadService.DownloadAsync(bundle.Url!, file, progress, bundle.Sha256, ct).ConfigureAwait(false);

        var meta = remote ?? new Meta();
        meta.Url = bundle.Url;
        meta.Length = new FileInfo(file).Length;
        await File.WriteAllTextAsync(metaFile, JsonSerializer.Serialize(meta), ct).ConfigureAwait(false);
        return (file, false);
    }

    /// <summary>
    /// Is the server copy the one we have? A different size always means a new pack. Otherwise the
    /// ETag decides (CDNs may add "W/" or vary it between edge servers, so a matching Last-Modified
    /// also counts), then Last-Modified; with nothing to compare, the local copy is kept.
    /// </summary>
    private static bool SameVersion(Meta local, Meta server)
    {
        if (server.Length > 0 && local.Length != server.Length) return false;
        string? le = NormEtag(local.ETag), se = NormEtag(server.ETag);
        bool lastModSame = !string.IsNullOrEmpty(local.LastModified) && local.LastModified == server.LastModified;
        if (le != null && se != null) return le == se || lastModSame;
        if (!string.IsNullOrEmpty(local.LastModified) && !string.IsNullOrEmpty(server.LastModified)) return lastModSame;
        return true;
    }

    private static string? NormEtag(string? etag)
    {
        if (string.IsNullOrWhiteSpace(etag)) return null;
        string e = etag.Trim();
        if (e.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) e = e[2..];
        return e.Trim('"');
    }

    private static async Task<Meta?> HeadAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Modexa/1.0");
            using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return new Meta
            {
                Length = resp.Content.Headers.ContentLength ?? -1,
                ETag = resp.Headers.ETag?.Tag,
                LastModified = resp.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return null;
        }
    }
}
