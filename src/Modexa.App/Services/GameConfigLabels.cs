using System.Globalization;
using System.Text.RegularExpressions;
using Modexa.Core.I18n;

namespace Modexa.App.Services;

/// <summary>
/// Friendly, localized names for gameconfig variant folders such as
/// "For More Mods/2,5x traffic 2,5x peds" or "Stock Traffic (Means Gta base)".
/// </summary>
public static class GameConfigLabels
{
    private static readonly Regex Traffic = new(@"(\d+(?:,\d+)?)x\s*traff?ic", RegexOptions.IgnoreCase);
    private static readonly Regex Peds = new(@"(\d+(?:,\d+)?)x\s*peds?", RegexOptions.IgnoreCase);

    public static string Label(string variant)
    {
        var parts = variant.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Part);
        return string.Join("  ·  ", parts);
    }

    private static string Part(string p)
    {
        if (p.StartsWith("Stock", StringComparison.OrdinalIgnoreCase)) return Loc.Instance["GC_Stock"];
        if (p.Equals("For More Mods", StringComparison.OrdinalIgnoreCase)) return Loc.Instance["GC_MoreMods"];
        if (p.Equals("For Less Mods", StringComparison.OrdinalIgnoreCase)) return Loc.Instance["GC_LessMods"];

        var t = Traffic.Match(p);
        var d = Peds.Match(p);
        if (!t.Success && !d.Success) return p;
        var bits = new List<string>();
        if (t.Success) bits.Add(Loc.Instance.Format("GC_Traffic", t.Groups[1].Value.Replace(',', '.')));
        if (d.Success) bits.Add(Loc.Instance.Format("GC_Peds", d.Groups[1].Value.Replace(',', '.')));
        return string.Join(Loc.Instance.IsRtl ? "، " : ", ", bits);
    }

    /// <summary>Stock first, then by traffic multiplier; "more mods" before "less mods".</summary>
    public static IEnumerable<string> Order(IEnumerable<string> variants)
        => variants.OrderBy(v => v.StartsWith("For Less", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                   .ThenBy(v => v.Contains("Stock", StringComparison.OrdinalIgnoreCase) ? -1.0 : Multiplier(v))
                   .ThenBy(v => v.Contains("ped", StringComparison.OrdinalIgnoreCase) ? 1 : 0);

    private static double Multiplier(string v)
    {
        var m = Traffic.Match(v);
        return m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            ? x : 99;
    }
}
