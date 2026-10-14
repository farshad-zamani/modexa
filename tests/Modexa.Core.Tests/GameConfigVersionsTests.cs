using Modexa.Core.Prepare;
using Xunit;

namespace Modexa.Core.Tests;

/// <summary>The gameconfig made for the game's build is picked from the pack (newest at the top, the rest in "Old Versions").</summary>
public class GameConfigVersionsTests
{
    private const string Root = "Game Config For Add-on Mods Gta V/GTA V Legacy/2/";

    // The real layout of the WTMod Game-Config pack (one variant each is enough here).
    private static readonly string[] Folders =
    {
        "Gta Config v37 for v 1.0.3889",
        "Old Versions/Gta Config v36 for v 1.0.3717",
        "Old Versions/Gta Config v35 for v 1.0.35",
        "Old Versions/Gta Config v34.5 for v 1.0.34",
        "Old Versions/Gta Config v34 for v 1.0.3407",
        "Old Versions/Gta Config v33 for v 1.0.32._",
        "Old Versions/Gta Config v32.5 for v 1.0.3,,,",
        "Old Versions/Gta Config v32 for v 1.0.3095",
        "Old Versions/Gta Config v31 for v 1.0.29",
        "Old Versions/Gta Config v25 for v 1.0.2245.0/Gta Config v25 for v 1.0.2245.0",
        "Old Versions/Gta Config v21 for v 1.0.2060",
    };

    private static readonly List<string> Files = Folders
        .SelectMany(f => new[]
        {
            $"{Root}{f}/For More Mods/Stock Traffic (Means Gta base)/gameconfig.xml",
            $"{Root}{f}/For Less Mods/2x traffic/gameconfig.xml",
        })
        .ToList();

    private static readonly GameConfigSpec Spec = new() { VersionsRoot = Root, Exclude = { "For Less Mods" } };

    [Theory]
    [InlineData(3889, "Gta Config v37 for v 1.0.3889", "3889")]
    [InlineData(3407, "Old Versions/Gta Config v34 for v 1.0.3407", "3407")]   // exact before "34xx"
    [InlineData(3411, "Old Versions/Gta Config v34.5 for v 1.0.34", "34xx")]  // "1.0.34" = every 34xx build
    [InlineData(3586, "Old Versions/Gta Config v35 for v 1.0.35", "35xx")]
    [InlineData(3258, "Old Versions/Gta Config v33 for v 1.0.32._", "32xx")]
    [InlineData(3095, "Old Versions/Gta Config v32 for v 1.0.3095", "3095")]
    [InlineData(2944, "Old Versions/Gta Config v31 for v 1.0.29", "29xx")]
    [InlineData(2245, "Old Versions/Gta Config v25 for v 1.0.2245.0", "2245")] // outer folder, not the repeated inner one
    [InlineData(2060, "Old Versions/Gta Config v21 for v 1.0.2060", "2060")]
    public void Picks_the_config_made_for_the_build(int build, string folder, string forBuild)
    {
        var pick = GameConfigVersions.Pick(Files, Spec, build)!;
        Assert.Equal(Root + folder, pick.Folder);
        Assert.Equal(forBuild, pick.ForBuild);
        Assert.True(pick.Matches);
    }

    [Fact]
    public void A_newer_build_gets_the_newest_config_with_a_warning()
    {
        var pick = GameConfigVersions.Pick(Files, Spec, 4100)!;
        Assert.Equal(Root + "Gta Config v37 for v 1.0.3889", pick.Folder);
        Assert.False(pick.Matches);
    }

    [Fact]
    public void Less_mods_variants_are_not_offered()
    {
        var pick = GameConfigVersions.Pick(Files, Spec, 3407)!;
        var variants = PrepareService.GameConfigVariants(Files, pick.Folder)
            .Where(v => !Spec.Exclude.Any(x => v.Contains(x, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.Equal(new[] { "For More Mods/Stock Traffic (Means Gta base)" }, variants);
    }
}
