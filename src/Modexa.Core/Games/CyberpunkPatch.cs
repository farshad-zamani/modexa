using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Modexa.Core.Games;

/// <summary>
/// Cyberpunk 2077 mods and tools are made for a PATCH ("2.31", "2.5"); the game's exe carries a build
/// number instead (3.0.88.46235 is patch 2.5). This turns one into the other and compares patches.
/// </summary>
public static class CyberpunkPatch
{
    public const string ExeRelative = @"bin\x64\Cyberpunk2077.exe";

    /// <summary>Known exe builds → patch (from the community's compatibility tables).</summary>
    private static readonly Dictionary<string, string> Builds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3.0.88.46235"] = "2.5"
    };

    /// <summary>Builds the user told Modexa the patch of (file version → patch). The app points this at its settings.</summary>
    public static Func<IReadOnlyDictionary<string, string>> Remembered { get; set; } = () => new Dictionary<string, string>();

    /// <summary>Called when the user names the patch of a build: the app saves it.</summary>
    public static Action<string, string>? Remember { get; set; }

    private static readonly Regex Dotted = new(@"(?<![\d.])(\d{1,2})\.(\d{1,3})(?!\d|\.\d)", RegexOptions.CultureInvariant);

    /// <summary>The exe's file version ("3.0.88.46235"), or null when the game isn't there.</summary>
    public static string? RawVersion(string gameFolder)
    {
        try
        {
            string exe = Path.Combine(gameFolder, ExeRelative);
            if (!File.Exists(exe)) return null;
            var v = FileVersionInfo.GetVersionInfo(exe);
            return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}.{v.FilePrivatePart}";
        }
        catch { return null; }
    }

    /// <summary>The game's patch ("2.31"), or null when Modexa can't tell (then the user is asked once).</summary>
    public static string? Detect(string gameFolder)
    {
        string? raw = RawVersion(gameFolder);
        if (raw == null) return null;
        if (Remembered().TryGetValue(raw, out var chosen)) return Normalize(chosen);
        if (Builds.TryGetValue(raw, out var known)) return known;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(Path.Combine(gameFolder, ExeRelative));
            foreach (var text in new[] { v.ProductVersion, raw })
                if (LooksLikePatch(text) is { } patch) return patch;
        }
        catch { }
        return null;
    }

    /// <summary>"2.31.0.0" or "2.5" → the patch; null for build numbers such as "3.0.88.46235".</summary>
    private static string? LooksLikePatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = new string(text.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || major is < 1 or > 9) return null;
        if (parts.Skip(2).Any(p => p.Trim('0').Length > 0)) return null; // 3.0.88.x is a build, not a patch
        return parts[1].Length is > 0 and <= 3 ? Normalize($"{major}.{parts[1]}") : null;
    }

    /// <summary>The patch named in a folder or file name ("Mod Runner CP2077 v2.31" → "2.31"), or null.</summary>
    public static string? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Match? last = null;
        foreach (Match m in Dotted.Matches(text)) last = m;
        return last == null ? null : Normalize($"{last.Groups[1].Value}.{last.Groups[2].Value}");
    }

    /// <summary>"2.50" and "2.5" are the same patch.</summary>
    public static string Normalize(string patch)
    {
        var parts = patch.Trim().TrimStart('v', 'V').Split('.');
        string major = int.TryParse(parts[0], out int m) ? m.ToString() : parts[0];
        string frac = parts.Length > 1 ? parts[1].TrimEnd('0') : "";
        return $"{major}.{(frac.Length == 0 ? "0" : frac)}";
    }

    public static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Orders patches (2.21 &lt; 2.3 &lt; 2.31 &lt; 2.5): the part after the dot compares as a decimal fraction.</summary>
    public static int Compare(string a, string b)
    {
        var x = Normalize(a).Split('.'); var y = Normalize(b).Split('.');
        int c = int.Parse(x[0]).CompareTo(int.Parse(y[0]));
        if (c != 0) return c;
        // "2.31" > "2.3" > "2.21": compare the fractions as decimals.
        string fx = x[1].PadRight(3, '0'), fy = y[1].PadRight(3, '0');
        return string.CompareOrdinal(fx, fy);
    }
}
