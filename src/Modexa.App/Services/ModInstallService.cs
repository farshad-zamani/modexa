using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Modexa.App.Views;
using Modexa.Core;
using Modexa.Core.Diagnostics;
using Modexa.Core.Engine;
using Modexa.Core.Format;
using Modexa.Core.Games;
using Modexa.Core.I18n;
using Modexa.Core.Install;
using Modexa.Core.Licensing;
using Modexa.Core.Rpf;

namespace Modexa.App.Services;

public sealed record ModResult(bool Success, string Message, ModInstallRecord? Record = null, bool Cancelled = false);

/// <summary>
/// Progress of an install for the page's busy card: a localized phase key, a percentage when known,
/// a detail line (file / archive, sizes), the step ("Package 1 of 2"), and the mod's name and icon.
/// </summary>
public readonly record struct InstallPhase(string Key, int? Percent = null, string? Detail = null, string? Step = null,
    string? Title = null, byte[]? Icon = null)
{
    /// <summary>Phase key that tells the page to hide its busy overlay (a dialog is about to show).</summary>
    public const string Hide = "__hide";

    public static implicit operator InstallPhase(string key) => new(key);
}

/// <summary>The game a mod is installed into (always chosen on that game's page).</summary>
public sealed record GameTarget(GameId Id, string Folder, string Title, GameEdition Edition)
{
    public bool IsGtaV => Id is GameId.GtaV or GameId.GtaVEnhanced;

    /// <summary>The game key written into .mxa headers by the packer.</summary>
    public string MxaKey => Id switch
    {
        GameId.GtaV or GameId.GtaVEnhanced => "GtaV",
        GameId.GtaSanAndreas => "GtaSa",
        GameId.GtaIV => "GtaIV",
        GameId.RedDeadRedemption1 => "Rdr1",
        GameId.RedDeadRedemption2 => "Rdr2",
        GameId.Cyberpunk2077 => "Cp2077",
        _ => Id.ToString()
    };

    public bool Accepts(MxaInfo info)
        => string.Equals(info.Game, MxaKey, StringComparison.OrdinalIgnoreCase)
           || string.Equals(info.Game, Id.ToString(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Every install and uninstall, driven from a game's page:
///
///   .oiv / .oivs (all tiers)  -> public OIV installer (read straight from the zip), fully recorded
///                                for an exact uninstall; several files are installed in name order
///                                (part 1, part 2, …) after one confirmation.
///   .mxa (licensed)           -> license for THIS product -> engine module -> content key -> engine installs.
///   .rpf / .zip / folder (Pro)-> Pro engine.
///   uninstall (all tiers)     -> <see cref="ModUninstaller"/> plays the install record back in reverse.
///
/// UI prompts run on the UI thread; all file work runs on worker threads.
/// </summary>
public static class ModInstallService
{
    public static readonly string[] OivExtensions = { ".oiv", ".oivs" };
    public static readonly string[] RawExtensions = { ".rpf", ".zip" };

    public static bool IsInstallable(string path, bool gtaV)
        => path.EndsWith(MxaFile.Extension, StringComparison.OrdinalIgnoreCase)
           || (gtaV && (OivExtensions.Concat(RawExtensions).Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase)) || Directory.Exists(path)));

    /// <summary>
    /// Installs several files in name order (so a split mod goes part 1, part 2, …). OpenIV packages
    /// are confirmed together, once.
    /// </summary>
    /// <param name="confirm">False when the user already chose to install (e.g. enabling a disabled mod).</param>
    public static async Task<List<ModResult>> InstallManyAsync(IReadOnlyList<string> paths, GameTarget target, Window owner,
        IProgress<InstallPhase>? phase, bool confirm = true)
    {
        var ordered = paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Path.GetFileName, NaturalComparer.Instance).ToList();
        var oivs = target.IsGtaV ? ordered.Where(OivInstaller.IsPackageFile).ToList() : new List<string>();
        var results = new List<ModResult>();
        if (oivs.Count > 0) results.AddRange(await InstallOivAsync(oivs, target, phase, confirm));
        foreach (var p in ordered.Except(oivs))
            results.Add(await InstallAsync(p, target, owner, phase));
        return results;
    }

