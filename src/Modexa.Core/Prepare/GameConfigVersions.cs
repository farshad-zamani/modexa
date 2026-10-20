using System.Globalization;
using System.Text.RegularExpressions;

namespace Modexa.Core.Prepare;

/// <summary>The gameconfig folder chosen for a game build.</summary>
/// <param name="Folder">Archive folder holding the variants (e.g. ".../Old Versions/Gta Config v34 for v 1.0.3407").</param>
/// <param name="ForBuild">The build(s) it was made for, as shown to the user: "3407", or "34xx" for a family.</param>
/// <param name="Matches">True when it was made for this build (or its family); false = the newest one, as a fallback.</param>
/// <param name="TooOld">
/// The game is older than the builds the pack has configs for (or between two of them): no config fits,
/// and the newest one would not run on it. The user has to update the game first.
/// </param>
/// <param name="Newest">The newest build the pack has a config for (shown to the user).</param>
public sealed record GameConfigPick(string Folder, string ForBuild, bool Matches, bool TooOld = false, string? Newest = null);

/// <summary>
/// Picks the gameconfig made for the game's build from a pack that keeps one folder per build:
/// <code>
///   2/Gta Config v37 for v 1.0.3889/…
///   2/Old Versions/Gta Config v36 for v 1.0.3717/…
///   2/Old Versions/Gta Config v35 for v 1.0.35/…        ← "1.0.35" = every 35xx build
///   2/Old Versions/Gta Config v34 for v 1.0.3407/…
///   2/Old Versions/Gta Config v33 for v 1.0.32._/…      ← "32._" = every 32xx build
/// </code>
/// The build after "1.0." is read up to its first non-digit. A full build must match exactly; a
/// shorter one matches every build that starts with it. The most specific match wins (exact before
/// "34xx" before "3xxx"), then the newest config. With no match, the newest config is used and the
/// user is told it was made for another build.
/// </summary>
public static class GameConfigVersions
{
    private static readonly Regex VersionDir = new(
        @"^Gta\s+Config\b.*?\bv(?<cfg>\d+(?:\.\d+)?)\s+for\s+(?:v\s*)?1\.0\.(?<build>\S.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The variants folder to use for <paramref name="build"/> (0 = unknown build), or null when the pack has none.</summary>
    public static GameConfigPick? Pick(IReadOnlyList<string> files, GameConfigSpec spec, int build)
    {
        if (string.IsNullOrWhiteSpace(spec.VersionsRoot))
        {
            if (string.IsNullOrWhiteSpace(spec.Folder)) return null;
            string fixedFor = spec.ForBuild is > 0 ? spec.ForBuild.Value.ToString(CultureInfo.InvariantCulture) : "";
            return new GameConfigPick(spec.Folder.TrimEnd('/'), fixedFor, spec.ForBuild is null or 0 || build == 0 || spec.ForBuild == build);
        }

        var candidates = Candidates(files, spec.VersionsRoot);
        if (candidates.Count == 0) return null;

        string b = build.ToString(CultureInfo.InvariantCulture);
        var matching = candidates
            .Where(c => build > 0 && c.Digits.Length > 0
                        && (c.Digits.Length >= b.Length ? c.Digits == b : b.StartsWith(c.Digits, StringComparison.Ordinal)))
            .OrderByDescending(c => c.Digits.Length)
            .ThenByDescending(c => c.Config)
            .ToList();
        var best = matching.FirstOrDefault();
        bool matches = best != null;
        var newest = candidates.OrderByDescending(c => c.Config).ThenByDescending(c => c.Digits).First();
        if (best == null && build > 0)
        {
            // Newer than every config (a fresh game update): the newest config, with a warning. Older,
            // or a build in between that no config covers: nothing fits — the game must be updated.
            bool newer = candidates.Where(c => c.Digits.Length > 0).All(c => build > Highest(c.Digits, b.Length));
            if (!newer) return new GameConfigPick(newest.Folder, Label(newest.Digits), false, TooOld: true, Newest: Label(newest.Digits));
        }
        best ??= newest;
        return new GameConfigPick(best.Folder, Label(best.Digits), matches || build == 0, Newest: Label(newest.Digits));
    }

    private sealed record Candidate(string Folder, double Config, string Digits);

    /// <summary>Every "Gta Config vNN for v 1.0.X" folder under the root that holds a gameconfig.xml.</summary>
    private static List<Candidate> Candidates(IReadOnlyList<string> files, string versionsRoot)
    {
        string root = versionsRoot.Replace('\\', '/').TrimEnd('/') + "/";
        var found = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (!f.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !f.EndsWith("/gameconfig.xml", StringComparison.OrdinalIgnoreCase)) continue;
            var segs = f[root.Length..].Split('/');
            string path = root.TrimEnd('/');
            for (int i = 0; i < segs.Length - 1; i++)
            {
                path += "/" + segs[i];
                var m = VersionDir.Match(segs[i].Trim());
                if (!m.Success) continue;
                // The outermost matching folder is the version folder (some packs repeat its name inside).
                if (!found.ContainsKey(path))
                {
                    double.TryParse(m.Groups["cfg"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double cfg);
                    string digits = new(m.Groups["build"].Value.TakeWhile(char.IsAsciiDigit).ToArray());
                    found[path] = new Candidate(path, cfg, digits);
                }
                break;
            }
        }
        return found.Values.ToList();
    }

    /// <summary>The highest build a config's digits cover: "3889" -> 3889, "35" -> 3599 (for 4-digit builds).</summary>
    private static long Highest(string digits, int buildLength)
        => long.TryParse(digits.Length >= buildLength ? digits : digits.PadRight(buildLength, '9'), NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : long.MaxValue;

    private static string Label(string digits) => digits.Length switch
    {
        0 => "?",
        >= 3 => digits,
        _ => digits + new string('x', 4 - digits.Length)
    };
}
