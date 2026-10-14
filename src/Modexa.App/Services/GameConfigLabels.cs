using System.Globalization;
using System.Text.RegularExpressions;
using Modexa.Core.I18n;

namespace Modexa.App.Services;

/// <summary>
/// Plain, localized names for gameconfig variant folders such as "For More Mods/2,5x traffic 2,5x peds"
/// ("Cars and pedestrians ×2.5 (busier streets and sidewalks)") or "Stock Traffic (Means Gta base)".
/// </summary>
public static class GameConfigLabels
{
    private static readonly Regex Traffic = new(@"(\d+(?:[,.]\d+)?)x\s*traff?ic", RegexOptions.IgnoreCase);
    private static readonly Regex Peds = new(@"(\d+(?:[,.]\d+)?)x\s*peds?", RegexOptions.IgnoreCase);

    /// <summary>True for the variant that keeps the game's own traffic.</summary>
    public static bool IsStock(string variant) => Last(variant).StartsWith("Stock", StringComparison.OrdinalIgnoreCase);

    public static string Label(string variant)
    {
        string p = Last(variant);
        if (IsStock(variant)) return Loc.Instance["GC_Stock"];

        var t = Traffic.Match(p);
        var d = Peds.Match(p);
        if (!t.Success && !d.Success) return p;
        double x = Number((t.Success ? t : d).Groups[1].Value);
        string factor = x.ToString("0.##", CultureInfo.InvariantCulture);
        bool both = t.Success && d.Success;
        string effect = x > 1 ? Loc.Instance[both ? "GC_BusierAll" : "GC_Busier"]
            : x < 1 ? Loc.Instance[both ? "GC_QuieterAll" : "GC_Quieter"]
            : Loc.Instance["GC_Normal"];
        return Loc.Instance.Format(both ? "GC_CarsPeds" : "GC_Cars", factor, effect);
    }

    /// <summary>Normal traffic first, then by multiplier (cars only before cars + pedestrians).</summary>
    public static IEnumerable<string> Order(IEnumerable<string> variants)
        => variants.OrderBy(v => IsStock(v) ? 0 : 1)
                   .ThenBy(Multiplier)
                   .ThenBy(v => Peds.IsMatch(Last(v)) ? 1 : 0);

    private static string Last(string variant) => variant.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? variant;

    private static double Multiplier(string v)
    {
        var m = Traffic.Match(Last(v));
        if (!m.Success) m = Peds.Match(Last(v));
        return m.Success ? Number(m.Groups[1].Value) : 99;
    }

    private static double Number(string s)
        => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 1;
}
