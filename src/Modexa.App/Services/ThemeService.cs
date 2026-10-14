using System.Windows;
using Modexa.Core.Licensing;

namespace Modexa.App.Services;

/// <summary>
/// Two separate things: the license tier (<see cref="CurrentTier"/> — what the app can do) and the
/// look (<see cref="LookTier"/> — which theme dictionary is active). By default the look follows
/// the license; a licensed user may pick a lower look (Free cyan, or Plus on a Pro license) from
/// the title bar, which changes nothing but the appearance. Swapping the dictionary restyles the
/// whole app live because control styles reference theme keys via DynamicResource.
/// </summary>
public static class ThemeService
{
    private static ResourceDictionary? _current;
    private static LicenseTier? _loadedLook;

    /// <summary>The license tier: decides features (never changed by the theme picker).</summary>
    public static LicenseTier CurrentTier { get; private set; } = LicenseTier.Free;

    /// <summary>The active look.</summary>
    public static LicenseTier LookTier { get; private set; } = LicenseTier.Free;

    /// <summary>The license tier changed.</summary>
    public static event Action<LicenseTier>? TierChanged;

    /// <summary>The look changed: (look, licensed). A licensed user on the Free look still gets the animated backdrop, in cyan.</summary>
    public static event Action<LicenseTier, bool>? LookChanged;

    private static readonly Dictionary<LicenseTier, string> Sources = new()
    {
        [LicenseTier.Free] = "pack://application:,,,/Modexa;component/Themes/Theme.Free.xaml",
        [LicenseTier.Plus] = "pack://application:,,,/Modexa;component/Themes/Theme.Plus.xaml",
        [LicenseTier.Pro] = "pack://application:,,,/Modexa;component/Themes/Theme.Pro.xaml",
    };

    /// <summary>Called once at startup after App resources are loaded (App.xaml merges the Free theme).</summary>
    public static void Initialize(ResourceDictionary initialThemeDict)
    {
        _current = initialThemeDict;
        _loadedLook = LicenseTier.Free;
    }

    /// <summary>Applies <paramref name="tier"/> only if it is higher than the current one.</summary>
    public static void Raise(LicenseTier tier)
    {
        if (tier > CurrentTier) Apply(tier);
    }

    /// <summary>Sets the license tier (and the look that goes with it, or the user's chosen one).</summary>
    public static void Apply(LicenseTier tier)
    {
        bool changed = tier != CurrentTier;
        CurrentTier = tier;
        ApplyLook(force: changed);
        if (changed) TierChanged?.Invoke(tier);
    }

    /// <summary>The looks a user with the current license may choose.</summary>
    public static bool CanUseLook(LicenseTier look) => CurrentTier != LicenseTier.Free && look <= CurrentTier;

    /// <summary>The user picked a look (persisted). Null = follow the license.</summary>
    public static void ChooseLook(LicenseTier? look)
    {
        if (Application.Current is App app)
        {
            app.Settings.Theme = look?.ToString();
            app.Settings.Save();
        }
        ApplyLook(force: true);
    }

    /// <summary>The look to show: the saved choice when the license allows it, else the license tier.</summary>
    private static LicenseTier PreferredLook()
    {
        string? saved = (Application.Current as App)?.Settings.Theme;
        if (CurrentTier != LicenseTier.Free && Enum.TryParse<LicenseTier>(saved, true, out var look) && look <= CurrentTier)
            return look;
        return CurrentTier;
    }

    private static void ApplyLook(bool force)
    {
        var look = PreferredLook();
        var app = Application.Current;
        if (_current != null && app != null && look != _loadedLook)
        {
            var next = new ResourceDictionary { Source = new Uri(Sources[look]) };
            var dicts = app.Resources.MergedDictionaries;
            int idx = dicts.IndexOf(_current);
            if (idx < 0) // someone swapped it meanwhile: replace whichever theme dictionary is active
                idx = dicts.ToList().FindLastIndex(d => d.Source?.OriginalString.Contains("Themes/Theme.", StringComparison.OrdinalIgnoreCase) == true);
            if (idx >= 0) dicts[idx] = next;
            else dicts.Insert(0, next);
            _current = next;
            _loadedLook = look;
            force = true;
        }
        if (!force && look == LookTier) return;
        LookTier = look;
        LookChanged?.Invoke(look, CurrentTier != LicenseTier.Free);
    }
}
