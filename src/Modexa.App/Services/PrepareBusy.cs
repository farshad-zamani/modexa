using Modexa.Core.I18n;
using Modexa.Core.Prepare;

namespace Modexa.App.Services;

/// <summary>Turns prepare-step progress into the busy card's phase, step ("Step 2 of 3") and detail line.</summary>
public static class PrepareBusy
{
    /// <param name="copyStep">The step also makes the editable mods\update\update.rpf (GTA V GameConfig).</param>
    public static InstallPhase Phase(PrepareProgress p, bool copyStep)
    {
        int steps = copyStep ? 3 : 2;
        int step = p.Phase switch
        {
            PreparePhase.Downloading or PreparePhase.Verifying => 1,
            PreparePhase.PreparingArchive => 2,
            _ => steps
        };
        string key = p.Phase switch
        {
            PreparePhase.Downloading => p.Cached ? "Dl_UsingCached" : "Dl_Downloading",
            PreparePhase.Verifying => "Dl_Verifying",
            PreparePhase.PreparingArchive => "Prep_CreatingModsRpf",
            PreparePhase.Extracting => "Dl_Extracting",
            PreparePhase.Installing => "Dl_Installing",
            _ => "Common_Done"
        };
        string? detail = p.Total > 0
            ? $"{p.Item}  ·  {ModInstallService.Size(p.Done)} / {ModInstallService.Size(p.Total)}".TrimStart(' ', '·')
            : p.Item;
        return new InstallPhase(key, p.Percent, detail, Loc.Instance.Format("Busy_Step", step, steps));
    }
}
