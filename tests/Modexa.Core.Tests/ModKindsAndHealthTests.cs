using System.IO;
using System.Text;
using Modexa.Core.Format;
using Modexa.Core.Install;
using Modexa.Core.Prepare;
using Modexa.Core.Rpf;
using Xunit;

namespace Modexa.Core.Tests;

public class ModKindsAndHealthTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "kinds_" + Guid.NewGuid().ToString("N"));

    public ModKindsAndHealthTests() => Directory.CreateDirectory(_game);

    public void Dispose()
    {
        try { Directory.Delete(_game, true); } catch { }
    }

    [Fact]
    public void An_oiv_that_brings_an_add_on_is_filed_under_both()
    {
        var oiv = new ModInstallRecord { ModType = MxaModType.Oiv, Source = "bugatti.oiv" };
        Assert.Equal(new[] { ModKinds.OivSetup }, oiv.Kinds);

        oiv.ArchiveEdits.Add(new ArchiveEdit { Archive = "update/update.rpf", Entry = "x64/dlcpacks/bugatti/dlc.rpf", WasCreated = true });
        Assert.Equal(new[] { ModKinds.OivSetup, ModKinds.AddOn }, oiv.Kinds);
        Assert.Equal("OIV Setup", oiv.KindLabel);

        var onDisk = new ModInstallRecord { ModType = MxaModType.Oiv, Source = "x.oiv" };
        onDisk.Files.Add(new InstalledFile { Dest = @"mods\update\x64\dlcpacks\audi\dlc.rpf", WasCreated = true });
        Assert.Contains(ModKinds.AddOn, onDisk.Kinds);
    }

    [Fact]
    public void Only_the_mod_runner_and_gameconfig_are_core_mods_menyoo_and_the_spawner_are_scripts()
    {
        Assert.Equal(new[] { ModKinds.CoreMods }, new ModInstallRecord { Setup = PrepareStep.ModRunner }.Kinds);
        Assert.Equal(new[] { ModKinds.CoreMods }, new ModInstallRecord { Setup = PrepareStep.GameConfig }.Kinds);
        Assert.Equal(new[] { ModKinds.Script }, new ModInstallRecord { Setup = PrepareStep.Menu }.Kinds);
        Assert.Equal(new[] { ModKinds.Script }, new ModInstallRecord { Setup = PrepareStep.AddonSpawner }.Kinds);
    }

    [Theory]
    [InlineData(@"scripts\Cool.dll", ModKinds.Script)]
    [InlineData(@"Trainer.asi", ModKinds.Asi)]
    [InlineData(@"plugins\x.dll", ModKinds.Plugin)]
    [InlineData(@"readme.txt", ModKinds.Files)]
    public void Loose_files_are_named_by_what_they_are_not_Files(string dest, string kind)
    {
        var r = new ModInstallRecord { ModType = MxaModType.Loose, Source = "m.zip" };
        r.Files.Add(new InstalledFile { Dest = dest, WasCreated = true });
        Assert.Equal(kind, r.KindLabel);
    }

    [Theory]
    [InlineData(@"bin\x64\plugins\cyber_engine_tweaks\mods\X\init.lua", ModKinds.Cet)]
    [InlineData(@"red4ext\plugins\X\x.dll", ModKinds.Red4ext)]
    [InlineData(@"r6\scripts\x.reds", ModKinds.Script)]
    [InlineData(@"r6\tweaks\x.yaml", ModKinds.TweakXl)]
    [InlineData(@"archive\pc\mod\x.archive", ModKinds.Archive)]
    [InlineData(@"mods\MyMod\info.json", ModKinds.RedMod)]
    public void Cyberpunk_mods_are_named_by_where_their_files_go(string dest, string kind)
    {
        var r = new ModInstallRecord { Game = "Cp2077", ModType = MxaModType.Loose, Source = "m.zip" };
        r.Files.Add(new InstalledFile { Dest = dest, WasCreated = true });
        Assert.Equal(kind, r.KindLabel);
    }

    [Fact]
    public void A_purchased_mod_is_premium()
    {
        Assert.True(new ModInstallRecord { ProductId = "CAR1", Source = "car.mxa" }.IsPremium);
        Assert.False(new ModInstallRecord { Source = "x.oiv" }.IsPremium);
    }

    [Fact]
    public void Files_that_are_gone_make_a_mod_missing_or_partly_missing()
    {
        File.WriteAllText(Path.Combine(_game, "a.asi"), "a");
        File.WriteAllText(Path.Combine(_game, "b.asi"), "b");
        var r = new ModInstallRecord { GameFolder = _game, ModType = MxaModType.Loose };
        r.Files.Add(new InstalledFile { Dest = "a.asi", WasCreated = true });
        r.Files.Add(new InstalledFile { Dest = "b.asi", WasCreated = true });
        Assert.Equal(InstallHealth.Ok, InstallChecks.Check(r));

        File.Delete(Path.Combine(_game, "b.asi"));
        Assert.Equal(InstallHealth.Partial, InstallChecks.Check(r));

        File.Delete(Path.Combine(_game, "a.asi"));
        Assert.Equal(InstallHealth.Missing, InstallChecks.Check(r));

        // Put aside by "clean launch": still the mod's own, not missing.
        File.WriteAllText(Path.Combine(_game, "a.asi" + CleanLaunchService.DisabledSuffix), "a");
        File.WriteAllText(Path.Combine(_game, "b.asi" + CleanLaunchService.DisabledSuffix), "b");
        Assert.Equal(InstallHealth.Ok, InstallChecks.Check(r));
    }

    [Fact]
    public void A_mod_in_an_archive_is_missing_when_the_archive_or_its_file_is_gone()
    {
        string rpf = ModsArchives.ModsPath(_game, ModsArchives.UpdateRpf);
        Directory.CreateDirectory(Path.GetDirectoryName(rpf)!);
        RpfBuilder.CreateOpen(rpf, new Dictionary<string, byte[]>
        {
            ["x64/car.txt"] = Encoding.UTF8.GetBytes("car"),
            ["common/data/dlclist.xml"] = Encoding.UTF8.GetBytes("<x/>")
        });
        var r = new ModInstallRecord { GameFolder = _game, ModType = MxaModType.Oiv };
        r.ArchiveEdits.Add(new ArchiveEdit { Archive = "update/update.rpf", Entry = "x64/car.txt", WasCreated = true });
        Assert.Equal(InstallHealth.Ok, InstallChecks.Check(r));

        r.ArchiveEdits.Add(new ArchiveEdit { Archive = "update/update.rpf", Entry = "x64/gone.txt", WasCreated = true });
        Assert.Equal(InstallHealth.Partial, InstallChecks.Check(r));

        File.Delete(rpf); // e.g. a backup restore replaced the whole mods folder
        Assert.Equal(InstallHealth.Missing, InstallChecks.Check(r));
    }

    [Fact]
    public void A_game_folder_that_is_gone_makes_everything_missing()
    {
        var r = new ModInstallRecord { GameFolder = Path.Combine(_game, "nowhere") };
        r.Files.Add(new InstalledFile { Dest = "a.asi", WasCreated = true });
        Assert.Equal(InstallHealth.Missing, InstallChecks.Check(r));
    }

    [Fact]
    public void The_game_is_ready_for_mods_only_with_the_mods_folder_enabler()
    {
        Assert.False(PrepareService.IsReadyForMods(_game));
        File.WriteAllText(Path.Combine(_game, "dinput8.dll"), "loader");
        Assert.False(PrepareService.IsReadyForMods(_game)); // a loader alone doesn't read the mods folder
        File.WriteAllText(Path.Combine(_game, "OpenIV.asi"), "x");
        Assert.True(PrepareService.IsReadyForMods(_game));
    }

    [Fact]
    public void Clean_launch_needs_a_loader_to_put_aside()
    {
        Assert.Equal(CleanLaunchService.State.NoLoader, CleanLaunchService.GetState(_game));
        Assert.False(CleanLaunchService.IsCleanLaunchEnabled(_game)); // not "clean" just because nothing is there

        File.WriteAllText(Path.Combine(_game, "dinput8.dll"), "loader");
        Assert.Equal(CleanLaunchService.State.Modded, CleanLaunchService.GetState(_game));

        CleanLaunchService.EnableCleanLaunch(_game);
        Assert.Equal(CleanLaunchService.State.Clean, CleanLaunchService.GetState(_game));
        CleanLaunchService.DisableCleanLaunch(_game);
        Assert.Equal(CleanLaunchService.State.Modded, CleanLaunchService.GetState(_game));
    }
}
