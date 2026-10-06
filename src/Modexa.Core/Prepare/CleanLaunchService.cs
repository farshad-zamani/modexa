using System.IO;

namespace Modexa.Core.Prepare;

/// <summary>
/// Toggles a "clean launch" by renaming the mod loader proxy DLLs so the game starts vanilla
/// (safe for GTA Online). Renaming is reversible; the mods\ tree is left untouched.
/// </summary>
public static class CleanLaunchService
{
    // Proxy DLLs that ASI loaders / ReShade / ENB use. Renaming any present ones disables loading.
    private static readonly string[] LoaderDlls =
    {
        "dinput8.dll", "xinput1_4.dll", "dsound.dll", "d3d11.dll", "dxgi.dll", "version.dll"
    };

    private const string DisabledSuffix = ".modexa-off";

    public static bool IsCleanLaunchEnabled(string gameFolder)
    {
        // "Clean" means none of the active loader DLLs are present (they're renamed to .off).
        return LoaderDlls.All(dll => !File.Exists(Path.Combine(gameFolder, dll)));
    }

    /// <summary>Disable all mods for the next launch (rename loaders to .off).</summary>
    public static void EnableCleanLaunch(string gameFolder)
    {
        foreach (var dll in LoaderDlls)
        {
            string active = Path.Combine(gameFolder, dll);
            string off = active + DisabledSuffix;
            try
            {
                if (File.Exists(active))
                {
                    if (File.Exists(off)) File.Delete(off);
                    File.Move(active, off);
                }
            }
            catch { /* skip locked files */ }
        }
    }

    /// <summary>Re-enable mods (restore loader DLLs from .off).</summary>
    public static void DisableCleanLaunch(string gameFolder)
    {
        foreach (var dll in LoaderDlls)
        {
            string active = Path.Combine(gameFolder, dll);
            string off = active + DisabledSuffix;
            try
            {
                if (File.Exists(off))
                {
                    if (File.Exists(active)) File.Delete(active);
                    File.Move(off, active);
                }
            }
            catch { }
        }
    }
}
