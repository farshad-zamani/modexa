using System.Windows;
using System.Windows.Data;
using Modexa.Core.I18n;

namespace Modexa.App.Services;

/// <summary>
/// Applies a UI language end-to-end: the string table (bindings refresh live), the active font set
/// (Vazirmatn for Persian, Chakra Petch/Rajdhani for English) and persistence.
/// </summary>
public static class LanguageService
{
    public static void Apply(string lang, bool persist = true)
    {
        Loc.Instance.SetLanguage(lang);
        ApplyFonts();

        if (persist && Application.Current is App app)
        {
            app.Settings.Language = Loc.Instance.Language;
            app.Settings.Save();
        }
    }

    public static FlowDirection Flow => Loc.Instance.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Points the active font keys at the set for the current language.</summary>
    public static void ApplyFonts()
    {
        var res = Application.Current.Resources;
        bool fa = Loc.Instance.IsRtl;
        res["Font.Display"] = res[fa ? "Font.Persian" : "Font.En.Display"];
        res["Font.Body"] = res[fa ? "Font.Persian" : "Font.En.Body"];
        res["Font.Hud"] = res[fa ? "Font.Persian" : "Font.En.Hud"];
    }
}

/// <summary>Live localization bindings for UI built in code (survive a language switch).</summary>
public static class Tr
{
    public static Binding B(string key) => new($"[{key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };

    public static T Bind<T>(this T target, DependencyProperty dp, string key) where T : DependencyObject
    {
        BindingOperations.SetBinding(target, dp, B(key));
        return target;
    }
}
