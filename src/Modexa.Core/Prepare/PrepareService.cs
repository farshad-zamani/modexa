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

public readonly record struct PrepareProgress(PreparePhase Phase, int? Percent);

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
    int? ConfigBuild,
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

        // 1) Download (or reuse the cached copy).
        string archive = await BundleCache.GetAsync(bundle, new Progress<int>(p =>
            progress?.Report(new PrepareProgress(PreparePhase.Downloading, p))), ct);

        // 2) Verify the pack has what we expect, resolve version folders, find gameconfig variants.
        progress?.Report(new PrepareProgress(PreparePhase.Verifying, null));
        var files = await Task.Run(() => BundleArchive.ListFiles(archive, bundle.Password), ct);
        var (maps, versionWarning) = ResolveMaps(bundle, files, versionToken);
        foreach (var map in maps)
            if (!files.Any(f => Matches(map, f)))
                throw new PrepareLayoutException(map.From);

        string? variant = null;
        var outcome = GameConfigOutcome.NotApplicable;
        if (bundle.GameConfig is { } gc)
        {
            var variants = GameConfigVariants(files, gc.Folder);
            if (variants.Count == 0) throw new PrepareLayoutException(gc.Folder);
            string? def = variants.FirstOrDefault(v => string.Equals(v, gc.Default, StringComparison.OrdinalIgnoreCase))
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
            progress?.Report(new PrepareProgress(PreparePhase.PreparingArchive, 0));
            bool had = File.Exists(ModsArchives.ModsPath(gameFolder, ModsArchives.UpdateRpf));
            var copyProgress = new Progress<int>(p => progress?.Report(new PrepareProgress(PreparePhase.PreparingArchive, p)));
            try
            {
                await Task.Run(() => ModsArchives.EnsureOpen(gameFolder, ModsArchives.UpdateRpf, copyProgress, ct), ct);
                modsRpfCreated = !had;
            }
            catch (ModsArchiveException ex)
            {
                Diagnostics.Log.Error("Create mods update.rpf", ex);
                modsRpfError = ex; // the rest of the step still installs; the result explains what is missing
            }
        }

        // 4) Extract + install on a worker thread.
        progress?.Report(new PrepareProgress(PreparePhase.Extracting, 0));
        string stagedConfig = Path.Combine(AppPaths.CacheDir, "staging", Guid.NewGuid().ToString("N") + ".xml");
        string? configSource = variant != null && bundle.GameConfig != null
            ? $"{bundle.GameConfig.Folder.TrimEnd('/')}/{variant}/gameconfig.xml"
            : null;

        var installed = await Task.Run(() =>
        {
            var backup = new BackupSession(gameFolder);
            string root = Path.GetFullPath(gameFolder) + Path.DirectorySeparatorChar;

            foreach (var dir in bundle.CreateDirs)
                Directory.CreateDirectory(Path.Combine(gameFolder, dir));

            int count = BundleArchive.Extract(archive, bundle.Password, key =>
            {
                if (configSource != null && string.Equals(key, configSource, StringComparison.OrdinalIgnoreCase))
                    return stagedConfig;

                string? rel = Destination(maps, bundle.GameConfig, key);
                if (rel == null) return null;
                string full = Path.GetFullPath(Path.Combine(gameFolder, rel));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null; // zip-slip guard
                backup.TrackBeforeWrite(rel.Replace('/', Path.DirectorySeparatorChar));
                return full;
            }, new Progress<int>(p => progress?.Report(new PrepareProgress(PreparePhase.Extracting, p))), ct);

            progress?.Report(new PrepareProgress(PreparePhase.Installing, null));
            if (bundle.OpenIv) count += InstallOpenIv(gameFolder, backup);
            if (bundle.ModexaAsi && InstallModexaAsi(gameFolder, backup)) count++;

            var gcOutcome = GameConfigOutcome.NotApplicable;
            if (configSource != null)
            {
                gcOutcome = File.Exists(stagedConfig)
                    ? ApplyGameConfig(gameFolder, File.ReadAllBytes(stagedConfig), backup)
                    : throw new PrepareLayoutException(configSource);
                try { File.Delete(stagedConfig); } catch { }
            }
            return (count, gcOutcome);
        }, ct);

        if (outcome != GameConfigOutcome.Skipped) outcome = installed.gcOutcome;
        progress?.Report(new PrepareProgress(PreparePhase.Done, 100));

        int? forBuild = bundle.GameConfig?.ForBuild;
        bool mismatch = forBuild is > 0 && build > 0 && forBuild != build;
        // A gameconfig that couldn't be placed yet isn't "installed": keep the step open for a re-run.
        if (outcome is GameConfigOutcome.NotApplicable or GameConfigOutcome.Applied)
            PrepareState.MarkDone(gameFolder, step, variant);
        return new PrepareResult(installed.count, outcome, variant, forBuild, mismatch, versionWarning, modsRpfCreated, modsRpfError);
    }

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

    /// <summary>Writes gameconfig.xml into an OPEN mods\update\update.rpf (the original is backed up).</summary>
    public static GameConfigOutcome ApplyGameConfig(string gameFolder, byte[] xml, BackupSession backup)
    {
        string rpfPath = Path.Combine(gameFolder, ModsUpdateRpf.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(rpfPath)) return GameConfigOutcome.NeedsModsUpdateRpf;

        uint? enc = RpfArchive.PeekEncryption(rpfPath);
        if (enc is not (Rpf7.EncryptionOpen or Rpf7.EncryptionNone)) return GameConfigOutcome.UpdateRpfEncrypted;

        var ed = RpfEditor.Open(rpfPath);
        if (!ed.FileExists(GameConfigEntry)) return GameConfigOutcome.NoGameConfigEntry;

        backup.TrackArchiveFile(ModsUpdateRpf, GameConfigEntry, ed.ReadFile(GameConfigEntry));
        ed.SetFile(GameConfigEntry, xml);
        ed.Commit();
        return GameConfigOutcome.Applied;
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
    private static bool InstallModexaAsi(string gameFolder, BackupSession backup)
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("Modexa.Core.Assets.Modexa.asi");
        if (res == null) return false; // public builds without the compiled gate
        backup.TrackBeforeWrite("Modexa.asi");
        using var fs = File.Create(Path.Combine(gameFolder, "Modexa.asi"));
        res.CopyTo(fs);
        return true;
    }

    /// <summary>
    /// Installs the embedded OpenIV mods-folder enabler (OpenIV.asi + its ASI loader) into the game
    /// root. For GTA V Legacy this is what makes the game read from the mods\ folder. Returns the
    /// number of files written.
    /// </summary>
    private static int InstallOpenIv(string gameFolder, BackupSession backup)
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("Modexa.Core.Assets.OpenIVFiles.zip");
        if (res == null) return 0;
        using var zip = new ZipArchive(res, ZipArchiveMode.Read);
        int n = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            backup.TrackBeforeWrite(rel);
            string dest = Path.Combine(gameFolder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            n++;
        }
        return n;
    }

    // ---- revert ----------------------------------------------------------------------------------

    /// <summary>Reverts everything Modexa installed into a game folder, restoring vanilla files.</summary>
    public static void Revert(string gameFolder)
    {
        new BackupSession(gameFolder).RevertAll();
        PrepareState.Clear(gameFolder);
    }

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

    public static async Task<string> GetAsync(BundleEntry bundle, IProgress<int>? progress, CancellationToken ct)
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
            progress?.Report(100);
            return file; // unchanged on the server, or offline with a good local copy
        }

        await DownloadService.DownloadAsync(bundle.Url!, file,
            new Progress<DownloadProgress>(p => { if (p.Percent is { } pct) progress?.Report(pct); }),
            bundle.Sha256, ct).ConfigureAwait(false);

        var meta = remote ?? new Meta();
        meta.Url = bundle.Url;
        meta.Length = new FileInfo(file).Length;
        await File.WriteAllTextAsync(metaFile, JsonSerializer.Serialize(meta), ct).ConfigureAwait(false);
        return file;
    }

    private static bool SameVersion(Meta a, Meta b)
        => a.Length == b.Length
           && (string.IsNullOrEmpty(b.ETag) || a.ETag == b.ETag)
           && (string.IsNullOrEmpty(b.LastModified) || a.LastModified == b.LastModified);

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
                LastModified = resp.Content.Headers.LastModified?.ToString("R")
            };
        }
        catch
        {
            return null;
        }
    }
}
