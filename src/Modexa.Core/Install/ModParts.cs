using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Modexa.Core.Format;

namespace Modexa.Core.Install;

/// <summary>
/// Tells the parts of one split mod apart from unrelated mods, and their order. Parts come as
/// separate files ("1. NaturalVision Installer - PART ONE.oiv", "Package.2.oiv", "Mod p3.oiv"…) and
/// are separate installed mods; this finds which belong together (to show them as one mod, and to
/// offer "this part or all parts") and in which order they install.
///
/// Two OpenIV mods belong together when they are in the same game folder, have the same author, and
/// either carry exactly the same name, or their names / file names are the same once the part marker
/// is taken out (both having one), or their names are largely the same — more readily so when they
/// were installed together.
/// </summary>
public static class ModParts
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string Words = "one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth";
    private const string Roman = "i|ii|iii|iv|v|vi|vii|viii|ix|x|xi|xii";
    private const string FaWords = "یک|دو|سه|چهار|پنج|شش|هفت|هشت|نه|ده|اول|دوم|سوم|چهارم|پنجم|ششم|هفتم|هشتم|نهم|دهم";

    // Explicit part markers, in order of trust.
    private static readonly Regex KeywordPart = new($@"(?<![\p{{L}}])(?:part|pt|p|vol|volume|disc|disk|cd)\s*[-_#.:]?\s*(?<n>\d+|{Words}|{Roman})(?![\p{{L}}\d])", Opts);
    private static readonly Regex OfPart = new(@"\(?\s*(?<n>\d+)\s*(?:of|/|از)\s*\d+\s*\)?", Opts);
    private static readonly Regex FaPart = new($@"(?:پارت|بخش|قسمت)\s*(?<n>\d+|{FaWords})", Opts);
    // Plain numbering in file names: "1. Name", "01 - Name", "Name.2", "Name_3", "Name - 4".
    private static readonly Regex LeadingNumber = new(@"^\s*(?<n>\d{1,3})\s*[.\-_)]\s*", Opts);
    private static readonly Regex TrailingNumber = new(@"[\s._\-]+(?<n>\d{1,3})\s*$", Opts);

    private static readonly Regex[] NameMarkers = { KeywordPart, OfPart, FaPart };
    private static readonly Regex[] FileMarkers = { KeywordPart, OfPart, FaPart, LeadingNumber, TrailingNumber };

    /// <summary>Words that say nothing about which mod it is ("INSTALL - Name", "Name Installer").</summary>
    private static readonly Regex Generic = new(@"\b(install(er)?|setup|mod|oiv|package|pack|v?\d+(\.\d+)+)\b", Opts);

    /// <summary>The part number a file or mod name carries ("PART ONE", "part 2", "p3", "1. Name", "Name.4", "پارت ۲"), or null.</summary>
    public static int? PartNumber(string? nameOrFile)
    {
        if (string.IsNullOrWhiteSpace(nameOrFile)) return null;
        string s = Digits(nameOrFile);
        if (Path.GetExtension(s).ToLowerInvariant() is ".oiv" or ".oivs" or ".mxa" or ".zip" or ".rar" or ".7z")
            s = Path.GetFileNameWithoutExtension(s);
        foreach (var rx in FileMarkers)
        {
            var m = rx.Match(s);
            if (m.Success && Number(m.Groups["n"].Value) is { } n) return n;
        }
        return null;
    }

    private static int? Number(string token)
    {
        string t = token.Trim().ToLowerInvariant();
        if (int.TryParse(t, out int n)) return n;
        string[] en = Words.Split('|'), ro = Roman.Split('|'), fa = FaWords.Split('|');
        int i = Array.IndexOf(en, t);
        if (i >= 0) return i < 12 ? i + 1 : i - 12 + 1;
        i = Array.IndexOf(ro, t);
        if (i >= 0) return i + 1;
        i = Array.IndexOf(fa, t);
        if (i >= 0) return i < 10 ? i + 1 : i - 10 + 1;
        return null;
    }

    /// <summary>
    /// Orders package files the way they must install: by mod, then by part number (part 1 before part
    /// 2 before part 10, however each is written), then by name.
    /// </summary>
    public static List<T> InInstallOrder<T>(IEnumerable<T> items, Func<T, string> fileName)
        => items.OrderBy(i => FileKey(fileName(i)) ?? Normalize(Path.GetFileNameWithoutExtension(fileName(i))), StringComparer.Ordinal)
            .ThenBy(i => PartNumber(fileName(i)) ?? 0)
            .ThenBy(i => fileName(i), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The name to show for all parts together: the name without its part marker, as written.</summary>
    public static string BaseName(string name)
    {
        string text = Digits(name);
        foreach (var m in NameMarkers) text = m.Replace(text, " ");
        text = Regex.Replace(text, @"\s{2,}", " ").Trim(' ', '-', '_', '.', ':', '–', '—');
        return text.Length == 0 ? name : text;
    }

    /// <summary>The name without its part marker, or null when it has none.</summary>
    public static string? NameKey(string? name) => Key(name, NameMarkers);

    /// <summary>The file name (without extension) without its part marker, or null when it has none.</summary>
    public static string? FileKey(string? fileName)
        => string.IsNullOrWhiteSpace(fileName) ? null : Key(Path.GetFileNameWithoutExtension(fileName), FileMarkers);

    private static string? Key(string? s, Regex[] markers)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string text = Digits(s).ToLowerInvariant();
        bool found = false;
        foreach (var m in markers)
        {
            string next = m.Replace(text, " ");
            found |= next != text;
            text = next;
        }
        if (!found) return null;
        string key = Normalize(text);
        return key.Length == 0 ? null : key;
    }

    private static string Normalize(string s) => Regex.Replace(Digits(s).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ").Trim();

    private static string Digits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c is >= '۰' and <= '۹' ? (char)('0' + (c - '۰')) : c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠')) : c);
        return sb.ToString();
    }

    /// <summary>
    /// How much two names are the same, 0..1: the shared beginning of their meaningful words (part
    /// markers and words like "INSTALL -" left out) over the longer one.
    /// </summary>
    public static double Similarity(string? a, string? b)
    {
        string x = Meaningful(a), y = Meaningful(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        int n = 0, max = Math.Min(x.Length, y.Length);
        while (n < max && x[n] == y[n]) n++;
        // Count only whole words of the shared beginning ("realcarspack" is no word of "realcarspack2").
        string longer = x.Length >= y.Length ? x : y;
        bool wordEnds = n == longer.Length || longer[n] == ' ';
        if (n > 0 && (n < max || !wordEnds)) { int space = x.LastIndexOf(' ', n - 1); n = space < 0 ? 0 : space; }
        return n < 6 ? 0 : (double)n / Math.Max(x.Length, y.Length);
    }

    private static string Meaningful(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        string t = Digits(s);
        if (Path.GetExtension(t).ToLowerInvariant() is ".oiv" or ".oivs" or ".mxa") t = Path.GetFileNameWithoutExtension(t);
        // A bare trailing number stays: "Real Cars Pack 2" may be a sequel rather than a part.
        foreach (var rx in FileMarkers) if (rx != TrailingNumber) t = rx.Replace(t, " ");
        t = Generic.Replace(t, " ");
        return Normalize(t);
    }

    /// <summary>True when two installed (or disabled) OpenIV mods are parts of the same mod.</summary>
    /// <param name="installedTogether">Installed one right after the other (e.g. picked together).</param>
    public static bool SameMod(string? nameA, string? authorA, string? sourceA, string? nameB, string? authorB, string? sourceB,
        bool installedTogether = false)
    {
        if (!string.Equals((authorA ?? "").Trim(), (authorB ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(nameA) && string.Equals(nameA.Trim(), nameB?.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        string? na = NameKey(nameA), nb = NameKey(nameB);
        if (na != null && na == nb) return true;
        string? fa = FileKey(sourceA), fb = FileKey(sourceB);
        if (fa != null && fa == fb) return true;
        // Largely the same name: related (a lower bar when they were installed together, so a lone
        // sequel such as "Real Cars Pack 2" stays its own mod unless it came in with the first one).
        double bar = installedTogether ? 0.6 : 0.85;
        return Math.Max(Similarity(nameA, nameB), Similarity(sourceA, sourceB)) >= bar;
    }

    public static bool SameMod(ModInstallRecord a, ModInstallRecord b)
        => a.ModType == MxaModType.Oiv && b.ModType == MxaModType.Oiv
           && a.ProductId == null && b.ProductId == null && a.Setup == null && b.Setup == null
           && InstalledModsStore.SameFolder(a.GameFolder, b.GameFolder)
           && SameMod(a.Name, a.Author, a.Source, b.Name, b.Author, b.Source,
               installedTogether: Math.Abs((a.InstalledUtc - b.InstalledUtc).TotalMinutes) <= 30);
}
