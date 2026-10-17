using System.IO;
using Modexa.Core.Install;
using Xunit;

namespace Modexa.Core.Tests;

public class DisabledModsTests
{
    [Fact]
    public void Kept_link_survives_the_original_and_forgetting_never_touches_the_users_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mxdis_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string game = Path.Combine(dir, "game");
        Directory.CreateDirectory(game);
        string file = Path.Combine(dir, "Package.2.oiv");
        File.WriteAllText(file, "package");
        var mod = new DisabledMod { Name = "Pack", GameFolder = game, Source = "Package.2.oiv", OriginalPath = file };
        try
        {
            mod.KeptPath = DisabledModsStore.Keep(mod.Id, file);
            DisabledModsStore.Add(mod);
            Assert.Single(DisabledModsStore.ForFolder(game));

            if (mod.KeptPath != null) // a hard link (same drive as the data folder)
            {
                File.Delete(file); // the user cleans up their downloads
                Assert.Equal(mod.KeptPath, mod.AvailablePath());
                Assert.Equal("package", File.ReadAllText(mod.AvailablePath()!));
                File.WriteAllText(file, "package");
            }
            else Assert.Equal(file, mod.AvailablePath());

            DisabledModsStore.Remove(mod.Id);
            Assert.Empty(DisabledModsStore.ForFolder(game));
            Assert.True(File.Exists(file));
            Assert.False(Directory.Exists(DisabledModsStore.DirFor(mod.Id)));
        }
        finally
        {
            DisabledModsStore.Remove(mod.Id);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Same_source_reinstall_replaces_the_disabled_entry_only_for_that_file()
    {
        string game = Path.Combine(Path.GetTempPath(), "mxdis_" + Guid.NewGuid().ToString("N"));
        var a = new DisabledMod { Name = "Pack", ModType = Format.MxaModType.Oiv, GameFolder = game, Source = "Package.1.oiv" };
        var b = new DisabledMod { Name = "Pack", ModType = Format.MxaModType.Oiv, GameFolder = game, Source = "Package.2.oiv" };
        try
        {
            DisabledModsStore.Add(a);
            DisabledModsStore.Add(b);
            Assert.Equal(2, DisabledModsStore.PartsOf(a).Count);
            DisabledModsStore.RemoveSameSource(game, "package.1.OIV");
            Assert.Equal(new[] { b.Id }, DisabledModsStore.ForFolder(game).Select(d => d.Id));
        }
        finally
        {
            DisabledModsStore.Remove(a.Id);
            DisabledModsStore.Remove(b.Id);
        }
    }
}
