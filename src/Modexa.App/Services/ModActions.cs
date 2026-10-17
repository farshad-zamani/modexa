using System.IO;
using System.Windows;
using Modexa.App.Views;
using Modexa.Core.Diagnostics;
using Modexa.Core.Format;
using Modexa.Core.Games;
using Modexa.Core.I18n;
using Modexa.Core.Install;
using WinForms = System.Windows.Forms;

namespace Modexa.App.Services;

public enum ModAction { Remove, Disable, Enable }

/// <summary>
/// Remove / disable / enable for the installed-mods lists, one mod or many at once.
///
/// Disabling is an exact uninstall (exactly what "Remove" does, so the game is truly back to how it
/// was without the mod) that remembers the mod file; enabling installs it again from that file.
/// Both reuse the tested install and uninstall paths — nothing is hidden or renamed inside the game
/// folder — which makes it a safe way to find which mod causes a problem: turn mods off, test, turn
/// them back on.
/// </summary>
public static class ModActions
{
    private static bool _running;

    /// <summary>True while an action runs (pages don't start another one meanwhile).</summary>
    public static bool IsRunning => _running;

    /// <summary>Runs an action with the page's busy overlay. Returns true when something changed.</summary>
    /// <param name="single">Started from one row's button (asks about a split mod's other parts).</param>
    public static async Task<bool> RunAsync(ModAction action, IReadOnlyList<ModItem> items, bool single, IInstallHost? host, Window owner)
    {
        if (_running || items.Count == 0) return false;
        _running = true;
        bool busy = true;
        var phase = new Progress<InstallPhase>(p =>
        {
            if (!busy) return; // a late report after completion must not re-open the overlay
            if (p.Key == InstallPhase.Hide) host?.HideBusy();
            else host?.ShowBusy(p);
        });
        try
        {
            ModResult? result = action switch
            {
                ModAction.Remove => await RemoveAsync(items, single, phase),
                ModAction.Disable => await DisableAsync(items.Where(i => i.CanDisable).Select(i => i.Record!).ToList(), single, phase),
                _ => await EnableAsync(items.Where(i => i.Disabled != null).Select(i => i.Disabled!).ToList(), single, owner, phase)
            };
            busy = false;
            host?.HideBusy();
            if (result == null) return false;
            if (!result.Cancelled && result.Message.Length > 0)
                DialogWindow.Show(result.Message, result.Success ? DialogKind.Success : DialogKind.Warning);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Mod action {action}", ex);
            busy = false;
            host?.HideBusy();
            DialogWindow.Show($"{Loc.Instance["Dl_Failed"]}\n{ex.Message}", DialogKind.Error);
            return true;
        }
        finally
        {
            busy = false;
            host?.HideBusy();
            _running = false;
        }
    }

    // ---- remove ------------------------------------------------------------------------------------

    private static async Task<ModResult?> RemoveAsync(IReadOnlyList<ModItem> items, bool single, IProgress<InstallPhase> phase)
    {
        List<ModInstallRecord> records;
        List<DisabledMod> forget;
        if (single)
        {
            var item = items[0];
            if (item.Record != null)
            {
                records = await ModInstallPanel.ConfirmUninstallAsync(item.Record) ?? new();
                if (records.Count == 0) return null;
                forget = new();
            }
            else
            {
                var d = item.Disabled!;
                if (!DialogWindow.Prompt(Loc.Instance.Format("Mods_Forget_Confirm", d.Name), Loc.Instance["Mods_Uninstall"],
                        Loc.Instance["Common_Cancel"], DialogKind.Question, image: IconBytes(d.IconFile)))
                    return null;
                records = new();
                forget = new() { d };
            }
        }
        else
        {
            if (!DialogWindow.Confirm(Loc.Instance.Format("Mods_RemoveSelected_Confirm", items.Count, List(items.Select(Title))), danger: true))
                return null;
            records = items.Where(i => i.Record != null).Select(i => i.Record!).ToList();
            forget = items.Where(i => i.Disabled != null).Select(i => i.Disabled!).ToList();
        }

        foreach (var d in forget) DisabledModsStore.Remove(d.Id);
        if (records.Count == 0)
            return new ModResult(true, string.Join("\n", forget.Select(d => Loc.Instance.Format("Mods_Removed_Disabled", d.Name))));
        var result = await ModInstallService.UninstallAsync(records, phase);
        if (forget.Count == 0 || !result.Success) return result;
        return result with { Message = result.Message + "\n" + string.Join("\n", forget.Select(d => Loc.Instance.Format("Mods_Removed_Disabled", d.Name))) };
    }

    // ---- disable -----------------------------------------------------------------------------------

