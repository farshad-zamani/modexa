using Modexa.Core.Install;
using Xunit;

namespace Modexa.Core.Tests;

public class ModPartsTests
{
    [Theory]
    // NaturalVision Evolved: different names per part, same author.
    [InlineData("NaturalVision Evolved - Part One", "1. NaturalVision Installer - PART ONE.oiv",
                "NaturalVision Evolved - Part Two", "2. NaturalVision Installer - PART TWO.oiv", true)]
    // The two-part dub: the very same name.
    [InlineData("GTA V Farsidub Fullpack", "GTA.V.Farsidub.Fullpack.1.oiv", "GTA V Farsidub Fullpack", "GTA.V.Farsidub.Fullpack.2.oiv", true)]
    [InlineData("Car Pack", "Package.1.oiv", "Car Pack Extra", "Package.2.oiv", true)]
    // Look-alikes without part markers are different mods.
    [InlineData("Real Cars Pack", "RealCarsPack.oiv", "Real Cars Pack 2", "RealCarsPack2.oiv", false)]
    [InlineData("HD Blood", "HD Blood V.oiv", "HD Water", "HD Water V.oiv", false)]
    public void Parts_of_one_mod_are_recognised(string nameA, string fileA, string nameB, string fileB, bool same)
        => Assert.Equal(same, ModParts.SameMod(nameA, "Author", fileA, nameB, "Author", fileB));

    [Fact]
    public void Another_author_is_another_mod()
        => Assert.False(ModParts.SameMod("Pack - Part 1", "A", "p1.oiv", "Pack - Part 2", "B", "p2.oiv"));

    [Fact]
    public void Largely_same_names_installed_together_are_one_mod()
    {
        Assert.True(ModParts.SameMod("Real Cars Pack", "A", "RealCarsPack.oiv", "Real Cars Pack 2", "A", "RealCarsPack2.oiv", installedTogether: true));
        Assert.True(ModParts.SameMod("Los Santos Remastered Roads", "A", "lsr.oiv", "Los Santos Remastered Roads Extra", "A", "lsr-extra.oiv", installedTogether: true));
        Assert.False(ModParts.SameMod("HD Blood", "A", "HD Blood V.oiv", "HD Water", "A", "HD Water V.oiv", installedTogether: true));
    }

    [Theory]
    [InlineData("1. NaturalVision Installer - PART ONE.oiv", 1)]
    [InlineData("NaturalVision Installer - Part Two.oiv", 2)]
    [InlineData("Mod part 3.oiv", 3)]
    [InlineData("Mod_p4.oiv", 4)]
    [InlineData("Package.2.oiv", 2)]
    [InlineData("02 - Roads.oiv", 2)]
    [InlineData("Roads (3 of 4).oiv", 3)]
    [InlineData("Mod Vol. II.oiv", 2)]
    [InlineData("ماد پارت ۲.oiv", 2)]
    [InlineData("Roads.oiv", null)]
    public void Part_numbers_are_read_from_file_names(string file, int? part)
        => Assert.Equal(part, ModParts.PartNumber(file));

    [Fact]
    public void Parts_install_in_order()
    {
        var files = new[] { "Mod part 10.oiv", "Mod part 2.oiv", "Mod - Part One.oiv", "Mod p3.oiv" };
        Assert.Equal(new[] { "Mod - Part One.oiv", "Mod part 2.oiv", "Mod p3.oiv", "Mod part 10.oiv" },
            ModParts.InInstallOrder(files, f => f));
    }
}
