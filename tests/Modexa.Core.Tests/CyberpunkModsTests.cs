using System.IO;
using System.IO.Compression;
using Modexa.Core.Format;
using Modexa.Core.Install;
using Xunit;

namespace Modexa.Core.Tests;

public class CyberpunkModsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cpmods_" + Guid.NewGuid().ToString("N"));
    private string Mod => Path.Combine(_root, "mod");
    private string Game => Path.Combine(_root, "game");

    public CyberpunkModsTests()
    {
        Directory.CreateDirectory(Mod);
        Directory.CreateDirectory(Path.Combine(Game, "bin", "x64"));
    }

    public void Dispose()
    {
        foreach (var r in InstalledModsStore.ForFolder(Game)) InstalledModsStore.Remove(r.Id);
        try { Directory.Delete(_root, true); } catch { }
    }

    private void File_(string rel, string text = "x")
    {
        string p = Path.Combine(Mod, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    [Fact]
    public void A_copy_and_paste_mod_is_one_item_and_extras_are_ignored()
    {
        File_(@"Virtual Car Dealer Cyberpunk 2077\archive\pc\mod\vcd.archive");
        File_(@"Virtual Car Dealer Cyberpunk 2077\r6\scripts\vcd.reds");
        File_(@"Virtual Car Dealer Cyberpunk 2077\readme.txt");
        File_(@"preview.png");
        var plan = CyberpunkMods.Plan(Mod, "Pack", "2.5");
        Assert.Equal(CpPlanKind.Ready, plan.Kind);
        var item = Assert.Single(plan.Items);
        Assert.Equal("Virtual Car Dealer Cyberpunk 2077", item.Title);
        Assert.Null(item.Version);
        Assert.Equal(new[] { @"archive\pc\mod\vcd.archive", @"r6\scripts\vcd.reds" }, item.Files.Select(f => f.Dest).OrderBy(d => d));
        Assert.Equal(new[] { "archive", "r6" }, item.Folders);
    }

    [Fact]
    public void A_mod_at_the_very_top_is_named_after_what_was_given()
    {
        File_(@"archive\pc\mod\a.archive");
        var plan = CyberpunkMods.Plan(Mod, "My Download", "2.5");
        Assert.Equal("My Download", Assert.Single(plan.Items).Title);
    }

    private void TwoVersions()
    {
        File_(@"Virtual Car Dealer Cyberpunk 2077\Virtual Car Dealer For v2.5\archive\pc\mod\a25.archive");
        File_(@"Virtual Car Dealer Cyberpunk 2077\Virtual Car Dealer For v2.31\archive\pc\mod\a231.archive");
    }

    [Theory]
    [InlineData("2.5", "a25.archive")]
    [InlineData("2.31", "a231.archive")]
    public void The_folder_for_the_games_patch_is_installed(string patch, string file)
    {
        TwoVersions();
        var plan = CyberpunkMods.Plan(Mod, "Pack", patch);
        Assert.Equal(CpPlanKind.Ready, plan.Kind);
        var item = Assert.Single(plan.Items);
        Assert.Equal("Virtual Car Dealer Cyberpunk 2077", item.Title);
        Assert.Equal(patch, item.Version);
        Assert.Equal(file, Path.GetFileName(Assert.Single(item.Files).Dest));
    }

    [Fact]
    public void A_patch_the_mod_is_not_made_for_installs_nothing_and_says_which_it_is_for()
    {
        TwoVersions();
        var plan = CyberpunkMods.Plan(Mod, "Pack", "2.3");
        Assert.Equal(CpPlanKind.WrongVersion, plan.Kind);
        Assert.Equal(new[] { "2.31", "2.5" }, plan.Versions);
        Assert.Empty(plan.Items);
    }

    [Fact]
    public void An_unknown_patch_asks_instead_of_guessing()
    {
        TwoVersions();
        Assert.Equal(CpPlanKind.NeedsPatch, CyberpunkMods.Plan(Mod, "Pack", null).Kind);
    }

    [Fact]
    public void A_mod_with_no_patch_in_its_names_installs_whatever_the_patch()
    {
        File_(@"Cool Mod v1.2\archive\pc\mod\a.archive"); // v1.2 is the mod's own version
        var plan = CyberpunkMods.Plan(Mod, "Pack", null);
        Assert.Equal(CpPlanKind.Ready, plan.Kind);
        Assert.Null(Assert.Single(plan.Items).Version);
    }

    [Fact]
    public void Sibling_folders_that_differ_only_in_a_version_are_builds_for_patches()
    {
        File_(@"Mod\Main v2.5\archive\pc\mod\a.archive");
        File_(@"Mod\Main v2.31\archive\pc\mod\b.archive");
        var plan = CyberpunkMods.Plan(Mod, "Pack", "2.31");
        Assert.Equal("b.archive", Path.GetFileName(Assert.Single(plan.Items).Files.Single().Dest));
    }

    [Fact]
    public void Something_that_holds_no_game_folders_is_not_a_mod()
    {
        File_(@"Some Pack\readme.txt");
        File_(@"Some Pack\screens\1.png");
        Assert.Equal(CpPlanKind.NoMods, CyberpunkMods.Plan(Mod, "Pack", "2.5").Kind);
    }

    [Fact]
    public void A_mod_that_replaces_the_games_own_files_is_refused()
    {
        File_(@"Evil\archive\pc\content\basegame_1_engine.archive");
        File_(@"Evil2\bin\x64\Cyberpunk2077.exe");
        Assert.Equal(CpPlanKind.NoMods, CyberpunkMods.Plan(Mod, "Pack", "2.5").Kind);
    }

    [Fact]
    public void Redmods_and_wrapped_mods_keep_their_own_layout()
    {
        File_(@"mods\MyRedMod\info.json");
        File_(@"mods\MyRedMod\archives\x.archive");
        File_(@"Pack\mods\Wrapped\archive\pc\mod\w.archive");
        var plan = CyberpunkMods.Plan(Mod, "Pack", "2.5");
        Assert.Equal(CpPlanKind.Ready, plan.Kind);
        Assert.Contains(plan.Items.SelectMany(i => i.Files), f => f.Dest == @"mods\MyRedMod\info.json");
        Assert.Contains(plan.Items.SelectMany(i => i.Files), f => f.Dest == @"archive\pc\mod\w.archive");
    }

    [Fact]
    public void Several_unversioned_folders_are_listed_separately_for_the_user_to_choose()
    {
        File_(@"Mod\Main\archive\pc\mod\a.archive");
        File_(@"Mod\Optional\archive\pc\mod\b.archive");
        var plan = CyberpunkMods.Plan(Mod, "Pack", "2.5");
        Assert.Equal(new[] { "Main", "Optional" }, plan.Items.Select(i => i.Title).OrderBy(t => t));
    }

    [Fact]
    public void Installing_copies_into_the_game_and_removing_puts_everything_back()
    {
        File_(@"M\archive\pc\mod\a.archive", "mod");
        File_(@"M\bin\x64\plugins\cyber_engine_tweaks\mods\X\init.lua", "lua");
        File.WriteAllText(Path.Combine(Game, "bin", "x64", "global.ini"), "original");
        File_(@"M\bin\x64\global.ini", "from mod");

        var item = CyberpunkMods.Plan(Mod, "Pack", "2.5").Items.Single();
        item.SourceName = "m.zip";
        var record = CyberpunkMods.Install(item, Game);
        InstalledModsStore.Add(record);

        Assert.Equal("mod", File.ReadAllText(Path.Combine(Game, "archive", "pc", "mod", "a.archive")));
        Assert.Equal("from mod", File.ReadAllText(Path.Combine(Game, "bin", "x64", "global.ini")));
        Assert.Equal("Cp2077", record.Game);
        Assert.Equal(ModKinds.Cet, record.KindLabel);

        Assert.True(ModUninstaller.Uninstall(record).Success);
        Assert.False(File.Exists(Path.Combine(Game, "archive", "pc", "mod", "a.archive")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Game, "bin", "x64", "global.ini")));
        Assert.False(Directory.Exists(Path.Combine(Game, "archive")));
    }

    [Fact]
    public void A_zip_is_unpacked_and_read_like_a_folder()
    {
        File_(@"Zipped Mod\archive\pc\mod\z.archive");
        string zip = Path.Combine(_root, "zipped.zip");
        ZipFile.CreateFromDirectory(Mod, zip);
        string dir = CyberpunkMods.Unpack(zip, Path.Combine(_root, "work"));
        var plan = CyberpunkMods.Plan(dir, "zipped", "2.5");
        Assert.Equal("Zipped Mod", Assert.Single(plan.Items).Title);
    }

    [Fact]
    public void A_zip_inside_a_zip_is_read_too()
    {
        string inner = Path.Combine(_root, "inner.zip");
        string stage = Path.Combine(_root, "stage");
        Directory.CreateDirectory(Path.Combine(stage, "Inner Mod", "r6", "scripts"));
        File.WriteAllText(Path.Combine(stage, "Inner Mod", "r6", "scripts", "i.reds"), "x");
        ZipFile.CreateFromDirectory(stage, inner);
        File_("readme.txt");
        File.Copy(inner, Path.Combine(Mod, "inner.zip"));
        string outer = Path.Combine(_root, "outer.zip");
        ZipFile.CreateFromDirectory(Mod, outer);
        var plan = CyberpunkMods.Plan(CyberpunkMods.Unpack(outer, Path.Combine(_root, "work")), "outer", "2.5");
        Assert.Equal("Inner Mod", Assert.Single(plan.Items).Title);
    }
}