    /// <summary>Routes a file to the right installer for <paramref name="target"/>.</summary>
    public static async Task<ModResult> InstallAsync(string path, GameTarget target, Window owner, IProgress<InstallPhase>? phase)
    {
        if (path.EndsWith(MxaFile.Extension, StringComparison.OrdinalIgnoreCase))
            return await InstallPackageAsync(path, target, owner, phase);
        if (!target.IsGtaV)
            return Fail("Mods_UnsupportedHere");
        if (OivInstaller.IsPackageFile(path))
            return (await InstallOivAsync(new[] { path }, target, phase)).FirstOrDefault() ?? new ModResult(false, "", Cancelled: true);
        return await InstallRawAsync(path, target, owner, phase);
    }

    // ---- OIV (Free) ------------------------------------------------------------------------------

    public static async Task<List<ModResult>> InstallOivAsync(IReadOnlyList<string> paths, GameTarget target, IProgress<InstallPhase>? phase,
        bool confirm = true)
    {
        if (await Task.Run(() => GameProcess.IsRunning(target.Folder))) return new() { Fail("Mods_GameRunning") };

        // 1) What each package says about itself (only assembly.xml is read: instant even for GBs).
        phase?.Report("Oiv_Reading");
        var items = new List<(string Path, OivPackageInfo Info)>();
        foreach (var path in paths)
        {
            try { items.Add((path, await Task.Run(() => OivInstaller.Inspect(path)))); }
            catch (Exception ex) when (ex is ModInstallException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return new() { new ModResult(false, Loc.Instance.Format("Oiv_Invalid", $"{Path.GetFileName(path)}: {ex.Message}")) };
            }
        }
        string NameOf((string Path, OivPackageInfo Info) i)
            => string.IsNullOrWhiteSpace(i.Info.Name) ? Path.GetFileNameWithoutExtension(i.Path) : i.Info.Name;

        // 2) Disk space: copies of the game archives the packages edit + their content.
        long needed = await Task.Run(() => items.Sum(i => OivInstaller.SpaceNeeded(i.Info, target.Folder)));
        var (free, drive) = FreeSpace(target.Folder);

        // 3) One confirmation, showing what the packages say about themselves.
        phase?.Report(new InstallPhase(InstallPhase.Hide));
        if (free >= 0 && needed > free)
        {
            DialogWindow.Show(Loc.Instance.Format("Oiv_NoSpace", Size(needed), drive, Size(free)), DialogKind.Warning);
            return new() { new ModResult(false, "", Cancelled: true) };
        }
        var first = items[0];
        var lines = new List<string>();
        if (items.Count == 1)
        {
            lines.Add(Loc.Instance.Format("Oiv_Confirm", NameOf(first), target.Title));
            if (!string.IsNullOrWhiteSpace(first.Info.Version)) lines.Add(Loc.Instance.Format("Oiv_Version", first.Info.Version));
            if (!string.IsNullOrWhiteSpace(first.Info.Author)) lines.Add(Loc.Instance.Format("Oiv_Author", first.Info.Author));
            if (!string.IsNullOrWhiteSpace(first.Info.Description))
                lines.Add("\n" + (first.Info.Description!.Length > 400 ? first.Info.Description[..400] + "…" : first.Info.Description));
        }
        else
        {
            lines.Add(Loc.Instance.Format("Oiv_Confirm_Many", items.Count, target.Title));
            foreach (var i in items)
                lines.Add($"• {NameOf(i)}{(string.IsNullOrWhiteSpace(i.Info.Version) ? "" : "  " + i.Info.Version)}  —  {Path.GetFileName(i.Path)}");
            if (items.Select(i => i.Info.Author).Distinct().Count() == 1 && !string.IsNullOrWhiteSpace(first.Info.Author))
                lines.Add(Loc.Instance.Format("Oiv_Author", first.Info.Author));
        }
        if (needed > 0 && free >= 0) lines.Add("\n" + Loc.Instance.Format("Oiv_Space", Size(needed), drive, Size(free)));
        if (confirm && !DialogWindow.Prompt(string.Join("\n", lines), Loc.Instance["Common_Install"], Loc.Instance["Common_Cancel"],
                DialogKind.Question, image: first.Info.Icon))
            return new() { new ModResult(false, "", Cancelled: true) };

        // 4) The same mod file installed here before (same file name) is replaced. Differently named
        //    files — e.g. the parts of a split mod — are always separate mods, never merged.
        var plan = new List<((string Path, OivPackageInfo Info) Item, ModInstallRecord? Replace)>();
        var installed = InstalledModsStore.ForFolder(target.Folder);
        foreach (var i in items)
        {
            var existing = installed.FirstOrDefault(r => OivInstaller.IsSamePackage(r, i.Path));
            if (existing != null && !DialogWindow.Confirm(Loc.Instance.Format("Oiv_Reinstall", NameOf(i)))) continue;
            plan.Add((i, existing));
        }

        // 5) Install, one package after the other, with one progress for each.
        var results = new List<ModResult>();
        for (int n = 0; n < plan.Count; n++)
        {
            var ((path, info), replace) = plan[n];
            string name = NameOf((path, info));
            string? step = plan.Count > 1 ? Loc.Instance.Format("Busy_Package", n + 1, plan.Count) : null;
            try
            {
                if (replace != null)
                {
                    phase?.Report(new InstallPhase("Mods_Uninstalling", null, replace.Source, step, name, info.Icon));
                    var un = await Task.Run(() => ModUninstaller.Uninstall(replace));
                    if (!un.Success) { results.Add(new ModResult(false, UninstallMessage(un))); continue; }
                }

                phase?.Report(new InstallPhase("Oiv_Phase_Install", 0, Path.GetFileName(path), step, name, info.Icon));
                var record = NewRecord(target, path, name);
                var progress = new Progress<OivProgress>(p => phase?.Report(new InstallPhase(
                    p.Copying ? "Oiv_Phase_Copy" : "Oiv_Phase_Install", Percent(p.Done, p.Total),
                    Detail(p.Current, p.Done, p.Total), step, name, info.Icon)));
                await Task.Run(() =>
                {
                    OivInstaller.Apply(path, target.Folder, record, new OivApplyOptions { Progress = progress });
                    InstalledModsStore.Add(record);
                });
                results.Add(Success(record));
            }
            catch (ModInstallException ex)
            {
                Log.Error("OIV install", ex);
                results.Add(new ModResult(false, $"{name}: {InstallMessage(ex)}"));
            }
            catch (Exception ex)
            {
                Log.Error("OIV install", ex);
                results.Add(new ModResult(false, $"{name}: {Loc.Instance["Dl_Failed"]}\n{ex.Message}"));
            }
        }
        if (results.Count == 0) results.Add(new ModResult(false, "", Cancelled: true));
        return results;
    }

