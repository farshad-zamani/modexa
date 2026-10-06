using System.ComponentModel;

namespace Modexa.Core.I18n;

/// <summary>
/// Minimal localization service for EN/FA. XAML binds to the indexer:
///   Text="{Binding [Home_Title], Source={x:Static i18n:Loc.Instance}}"
/// Switching language raises a blanket change so every bound string refreshes live.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private string _lang = "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc() { }

    /// <summary>Current language code: "en" or "fa".</summary>
    public string Language => _lang;

    /// <summary>True for right-to-left languages (Persian).</summary>
    public bool IsRtl => _lang == "fa";

    public void SetLanguage(string lang)
    {
        lang = (lang ?? "en").ToLowerInvariant();
        if (lang != "en" && lang != "fa") lang = "en";
        if (lang == _lang) return;
        _lang = lang;
        // "Item[]" is the name WPF listens on for indexer bindings ({Binding [Key]}); the null
        // member name covers every other property on this source.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRtl)));
        LanguageChanged?.Invoke();
    }

    /// <summary>Localized string by key; falls back to EN, then to the key itself.</summary>
    public string this[string key]
    {
        get
        {
            var table = _lang == "fa" ? LocStrings.Fa : LocStrings.En;
            if (table.TryGetValue(key, out var v)) return v;
            if (LocStrings.En.TryGetValue(key, out var en)) return en;
            return key;
        }
    }

    /// <summary>Non-binding lookup for code paths.</summary>
    public string T(string key) => this[key];

    /// <summary>Localized format string: <c>Format("Key", arg0, ...)</c>.</summary>
    public string Format(string key, params object?[] args) => string.Format(this[key], args);

    /// <summary>Raised after the language changes (after bindings were notified).</summary>
    public event Action? LanguageChanged;
}