    private static async Task<ModResult?> DisableAsync(List<ModInstallRecord> records, bool single, IProgress<InstallPhase> phase)
    {
        if (records.Count == 0) return null;
        if (single)
        {
            var record = records[0];
            var parts = await Task.Run(() => ModInstallService.PartsOf(record));
            string name = ModInstallService.DisplayName(record);
            byte[]? icon = IconBytes(record.IconFile);
            if (parts.Count > 1)
            {
                string list = string.Join("\n", parts.OrderBy(p => p.Source, NaturalComparer.Instance).Select(p => "• " + (p.Source ?? p.Name)));
                int pick = DialogWindow.Ask(Loc.Instance.Format("Mods_Disable_Parts", name, parts.Count, list),
                    new[] { Loc.Instance["Mods_Uninstall_ThisPart"], Loc.Instance.Format("Mods_Uninstall_AllParts", parts.Count), Loc.Instance["Common_Cancel"] },
                    image: icon);
                if (pick is not (0 or 1)) return null;
                if (pick == 1) records = parts;
            }
            else if (!DialogWindow.Prompt(Loc.Instance.Format("Mods_Disable_Confirm", name), Loc.Instance["Mods_Disable"],
                         Loc.Instance["Common_Cancel"], DialogKind.Question, image: icon))
                return null;
        }
        else if (!DialogWindow.Confirm(Loc.Instance.Format("Mods_DisableSelected_Confirm", records.Count,
                     List(records.Select(ModInstallService.DisplayName)))))
            return null;

        string folder = records[0].GameFolder;
        if (await Task.Run(() => GameProcess.IsRunning(folder))) return new ModResult(false, Loc.Instance["Mods_GameRunning"]);

        // The mod file is needed to turn the mod on again: find it (or let the user point to it) first.
        var plan = new List<(ModInstallRecord Record, string Path)>();
        var skipped = new List<string>();
        foreach (var r in records.OrderByDescending(r => r.InstalledUtc))
        {
            string? path = r.SourcePath != null && (File.Exists(r.SourcePath) || Directory.Exists(r.SourcePath)) ? r.SourcePath : null;
            if (path == null)
            {
                phase.Report(new InstallPhase(InstallPhase.Hide));
                path = AskForFile(Loc.Instance.Format("Mods_Disable_NoSource", ModInstallService.DisplayName(r), r.Source ?? ""), r.Source);
            }
            if (path == null) { skipped.Add(ModInstallService.DisplayName(r)); continue; }
            plan.Add((r, path));
        }
        if (plan.Count == 0) return skipped.Count > 0 ? new ModResult(false, "", Cancelled: true) : null;

        var done = new List<string>();
        var errors = new List<string>();
        for (int i = 0; i < plan.Count; i++)
        {
            var (r, path) = plan[i];
            string title = ModInstallService.DisplayName(r);
            string? step = plan.Count > 1 ? Loc.Instance.Format("Busy_Package", i + 1, plan.Count) : null;
            byte[]? icon = IconBytes(r.IconFile);
            phase.Report(new InstallPhase("Mods_Disabling", null, r.Source, step, title, icon));

            var entry = new DisabledMod
            {
                Name = title,
                ModType = r.ModType,
                Kind = r.KindLabel,
                Game = r.Game,
                GameTitle = r.GameTitle,
                GameFolder = r.GameFolder,
                ProductId = r.ProductId,
                Source = r.Source ?? Path.GetFileName(path),
                Version = r.Version,
                Author = r.Author,
                OriginalPath = path,
                InstalledUtc = r.InstalledUtc
            };
            entry.KeptPath = await Task.Run(() => DisabledModsStore.Keep(entry.Id, path));
            entry.IconFile = DisabledModsStore.KeepIcon(entry.Id, r.IconFile);

            var stepProgress = new Progress<string>(s => phase.Report(new InstallPhase("Mods_Disabling", null, s.Replace('/', '\\'), step, title, icon)));
            var un = await Task.Run(() => ModUninstaller.Uninstall(r, stepProgress));
            if (un.Success)
            {
                DisabledModsStore.Add(entry);
                done.Add(Loc.Instance.Format("Mods_Disabled_Ok", title));
            }
            else
            {
                DisabledModsStore.Remove(entry.Id); // nothing was turned off: keep nothing
                errors.AddRange(un.Errors.Select(e => plan.Count > 1 ? $"{title}: {e}" : e));
            }
        }

        if (errors.Count > 0)
            return new ModResult(false, Loc.Instance.Format("Mods_UninstallIncomplete", string.Join("\n", errors.Take(6).Select(e => "• " + e))));
        return new ModResult(true, string.Join("\n", done));
    }

    // ---- enable ------------------------------------------------------------------------------------

