using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Modexa.Core.Format;

namespace Modexa.Core.Install;

/// <summary>
/// Tells the parts of one split mod apart from unrelated mods. Parts come as separate files
/// ("1. NaturalVision Installer - PART ONE.oiv", "Package.2.oiv"…) and are separate installed mods;
/// this only finds which installed ones belong together (to offer "this part or all parts").
///
/// Two OpenIV mods are parts of the same mod when they are in the same game folder, have the same
/// author, and either carry exactly the same name, or their names / file names are the same once
/// the part marker is taken out — and both actually had one ("Part One" / "Part Two", "Package.1" /
/// "Package.2"). A mod without any part marker is never grouped with a look-alike ("Pack" vs "Pack 2").
/// </summary>
public static class ModParts
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex[] NameMarkers =
    {
        new(@"\b(part|pt|vol|volume|disc|disk)\s*[-_#.:]?\s*(\d+|one|two|three|four|five|six|seven|eight|nine|ten|[ivx]{1,4})\b", Opts),
        new(@"\(\s*\d+\s*(of|/)\s*\d+\s*\)|\b\d+\s*(of|/)\s*\d+\b", Opts),
        new(@"(پارت|بخش|قسمت)\s*\d+", Opts),
    };

    // File names also number their parts plainly: "1. Name", "Name.2", "Name_3", "Name - 4".
    private static readonly Regex[] FileMarkers =
    {
        new(@"^\s*\d+\s*[.\-_)]\s*", Opts),
        new(@"[\s._\-]+\d+\s*$", Opts),
    };

    /// <summary>The name without its part marker, or null when it has none.</summary>
    public static string? NameKey(string? name) => Key(name, NameMarkers);

    /// <summary>The file name (without extension) without its part marker, or null when it has none.</summary>
    public static string? FileKey(string? fileName)
        => string.IsNullOrWhiteSpace(fileName) ? null : Key(Path.GetFileNameWithoutExtension(fileName), NameMarkers.Concat(FileMarkers).ToArray());

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
        string key = Regex.Replace(text, @"[^\p{L}\p{Nd}]+", " ").Trim();
        return key.Length == 0 ? null : key;
    }

    private static string Digits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c is >= '۰' and <= '۹' ? (char)('0' + (c - '۰')) : c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠')) : c);
        return sb.ToString();
    }

    /// <summary>True when two installed (or disabled) OpenIV mods are parts of the same mod.</summary>
    public static bool SameMod(string? nameA, string? authorA, string? sourceA, string? nameB, string? authorB, string? sourceB)
    {
        if (!string.Equals((authorA ?? "").Trim(), (authorB ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(nameA) && string.Equals(nameA.Trim(), nameB?.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        string? na = NameKey(nameA), nb = NameKey(nameB);
        if (na != null && na == nb) return true;
        string? fa = FileKey(sourceA), fb = FileKey(sourceB);
        return fa != null && fa == fb;
    }

    public static bool SameMod(ModInstallRecord a, ModInstallRecord b)
        => a.ModType == MxaModType.Oiv && b.ModType == MxaModType.Oiv
           && a.ProductId == null && b.ProductId == null && a.Setup == null && b.Setup == null
           && InstalledModsStore.SameFolder(a.GameFolder, b.GameFolder)
           && SameMod(a.Name, a.Author, a.Source, b.Name, b.Author, b.Source);
}