    // ---- licensed .mxa ---------------------------------------------------------------------------

    public static async Task<ModResult> InstallPackageAsync(string mxaPath, GameTarget target, Window owner, IProgress<InstallPhase>? phase)
    {
        if (!await Task.Run(() => MxaFile.IsMxaFile(mxaPath)))
            return Fail("Mxa_NotPackage");

        MxaInfo info;
        try { info = await Task.Run(() => MxaFile.ReadInfo(mxaPath)); }
        catch { return Fail("Mxa_Corrupt"); }

        // The package must be for this game (and this GTA V edition, when it names one).
        if (!target.Accepts(info))
            return new ModResult(false, Loc.Instance.Format("Mxa_WrongGame", info.Game, target.Title));
        if (target.IsGtaV && Enum.TryParse<GameEdition>(info.Edition, true, out var ed) && ed != GameEdition.Unknown && ed != target.Edition)
            return new ModResult(false, Loc.Instance.Format("Mxa_WrongEdition", ed == GameEdition.Enhanced ? "GTA V Enhanced" : "GTA V Legacy"));
        if (await Task.Run(() => GameProcess.IsRunning(target.Folder))) return Fail("Mods_GameRunning");

        // 1) A license for this product. Each paid mod is its own product with its own key.
        var license = LicenseStore.ForProduct(info.ProductId);
        if (license == null)
        {
            phase?.Report(new InstallPhase(InstallPhase.Hide));
            var dlg = new LicenseWindow(LicensePurpose.Product, prompt: info.Name, productId: info.ProductId) { Owner = owner };
            if (dlg.ShowDialog() != true)
                return Fail("License_Required");
            ThemeService.Raise(dlg.ActivatedTier); // first license: Free -> Plus
            license = LicenseStore.ForProduct(info.ProductId);
            if (license == null) return Fail("License_Required");
        }

        try
        {
            // 2) The engine (Plus build is enough; a Pro user already has the superset).
            phase?.Report(new InstallPhase("Engine_Downloading", null, null, null, info.Name));
            string engineKey = LicenseStore.Best()?.Key ?? license.Key;
            var engine = await Task.Run(() => EngineModule.EnsureAsync(engineKey, LicenseTier.Plus));

            // 3) The product's content key — the server only issues it if the license covers the product.
            phase?.Report(new InstallPhase("Dl_Verifying", null, null, null, info.Name));
            byte[] contentKey = await Task.Run(() => EngineModule.GetContentKeyAsync(info.ProductId, license.Key));

            // 4) Add-ons edit mods\update\update.rpf: create it now, with visible progress.
            bool needsRpf = target.IsGtaV && info.ModType == MxaModType.Addon;
            int steps = needsRpf && !await Task.Run(() => ModsArchives.IsReady(target.Folder)) ? 3 : 2, at = 0;
            string Step(int i) => Loc.Instance.Format("Busy_Step", i, steps);
            if (steps == 3)
            {
                string? error = await EnsureModsArchiveAsync(target.Folder, ModsArchives.UpdateRpf, phase, Step(++at), info.Name);
                if (error != null) return new ModResult(false, error);
            }

            // 5) Decrypt + install inside the engine, on a worker thread.
            int unpackStep = ++at, installStep = ++at;
            var progress = new Progress<EngineProgress>(p => phase?.Report(new InstallPhase(
                p.Stage == EngineStage.Unpacking ? "Mxa_Phase_Unpack" : "Oiv_Phase_Install",
                Percent(p.Done, p.Total), Detail(p.Current, p.Done, p.Total),
                Step(p.Stage == EngineStage.Unpacking ? unpackStep : installStep), info.Name)));
            phase?.Report(new InstallPhase("Mxa_Phase_Unpack", 0, null, Step(unpackStep), info.Name));
            string work = NewWorkDir("unpack");
            try
            {
                var record = await Task.Run(() =>
                {
                    var rec = engine.InstallPackage(mxaPath, contentKey, target.Folder, info.Game, work, progress);
                    Stamp(rec, target, mxaPath, info.Name);
                    rec.Author ??= string.IsNullOrWhiteSpace(info.Author) ? null : info.Author;
                    InstalledModsStore.Add(rec);
                    // Machine-bound entitlement: the ASI keeps this content active only on this PC.
                    try { Modexa.Core.Drm.EntitlementService.RegisterInstall(target.Folder, rec, info.ProductId); } catch { }
                    return rec;
                });
                return Success(record);
            }
            finally
            {
                CleanupLater(work);
            }
        }
        catch (LicenseGateException ex)
        {
            Log.Error("Install gate", ex);
            return new ModResult(false, LicenseWindow.GateMessage(ex));
        }
        catch (ModInstallException ex)
        {
            Log.Error("Install", ex);
            return new ModResult(false, InstallMessage(ex));
        }
        catch (Exception ex)
        {
            Log.Error("Install", ex);
            return new ModResult(false, $"{Loc.Instance["Dl_Failed"]}\n{ex.Message}");
        }
    }