    private static async Task<ModResult?> EnableAsync(List<DisabledMod> mods, bool single, Window owner, IProgress<InstallPhase> phase)
    {
        if (mods.Count == 0) return null;
        if (single)
        {
            var parts = await Task.Run(() => DisabledModsStore.PartsOf(mods[0]));
            if (parts.Count > 1)
            {
                string list = string.Join("\n", parts.OrderBy(p => p.Source, NaturalComparer.Instance).Select(p => "• " + (p.Source ?? p.Name)));
                int pick = DialogWindow.Ask(Loc.Instance.Format("Mods_Enable_Parts", mods[0].Name, parts.Count, list),
                    new[] { Loc.Instance["Mods_Uninstall_ThisPart"], Loc.Instance.Format("Mods_Uninstall_AllParts", parts.Count), Loc.Instance["Common_Cancel"] },
                    image: IconBytes(mods[0].IconFile));
                if (pick is not (0 or 1)) return null;
                if (pick == 1) mods = parts;
            }
        }

        // In the order they were first installed (part 1 before part 2, a base mod before its patch).
        var ordered = mods.OrderBy(m => m.InstalledUtc).ThenBy(m => m.Source, NaturalComparer.Instance).ToList();
        var results = new List<ModResult>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            // Installed again meanwhile (from the same file): nothing to do but forget the entry.
            if (InstalledModsStore.ForFolder(m.GameFolder).Any(r => string.Equals(r.Source, m.Source, StringComparison.OrdinalIgnoreCase)))
            {
                DisabledModsStore.Remove(m.Id);
                continue;
            }
            string? path = m.AvailablePath();
            if (path == null)
            {
                phase.Report(new InstallPhase(InstallPhase.Hide));
                path = AskForFile(Loc.Instance.Format("Mods_Enable_Missing", m.Name, m.Source ?? ""), m.Source);
                if (path == null) continue;
            }

            phase.Report(new InstallPhase("Mods_Enabling", null, m.Source,
                ordered.Count > 1 ? Loc.Instance.Format("Busy_Package", i + 1, ordered.Count) : null, m.Name, IconBytes(m.IconFile)));
            var target = TargetFor(m.Game, m.GameFolder, m.GameTitle);
            var r = await ModInstallService.InstallManyAsync(new[] { path }, target, owner, phase, confirm: false);
            results.AddRange(r);
            if (r.Count > 0 && r.All(x => x.Success))
            {
                // The new record points at the user's own file again (Modexa's link goes away now).
                foreach (var rec in r.Select(x => x.Record).Where(x => x != null))
                {
                    if (m.OriginalPath != null && File.Exists(m.OriginalPath)) rec!.SourcePath = m.OriginalPath;
                    InstalledModsStore.Update(new[] { rec! });
                }
                DisabledModsStore.Remove(m.Id);
            }
        }

        var shown = results.Where(r => !r.Cancelled).ToList();
        if (shown.Count == 0) return results.Count > 0 ? new ModResult(false, "", Cancelled: true) : new ModResult(true, "");
        return new ModResult(shown.All(r => r.Success), string.Join("\n\n", shown.Select(r => r.Message)));
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>The game a disabled mod goes back into.</summary>
    public static GameTarget TargetFor(string gameKey, string folder, string? title)
    {
        GameId id = gameKey switch
        {
            "GtaV" => GameCatalog.ForEdition(GameDetector.EditionOf(folder)),
            "GtaSa" => GameId.GtaSanAndreas,
            "GtaIV" => GameId.GtaIV,
            "Rdr1" => GameId.RedDeadRedemption1,
            "Rdr2" => GameId.RedDeadRedemption2,
            "Cp2077" => GameId.Cyberpunk2077,
            _ => Enum.TryParse<GameId>(gameKey, true, out var g) ? g : GameId.GtaV
        };
        var edition = id switch { GameId.GtaVEnhanced => GameEdition.Enhanced, GameId.GtaV => GameEdition.Legacy, _ => GameEdition.Unknown };
        return new GameTarget(id, folder, string.IsNullOrWhiteSpace(title) ? GameCatalog.Get(id).DisplayName : title!, edition);
    }

    private static string Title(ModItem i) => i.Record != null ? ModInstallService.DisplayName(i.Record) : i.Disabled!.Name;

    private static string List(IEnumerable<string> names)
    {
        var all = names.ToList();
        var shown = all.Take(10).Select(n => "• " + n).ToList();
        if (all.Count > shown.Count) shown.Add($"… (+{all.Count - shown.Count})");
        return string.Join("\n", shown);
    }

    /// <summary>Tells why, then lets the user pick the mod file (null when cancelled).</summary>
    private static string? AskForFile(string message, string? fileName)
    {
        if (!DialogWindow.Prompt(message, Loc.Instance["Mods_ChooseFile"], Loc.Instance["Common_Cancel"], DialogKind.Question))
            return null;
        string ext = Path.GetExtension(fileName ?? "");
        string filter = ext.Length > 0 ? $"{fileName}|*{ext}|*.*|*.*" : "*.*|*.*";
        using var dlg = new WinForms.OpenFileDialog { Filter = filter, Title = Loc.Instance["Mods_ChooseFile"], FileName = fileName ?? "" };
        return dlg.ShowDialog() == WinForms.DialogResult.OK ? dlg.FileName : null;
    }

    private static byte[]? IconBytes(string? file)
    {
        try { return file != null && File.Exists(file) ? File.ReadAllBytes(file) : null; }
        catch { return null; }
    }
}
