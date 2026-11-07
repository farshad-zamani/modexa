using Modexa.Core.Games;
using Modexa.Core.Prepare;
using Xunit;

namespace Modexa.Core.Tests;

public class CyberpunkPatchTests
{
    [Theory]
    [InlineData("Mod Runner CP2077 v2.31", "2.31")]
    [InlineData("Mod Runner CP2077 v2.5", "2.5")]
    [InlineData("Virtual Car Dealer For v2.5", "2.5")]
    [InlineData("Virtual Car Dealer v1.0.2 For v2.31", "2.31")]
    [InlineData("Cyberpunk 2077", null)]
    [InlineData("v2.50", "2.5")]
    public void The_patch_is_read_from_a_folder_name(string name, string? patch)
        => Assert.Equal(patch, CyberpunkPatch.Parse(name));

    [Fact]
    public void Patches_compare_as_patches_not_as_text()
    {
        Assert.True(CyberpunkPatch.Same("2.5", "2.50"));
        Assert.False(CyberpunkPatch.Same("2.3", "2.31"));
        Assert.True(CyberpunkPatch.Compare("2.5", "2.31") > 0);
        Assert.True(CyberpunkPatch.Compare("2.31", "2.3") > 0);
        Assert.True(CyberpunkPatch.Compare("2.3", "2.21") > 0);
    }

    private static readonly string[] Pack =
    {
        "Mod Runner Pack Cyberpunk 2077 Core Mods/Mod Runner CP2077 v2.31/bin/x64/version.dll",
        "Mod Runner Pack Cyberpunk 2077 Core Mods/Mod Runner CP2077 v2.31/r6/scripts/a.reds",
        "Mod Runner Pack Cyberpunk 2077 Core Mods/Mod Runner CP2077 v2.5/bin/x64/version.dll",
        "Mod Runner Pack Cyberpunk 2077 Core Mods/Mod Runner CP2077 v2.5/red4ext/RED4ext.dll",
    };

    private static BundleEntry Runner() => new()
    {
        RequireVersionMatch = true, ExactVersion = true,
        Maps = { new BundleMap { From = "Mod Runner Pack Cyberpunk 2077 Core Mods/*/", To = "" } }
    };

    [Theory]
    [InlineData("2.5", "v2.5")]
    [InlineData("2.31", "v2.31")]
    public void The_runner_folder_of_the_games_patch_is_used(string patch, string folder)
    {
        var (maps, warning) = PrepareService.ResolveMaps(Runner(), Pack, patch);
        Assert.Null(warning);
        Assert.EndsWith(folder + "/", maps.Single().From);
    }

    [Fact]
    public void A_patch_between_two_folders_installs_nothing_and_names_the_ones_there_are()
    {
        var ex = Assert.Throws<PrepareVersionException>(() => PrepareService.ResolveMaps(Runner(), Pack, "2.3")); // not 2.31
        Assert.Equal("2.3", ex.Detected);
        Assert.Equal(new[] { "2.31", "2.5" }, ex.Available);
    }

    [Fact]
    public void An_unknown_patch_never_picks_a_runner_for_you()
    {
        var ex = Assert.Throws<PrepareVersionException>(() => PrepareService.ResolveMaps(Runner(), Pack, null));
        Assert.Equal("", ex.Detected);
        Assert.Equal(new[] { "2.31", "2.5" }, ex.Available);
    }

    [Fact]
    public void A_new_version_folder_in_the_pack_is_found_without_an_app_update()
    {
        var pack = Pack.Append("Mod Runner Pack Cyberpunk 2077 Core Mods/Mod Runner CP2077 v2.6/bin/x64/version.dll").ToArray();
        var (maps, _) = PrepareService.ResolveMaps(Runner(), pack, "2.6");
        Assert.EndsWith("v2.6/", maps.Single().From);
    }
}
