using System.Windows;
using Modexa.Core.Licensing;

namespace Modexa.App.Services;

/// <summary>
/// Swaps the active tier theme dictionary at runtime. Because control styles reference theme keys
/// via DynamicResource, replacing the dictionary restyles the whole app live.
/// </summary>
public static class ThemeService
{
    private static ResourceDictionary? _current;
    public static LicenseTier CurrentTier { get; private set; } = LicenseTier.Free;

    public static event Action<LicenseTier>? TierChanged;

    private static readonly Dictionary<LicenseTier, string> Sources = new()
    {
        [LicenseTier.Free] = "pack://application:,,,/Themes/Theme.Free.xaml",
        [LicenseTier.Plus] = "pack://application:,,,/Themes/Theme.Plus.xaml",
        [LicenseTier.Pro] = "pack://application:,,,/Themes/Theme.Pro.xaml",
    };

    /// <summary>Called once at startup after App resources are loaded.</summary>
    public static void Initialize(ResourceDictionary initialThemeDict)
    {
        _current = initialThemeDict;
    }

    /// <summary>Applies <paramref name="tier"/> only if it is higher than the current one.</summary>
    public static void Raise(LicenseTier tier)
    {
        if (tier > CurrentTier) Apply(tier);
    }

    public static void Apply(LicenseTier tier)
    {
        if (_current == null) return;
        if (tier == CurrentTier && _current.Source != null) return;

        var app = Application.Current;
        if (app == null) return;

        var next = new ResourceDictionary { Source = new Uri(Sources[tier]) };
        var dicts = app.Resources.MergedDictionaries;

        int idx = dicts.IndexOf(_current);
        if (idx >= 0)
            dicts[idx] = next;
        else
            dicts.Insert(0, next);

        _current = next;
        CurrentTier = tier;
        TierChanged?.Invoke(tier);
    }
}