    // ---- Pro: any other mod ----------------------------------------------------------------------

    /// <summary>
    /// Pro-only: installs an add-on dlc.rpf, a .zip or a folder. Requires the Pro build of the
    /// engine — which the server only gives to PRO licenses.
    /// </summary>
    public static async Task<ModResult> InstallRawAsync(string path, GameTarget target, Window owner, IProgress<InstallPhase>? phase)
    {
        if (await Task.Run(() => GameProcess.IsRunning(target.Folder))) return Fail("Mods_GameRunning");
        var pro = await GetProEngineAsync(owner, phase);
        if (pro == null) return Fail("Pro_Required");
        string title = Path.GetFileName(path.TrimEnd('\\', '/'));

        // A .zip / folder that only holds mod packages (e.g. the parts of a split OIV): install the
        // packages themselves, each as its own mod — never copy them into the game as files.
        var (packages, hasAddon) = await Task.Run(() => LookInside(path));
        if (packages.Count > 0)
        {
            string unpack = NewWorkDir("packages");
            try
            {
                phase?.Report(new InstallPhase("Oiv_Reading", null, null, null, title));
                var files = await Task.Run(() => ExtractPackages(path, packages, unpack));
                var results = await InstallManyAsync(files, target, owner, phase);
                var shown = results.Where(r => !r.Cancelled).ToList();
                return shown.Count == 0 ? new ModResult(false, "", Cancelled: true)
                    : new ModResult(shown.All(r => r.Success), string.Join("\n\n", shown.Select(r => r.Message)), shown.FirstOrDefault()?.Record);
            }
            finally { CleanupLater(unpack); }
        }

        // The same file installed here before: replace it (after asking), like an OIV reinstall.
        string sourceName = Path.GetFileName(path.TrimEnd('\\', '/'));
        var same = InstalledModsStore.ForFolder(target.Folder)
            .FirstOrDefault(r => r.Setup == null && r.ProductId == null && r.ModType != MxaModType.Oiv
                                 && string.Equals(r.Source, sourceName, StringComparison.OrdinalIgnoreCase));
        if (same != null)
        {
            phase?.Report(new InstallPhase(InstallPhase.Hide));
            if (!DialogWindow.Confirm(Loc.Instance.Format("Oiv_Reinstall", DisplayName(same))))
                return new ModResult(false, "", Cancelled: true);
            phase?.Report(new InstallPhase("Mods_Uninstalling", null, same.Source, null, title));
            var un = await Task.Run(() => ModUninstaller.Uninstall(same));
            if (!un.Success) return new ModResult(false, UninstallMessage(un));
        }

        // Add-ons go into mods\update\update.rpf's dlclist: make that copy first, with progress.
        if (hasAddon || Path.GetExtension(path).Equals(".rpf", StringComparison.OrdinalIgnoreCase))
        {
            string? error = await EnsureModsArchiveAsync(target.Folder, ModsArchives.UpdateRpf, phase, null, title);
            if (error != null) return new ModResult(false, error);
        }

        phase?.Report(new InstallPhase("Dl_Installing", null, null, null, title));
        string work = NewWorkDir("raw");
        try
        {
            var record = await Task.Run(() =>
            {
                var rec = pro.InstallRaw(path, target.Folder, "GtaV", work);
                Stamp(rec, target, path, rec.Name);
                InstalledModsStore.Add(rec);
                return rec;
            });
            return Success(record);
        }
        catch (ModInstallException ex)
        {
            Log.Error("Raw install", ex);
            return new ModResult(false, InstallMessage(ex));
        }
        catch (Exception ex)
        {
            Log.Error("Raw install", ex);
            return new ModResult(false, $"{Loc.Instance["Dl_Failed"]}\n{ex.Message}");
        }
        finally
        {
            CleanupLater(work);
        }
    }

