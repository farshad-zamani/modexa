using System.Text.RegularExpressions;

namespace Modexa.Core.Licensing;

/// <summary>
/// The license key shape, matching what the FSLM WooCommerce plugin generates (same scheme as the
/// Backup Manager: PREFIX-XXXXXX-XXXXXX-XXXXXX-XXXXXX-SUFFIX, alphabet without I/O/0/1).
///
///   Plus (one key per paid mod):  MDXPLS-XXXXXX-XXXXXX-XXXXXX-XXXXXX-&lt;PRODUCT CODE&gt;
///   Pro  (the app upgrade):       MDXPRO-XXXXXX-XXXXXX-XXXXXX-XXXXXX-PRO
///
/// The PREFIX tells the tier. For Plus keys the SUFFIX is the mod's product code — the same code
/// the packer writes into the .mxa — so a key only opens the mod it was sold with. In the FSLM
/// product settings set: prefix "MDXPLS" + suffix "&lt;code&gt;" for each mod product, and
/// prefix "MDXPRO" + suffix "PRO" for the Pro product. Chunks: 4 × 6 characters.
/// </summary>
public static class LicenseFormat
{
    public const string PlusPrefix = "MDXPLS";
    public const string ProPrefix = "MDXPRO";
    public const string ProSuffix = "PRO";
    public const string CharacterSet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int ChunkLength = 6;
    public const int ChunkCount = 4;

    private static readonly Regex KeyPattern = new(
        $@"^({PlusPrefix}|{ProPrefix})-([{CharacterSet}]{{{ChunkLength}}}-){{{ChunkCount}}}([A-Z0-9]{{2,12}})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CodePattern = new("^[A-Z0-9]{2,12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Normalize(string? key) => (key ?? "").Trim().ToUpperInvariant();

    public static bool IsValid(string? key) => !string.IsNullOrWhiteSpace(key) && KeyPattern.IsMatch(Normalize(key));

    /// <summary>Tier from the prefix (Free when the key is malformed).</summary>
    public static LicenseTier TierOf(string? key)
    {
        if (!IsValid(key)) return LicenseTier.Free;
        string k = Normalize(key);
        return k.StartsWith(ProPrefix + "-", StringComparison.Ordinal) ? LicenseTier.Pro : LicenseTier.Plus;
    }

    /// <summary>The suffix — for Plus keys, the product code of the mod the key was sold with.</summary>
    public static string? SuffixOf(string? key)
    {
        if (!IsValid(key)) return null;
        string k = Normalize(key);
        return k[(k.LastIndexOf('-') + 1)..];
    }

    /// <summary>Valid product code for the packer / .mxa header (2–12 letters or digits).</summary>
    public static bool IsValidProductCode(string? code) => !string.IsNullOrWhiteSpace(code) && CodePattern.IsMatch(code.Trim());

    /// <summary>True if <paramref name="key"/> is a Plus key sold for <paramref name="productCode"/>.</summary>
    public static bool Covers(string? key, string? productCode)
    {
        if (TierOf(key) != LicenseTier.Plus) return false;
        if (string.IsNullOrWhiteSpace(productCode)) return true; // legacy packages without a code
        return string.Equals(SuffixOf(key), productCode.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Example shown under the key box.</summary>
    public static string Example(LicenseTier tier, string? productCode = null) => tier == LicenseTier.Pro
        ? $"{ProPrefix}-XXXXXX-XXXXXX-XXXXXX-XXXXXX-{ProSuffix}"
        : $"{PlusPrefix}-XXXXXX-XXXXXX-XXXXXX-XXXXXX-{(IsValidProductCode(productCode) ? productCode!.Trim().ToUpperInvariant() : "CODE")}";
}
