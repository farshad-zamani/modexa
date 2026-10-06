using Modexa.Core.Licensing;
using Xunit;

namespace Modexa.Core.Tests;

public class LicenseFormatTests
{
    [Theory]
    [InlineData("MDXPRO-ABCDEF-GHJKLM-NPQRST-UVWXYZ-PRO", true)]
    [InlineData("MDXPLS-ABCDEF-GHJKLM-NPQRST-UVWXYZ-CAR206", true)]
    [InlineData("mdxpls-abcdef-ghjklm-npqrst-uvwxyz-car206", true)]  // case-insensitive
    [InlineData("MDXPLS-ABCDEF-GHJKLM-NPQRST-UVWXYZ-X", false)]       // product code too short
    [InlineData("MDXPLS-ABC-GHJKLM-NPQRST-UVWXYZ-CAR206", false)]     // wrong chunk length
    [InlineData("MDXPLS-ABCDEI-GHJKLM-NPQRST-UVWXYZ-CAR206", false)]  // 'I' not in alphabet
    [InlineData("MDXPLS-ABCDEF-GHJKLM-NPQRST-CAR206", false)]         // only 3 chunks
    [InlineData("RSGBMP-ABCDEF-GHJKLM-NPQRST-UVWXYZ-PRO", false)]     // Backup Manager key
    [InlineData("MDX-ABCDEF-GHJKLM-NPQRST-UVWXYZ-PRO", false)]        // old draft format
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_matches_expected(string? key, bool expected)
        => Assert.Equal(expected, LicenseFormat.IsValid(key));

    [Theory]
    [InlineData("MDXPRO-ABCDEF-GHJKLM-NPQRST-UVWXYZ-PRO", LicenseTier.Pro)]
    [InlineData("MDXPLS-ABCDEF-GHJKLM-NPQRST-UVWXYZ-CAR206", LicenseTier.Plus)]
    [InlineData("whatever", LicenseTier.Free)]
    public void Tier_comes_from_prefix(string key, LicenseTier expected)
        => Assert.Equal(expected, LicenseManager.TierFromKey(key));

    [Theory]
    [InlineData("MDXPLS-ABCDEF-GHJKLM-NPQRST-UVWXYZ-CAR206", "CAR206", true)]
    [InlineData("MDXPLS-ABCDEF-GHJKLM-NPQRST-UVWXYZ-CAR206", "car206", true)]
    [InlineData("MDXPLS-ABCDEF-GHJKLM-NPQRST-UVWXYZ-CAR206", "MAP01", false)]  // key for another mod
    [InlineData("MDXPRO-ABCDEF-GHJKLM-NPQRST-UVWXYZ-PRO", "CAR206", false)]    // Pro keys don't open paid mods
    public void Plus_key_covers_only_its_product(string key, string code, bool expected)
        => Assert.Equal(expected, LicenseFormat.Covers(key, code));
}
