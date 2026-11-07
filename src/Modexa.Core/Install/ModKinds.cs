using Modexa.Core.Prepare;

namespace Modexa.Core.Install;

/// <summary>
/// The kinds an installed mod is filed under in the installed lists (filter chips and row tags).
/// One mod can have more than one: an OpenIV package that brings an add-on is both "OIV Setup" and "Add-on".
/// </summary>
public static class ModKinds
{
    public const string CoreMods = "Core Mods";   // Mod Runner and GameConfig
    public const string OivSetup = "OIV Setup";
    public const string AddOn = "Add-on";
    public const string Modexa = "Modexa";
    public const string Script = "Script";
    public const string Asi = "ASI";
    public const string Plugin = "Plugin";
    public const string Manual = "Manual";
    public const string Files = "Files";

    // Cyberpunk 2077
    public const string Cet = "CET";
    public const string Red4ext = "RED4ext";
    public const string TweakXl = "TweakXL";
    public const string Archive = "Archive";
    public const string RedMod = "REDmod";

    /// <summary>The kind of a prepare step: the core of mod loading, or a script-type tool.</summary>
    public static string OfStep(PrepareStep step)
        => step is PrepareStep.Menu or PrepareStep.AddonSpawner ? Script : CoreMods;

    /// <summary>Chip order in the lists.</summary>
    public static int Order(string kind)
    {
        int i = Array.IndexOf(new[] { OivSetup, AddOn, Modexa, Script, Asi, Cet, Red4ext, TweakXl, Archive, RedMod, Plugin, Manual, Files, CoreMods }, kind);
        return i < 0 ? 50 : i;
    }

    /// <summary>Labels stored by an older version, in today's words.</summary>
    public static string Current(string kind) => kind == "OIV" ? OivSetup : kind;
}