    private static readonly string[] PackageExtensions = { ".oiv", ".oivs", ".mxa" };

    /// <summary>Readmes, screenshots and links shipped with a mod (not part of what gets installed).</summary>
    private static bool IsExtra(string name) => Path.GetExtension(name).ToLowerInvariant() is
        ".txt" or ".md" or ".pdf" or ".url" or ".htm" or ".html" or ".rtf" or ".doc" or ".docx"
        or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    /// <summary>
    /// What a .zip / folder holds: its mod packages when it holds nothing else (besides readmes and
    /// pictures), and whether it carries an add-on dlc.rpf.
    /// </summary>
    private static (List<string> Packages, bool HasAddon) LookInside(string path)
    {
        try
        {
            List<string> names;
            if (Directory.Exists(path))
                names = Directory.GetFiles(path, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(path, f)).ToList();
            else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                names = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.FullName).ToList();
            }
            else return (new List<string>(), false);

            var content = names.Where(n => !IsExtra(n)).ToList();
            bool addon = content.Any(n => Path.GetFileName(n).Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase));
            // An extracted OIV (assembly.xml at the top) is one package as a whole: the engine applies it.
            bool extractedOiv = content.Any(n => n.Equals("assembly.xml", StringComparison.OrdinalIgnoreCase) || n.Equals("super.xml", StringComparison.OrdinalIgnoreCase));
            var packages = !extractedOiv && content.Count > 0 && content.All(n => PackageExtensions.Contains(Path.GetExtension(n).ToLowerInvariant()))
                ? content : new List<string>();
            return (packages, addon);
        }
        catch { return (new List<string>(), false); }
    }

    /// <summary>Copies the packages out of a .zip / folder (by file name — the name is the mod's identity).</summary>
    private static List<string> ExtractPackages(string path, IReadOnlyList<string> packages, string dir)
    {
        var result = new List<string>();
        if (Directory.Exists(path))
        {
            // Packages in a folder are installed from where they are.
            result.AddRange(packages.Select(p => Path.Combine(path, p)));
            return result;
        }
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        foreach (var name in packages)
        {
            var entry = zip.GetEntry(name) ?? zip.Entries.First(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
            string dest = Path.Combine(dir, entry.Name);
            if (File.Exists(dest)) dest = Path.Combine(dir, Guid.NewGuid().ToString("N")[..6] + "_" + entry.Name);
            System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, dest);
            result.Add(dest);
        }
        return result;
    }

    /// <summary>The loaded Pro engine, downloading it for an existing PRO key or offering activation.</summary>
    private static async Task<IProModEngine?> GetProEngineAsync(Window owner, IProgress<InstallPhase>? phase)
    {
        if (EngineModule.Load() is IProModEngine loaded) return loaded;

        var best = LicenseStore.Best();
        if (best?.Tier == LicenseTier.Pro)
        {
            try
            {
                phase?.Report("Engine_Downloading");
                var e = await Task.Run(() => EngineModule.EnsureAsync(best.Key, LicenseTier.Pro));
                if (e is IProModEngine p) { ThemeService.Apply(LicenseTier.Pro); return p; }
            }
            catch (LicenseGateException ex)
            {
                DialogWindow.Show($"{Loc.Instance["Pro_EngineFailed"]}\n{LicenseWindow.GateMessage(ex)}", DialogKind.Warning);
                return null;
            }
        }

        phase?.Report(new InstallPhase(InstallPhase.Hide));
        var dlg = new LicenseWindow(LicensePurpose.Pro) { Owner = owner };
        if (dlg.ShowDialog() != true || dlg.ActivatedTier != LicenseTier.Pro) return null;
        ThemeService.Apply(LicenseTier.Pro);
        return EngineModule.Load() as IProModEngine;
    }

    // ---- uninstall (all tiers) -------------------------------------------------------------------

    /// <summary>
    /// Removes mods exactly as they were installed (newest first, e.g. both parts of a split mod).
    /// Returns a message for the user.
    /// </summary>
    public static async Task<ModResult> UninstallAsync(IReadOnlyList<ModInstallRecord> records, IProgress<InstallPhase>? phase = null)
    {
        if (records.Count == 0) return new ModResult(false, "", Cancelled: true);
        if (await Task.Run(() => GameProcess.IsRunning(records[0].GameFolder))) return Fail("Mods_GameRunning");
        var errors = new List<string>();
        long freed = 0;
        try
        {
            var ordered = records.OrderByDescending(r => r.InstalledUtc).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                string title = DisplayName(r);
                string? stepText = ordered.Count > 1 ? Loc.Instance.Format("Busy_Package", i + 1, ordered.Count) : null;
                byte[]? icon = IconBytes(r);
                phase?.Report(new InstallPhase("Mods_Uninstalling", null, r.Source, stepText, title, icon));
                var step = new Progress<string>(s => phase?.Report(new InstallPhase("Mods_Uninstalling", null, s.Replace('/', '\\'), stepText, title, icon)));
                var result = await Task.Run(() => ModUninstaller.Uninstall(r, step));
                freed += result.FreedBytes;
                errors.AddRange(result.Errors.Select(e => ordered.Count > 1 ? $"{title}: {e}" : e));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall", ex);
            return new ModResult(false, $"{Loc.Instance["Dl_Failed"]}\n{ex.Message}");
        }

        if (errors.Count > 0)
            return new ModResult(false, Loc.Instance.Format("Mods_UninstallIncomplete", string.Join("\n", errors.Take(6).Select(e => "• " + e))));
        string done = records.Count == 1
            ? Loc.Instance.Format("Mods_Uninstalled", DisplayName(records[0]))
            : string.Join("\n", records.Select(r => Loc.Instance.Format("Mods_Uninstalled", $"{DisplayName(r)} ({r.Source})")));
        if (freed > 64L * 1024 * 1024) done += "\n\n" + Loc.Instance.Format("Mods_Freed", Size(freed));
        return new ModResult(true, done);
    }

    public static Task<ModResult> UninstallAsync(ModInstallRecord record) => UninstallAsync(new[] { record });

    /// <summary>Installed records that are other parts of the same split mod (same name and author, same game).</summary>
    public static List<ModInstallRecord> PartsOf(ModInstallRecord record)
        => InstalledModsStore.ForFolder(record.GameFolder)
            .Where(r => r.ModType == MxaModType.Oiv && record.ModType == MxaModType.Oiv && r.ProductId == null && r.Setup == null
                        && string.Equals(r.Name, record.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.Author ?? "", record.Author ?? "", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Deletes archive copies Modexa made that no installed mod uses any more.</summary>
    public static async Task<ModResult> FreeUnusedAsync(string gameFolder, IReadOnlyList<UnusedArchive> unused, IProgress<InstallPhase>? phase)
    {
        if (await Task.Run(() => GameProcess.IsRunning(gameFolder))) return Fail("Mods_GameRunning");
        var errors = new List<string>();
        long freed = 0;
        foreach (var u in unused)
        {
            phase?.Report(new InstallPhase("Mods_Uninstalling", null, u.Archive.Replace('/', '\\')));
            try
            {
                await Task.Run(() => ModsArchives.Release(gameFolder, u.Archive));
                freed += u.Bytes;
            }
            catch (Exception ex) { errors.Add($"{u.Archive}: {ex.Message}"); }
        }
        return errors.Count == 0
            ? new ModResult(true, Loc.Instance.Format("Mods_Freed", Size(freed)))
            : new ModResult(false, Loc.Instance.Format("Mods_UninstallIncomplete", string.Join("\n", errors.Take(6).Select(e => "• " + e))));
    }

    private static string UninstallMessage(UninstallResult result)
        => Loc.Instance.Format("Mods_UninstallIncomplete", string.Join("\n", result.Errors.Take(6).Select(e => "• " + e)));

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>The name shown for a record (prepare steps in the current language).</summary>
    public static string DisplayName(ModInstallRecord r) => r.Setup switch
    {
        Core.Prepare.PrepareStep.ModRunner when r.Game != "GtaV" => Loc.Instance["Simple_Step_Runner"],
        Core.Prepare.PrepareStep.ModRunner => Loc.Instance["GtaV_Step_ModRunner"],
        Core.Prepare.PrepareStep.GameConfig => Loc.Instance["GtaV_Step_GameConfig"],
        Core.Prepare.PrepareStep.Menu => Loc.Instance["GtaV_Step_Menu"],
        _ => string.IsNullOrWhiteSpace(r.Name) ? "(mod)" : r.Name
    };

    private static byte[]? IconBytes(ModInstallRecord r)
    {
        try { return r.IconFile != null && File.Exists(r.IconFile) ? File.ReadAllBytes(r.IconFile) : null; }
        catch { return null; }
    }

    private static ModResult Fail(string key) => new(false, Loc.Instance[key]);

    private static int? Percent(long done, long total) => total > 0 ? (int)Math.Clamp(done * 100 / total, 0, 100) : null;

    /// <summary>"x64\audio\sfx\RADIO_02_POP.rpf  ·  3.2 GB / 17.5 GB"</summary>
    private static string? Detail(string? current, long done, long total)
    {
        string bytes = total > 0 ? $"{Size(done)} / {Size(total)}" : "";
        string where = string.IsNullOrEmpty(current) ? "" : current.Replace('/', '\\');
        return where.Length == 0 ? bytes : bytes.Length == 0 ? where : $"{where}  ·  {bytes}";
    }

    /// <summary>Human size, always with Latin digits ("3.2 GB").</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B"
    };

    private static (long free, string drive) FreeSpace(string folder)
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
            return d.IsReady ? (d.AvailableFreeSpace, d.Name.TrimEnd('\\')) : (-1, d.Name);
        }
        catch { return (-1, ""); }
    }

    private static ModInstallRecord NewRecord(GameTarget target, string source, string name) => new()
    {
        Name = name,
        Game = target.MxaKey,
        GameTitle = target.Title,
        GameFolder = target.Folder,
        Source = Path.GetFileName(source.TrimEnd('\\', '/')),
        SourcePath = FullPath(source),
        InstalledUtc = DateTime.UtcNow
    };

    private static string? FullPath(string path)
    {
        try { return Path.GetFullPath(path.TrimEnd('\\', '/')); }
        catch { return null; }
    }

    private static void Stamp(ModInstallRecord rec, GameTarget target, string source, string? name)
    {
        rec.GameTitle = target.Title;
        rec.GameFolder = target.Folder;
        rec.Source = Path.GetFileName(source.TrimEnd('\\', '/'));
        rec.SourcePath = FullPath(source);
        if (string.IsNullOrWhiteSpace(rec.Name) && !string.IsNullOrWhiteSpace(name)) rec.Name = name!;
        rec.InstalledUtc = DateTime.UtcNow;
    }

    /// <summary>"Done", plus anything the installer had to skip (unsupported package parts).</summary>
    private static ModResult Success(ModInstallRecord record)
    {
        // Installed again from the same file: its "disabled" entry (if any) is no longer needed.
        try { DisabledModsStore.RemoveSameSource(record.GameFolder, record.Source); } catch { }
        string done = Loc.Instance.Format("Mods_Installed_Ok", record.Name);
        if (record.Warnings.Count == 0) return new ModResult(true, done, record);
        var shown = record.Warnings.Take(8).Select(w => "• " + w).ToList();
        if (record.Warnings.Count > shown.Count) shown.Add($"… (+{record.Warnings.Count - shown.Count})");
        return new ModResult(true, done + "\n\n" + Loc.Instance.Format("Mods_Warnings", string.Join("\n", shown)), record);
    }

    /// <summary>
    /// Creates mods\&lt;archive&gt; (copy of the game's, converted to OPEN) when it is missing.
    /// Returns a user-facing error, or null when the archive is ready.
    /// </summary>
    private static async Task<string?> EnsureModsArchiveAsync(string gameFolder, string archive, IProgress<InstallPhase>? phase,
        string? step, string? title)
    {
        if (await Task.Run(() => ModsArchives.IsReady(gameFolder, archive))) return null;
        long size = await Task.Run(() => ModsArchives.CopySize(gameFolder, archive));
        string name = archive.Replace('/', '\\');
        phase?.Report(new InstallPhase("Prep_CreatingModsRpf", 0, name, step, title));
        long last = 0;
        var copied = new Progress<long>(n =>
        {
            if (n - last < (16 << 20) && n < size) return;
            last = n;
            phase?.Report(new InstallPhase("Prep_CreatingModsRpf", Percent(n, size), Detail(name, n, size), step, title));
        });
        try
        {
            await Task.Run(() => ModsArchives.EnsureOpen(gameFolder, archive, copied: copied));
            return null;
        }
        catch (ModsArchiveException ex)
        {
            Log.Error("Create mods archive", ex);
            return Loc.Instance.Format("Mods_NotPrepared", ModsArchiveMessages.For(ex));
        }
    }

    private static string InstallMessage(ModInstallException ex) => ex.Code switch
    {
        ModInstallException.NotPrepared => Loc.Instance.Format("Mods_NotPrepared",
            ex.InnerException is ModsArchiveException mae ? ModsArchiveMessages.For(mae) : ex.Message),
        ModInstallException.Unsupported => $"{Loc.Instance["Mods_Unsupported"]}\n{ex.Message}",
        ModInstallException.BadPackage => Loc.Instance.Format("Oiv_Invalid", ex.Message),
        _ => $"{Loc.Instance["Dl_Failed"]}\n{ex.Message}"
    };

    private static string NewWorkDir(string kind)
    {
        string dir = Path.Combine(AppPaths.CacheDir, kind, Guid.NewGuid().ToString("N"));
        AppPaths.EnsureDir(dir);
        return dir;
    }

    private static void CleanupLater(string dir)
        => _ = Task.Run(() => { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } });

    /// <summary>Opens a folder in Windows Explorer.</summary>
    public static void OpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        try { Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{folder}\"", UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Open folder", ex); }
    }
}

/// <summary>Orders "Package.2.oiv" before "Package.10.oiv" (numbers compared by value).</summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

/// <summary>Is the game running from this folder? (Files would be locked mid-install.)</summary>
public static class GameProcess
{
    public static bool IsRunning(string gameFolder)
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new[] { gameFolder, Path.Combine(gameFolder, "bin", "x64") })
                if (Directory.Exists(dir))
                    foreach (var exe in Directory.EnumerateFiles(dir, "*.exe"))
                        names.Add(Path.GetFileNameWithoutExtension(exe));
            // Launchers/updaters can stay open; the game executables can't.
            names.RemoveWhere(n => n.Contains("Launcher", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("LanguageSelect", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("PlayGTAV", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("unins", StringComparison.OrdinalIgnoreCase));
            return Process.GetProcesses().Any(p =>
            {
                try { return names.Contains(p.ProcessName); }
                finally { p.Dispose(); }
            });
        }
        catch { return false; }
    }
}
