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
}
