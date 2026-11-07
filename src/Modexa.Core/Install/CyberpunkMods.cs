using System.IO;
using System.Text.RegularExpressions;
using Modexa.Core.Engine;
using Modexa.Core.Format;
using Modexa.Core.Games;
using Modexa.Core.Prepare;

namespace Modexa.Core.Install;

/// <summary>What was found in something a user gave for Cyberpunk 2077.</summary>
public enum CpPlanKind
{
    /// <summary>Mods to install (<see cref="CpModPlan.Items"/>).</summary>
    Ready,
    /// <summary>Nothing in it looks like a Cyberpunk 2077 mod.</summary>
    NoMods,
    /// <summary>The mod is made for other patches than the game's.</summary>
    WrongVersion,
    /// <summary>The mod has a version per patch and the game's patch isn't known: ask the user.</summary>
    NeedsPatch
}

/// <summary>One mod found: a folder of files that go into the game folder as they are.</summary>
public sealed class CpModItem
{
    public string Title { get; init; } = "";
    /// <summary>Where it sits in what the user gave (for the list), or null when it is the top.</summary>
    public string? Location { get; init; }
    /// <summary>The patch it is made for, or null when it doesn't name one.</summary>
    public string? Version { get; init; }
    /// <summary>The file or folder it came from (shown in the installed list).</summary>
    public string SourceName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    /// <summary>Each file and where it goes (game-relative).</summary>
    public List<(string Source, string Dest)> Files { get; init; } = new();

    /// <summary>The game folders it adds to ("archive", "r6", "red4ext"…).</summary>
    public IReadOnlyList<string> Folders => Files.Select(f => f.Dest.Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed class CpModPlan
{
    public CpPlanKind Kind { get; set; }
    public List<CpModItem> Items { get; } = new();
    /// <summary>The patches the mod names (to tell the user which ones it is for).</summary>
    public List<string> Versions { get; } = new();
}

/// <summary>
/// Cyberpunk 2077 mods (free, every edition). Most are copy-and-paste: the mod holds folders that
/// are the game's own ("archive", "bin", "r6", "red4ext"…) and they go into the game folder as they
/// are. Folders named for a patch ("For v2.5") are chosen by the game's patch. Anything Modexa can't be
/// sure about is never installed.
/// </summary>
public static class CyberpunkMods
{
    /// <summary>Folders the game itself has at its top (a mod's own folder of these goes in as it is).</summary>
    private static readonly string[] Roots = { "archive", "bin", "engine", "mods", "r6", "red4ext" };

    /// <summary>The password of the WTMod.com downloads (tried quietly; anything else is asked for).</summary>
    private static readonly string[] KnownPasswords = { "WTMod.com" };

    public static readonly string[] ArchiveExtensions = { ".zip", ".rar", ".7z" };

    public static bool IsArchive(string path)
        => File.Exists(path) && ArchiveExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ---- reading -------------------------------------------------------------------------------------

    /// <summary>
    /// The folder to read: the folder itself, or the archive unpacked into <paramref name="workDir"/>
    /// (an archive inside it, one level deep, too). A protected archive is opened with the password the
    /// mod site uses, else <paramref name="askPassword"/> asks for it (null = cancelled).
    /// </summary>
    public static string Unpack(string path, string workDir, IProgress<int>? progress = null,
        Func<string, string?>? askPassword = null, CancellationToken ct = default)
    {
        if (Directory.Exists(path)) return path;
        string dir = Path.Combine(workDir, "cp-" + Guid.NewGuid().ToString("N")[..8]);
        UnpackWithPassword(path, dir, progress, askPassword, ct);

        // Archives inside (and no mod folders at the top): unpack them next to themselves, then read.
        if (Scan(dir).Count == 0)
            foreach (var inner in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(IsArchive).ToList())
            {
                string target = Path.Combine(Path.GetDirectoryName(inner)!, Path.GetFileNameWithoutExtension(inner));
                try { UnpackWithPassword(inner, target, null, askPassword, ct); File.Delete(inner); }
                catch (OperationCanceledException) { throw; }
                catch { TryDelete(target); }
            }
        return dir;
    }

    private static void UnpackWithPassword(string archive, string dir, IProgress<int>? progress, Func<string, string?>? askPassword, CancellationToken ct)
    {
        if (!BundleArchive.IsEncrypted(archive)) { Extract(archive, dir, null, progress, ct); return; }
        foreach (var pw in KnownPasswords)
            if (TryExtract(archive, dir, pw, progress, ct)) return;
        string name = Path.GetFileName(archive);
        while (askPassword?.Invoke(name) is { Length: > 0 } entered)
        {
            if (TryExtract(archive, dir, entered, progress, ct)) return;
            name = Path.GetFileName(archive) + "\n" + I18n.Loc.Instance["Raw_WrongPassword"];
        }
        throw new ModInstallException(ModInstallException.BadPackage, I18n.Loc.Instance.Format("Raw_NeedsPassword", Path.GetFileName(archive)));
    }

    private static bool TryExtract(string archive, string dir, string password, IProgress<int>? progress, CancellationToken ct)
    {
        try { Extract(archive, dir, password, progress, ct); return true; }
        catch (OperationCanceledException) { throw; }
        catch { TryDelete(dir); return false; } // a wrong password leaves garbage behind
    }

    private static void Extract(string archive, string dir, string? password, IProgress<int>? progress, CancellationToken ct)
    {
        string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        BundleArchive.Extract(archive, password, key =>
        {
            string full = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null; // never outside the work folder
        }, progress, ct);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    // ---- what is in it -------------------------------------------------------------------------------

    private sealed record Candidate(string Prefix, List<(string Source, string Dest)> Files);

    /// <summary>The mod folders in <paramref name="dir"/>: where each starts (its prefix) and what goes where.</summary>
    private static List<Candidate> Scan(string dir)
    {
        string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var byPrefix = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetFullPath(file)[root.Length..].Replace('\\', '/');
            var segs = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int at = RootIndex(segs);
            if (at < 0) continue; // a readme, a picture, a link: not part of the mod
            string prefix = string.Join('/', segs[..at]);
            if (!byPrefix.TryGetValue(prefix, out var c)) byPrefix[prefix] = c = new Candidate(prefix, new());
            c.Files.Add((file, string.Join('\\', segs[at..])));
        }
        return byPrefix.Values.OrderBy(c => c.Prefix, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Index of the first folder of the path that is one of the game's own, or -1.</summary>
    private static int RootIndex(string[] segs)
    {
        // Not the last segment: that is the file. "mods" is a root only when nothing deeper is
        // ("Pack\mods\MyMod\archive\pc\mod\x.archive" is the mod "MyMod", not a REDmod).
        for (int i = 0; i < segs.Length - 1; i++)
        {
            string s = segs[i].ToLowerInvariant();
            if (s == "mods" || !Roots.Contains(s)) continue;
            string next = segs[i + 1].ToLowerInvariant();
            bool ok = s switch { "bin" => next == "x64", "archive" => next == "pc", _ => true };
            if (ok) return i;
        }
        for (int i = 0; i < segs.Length - 2; i++)
            if (segs[i].Equals("mods", StringComparison.OrdinalIgnoreCase)) return i; // a REDmod: mods\<name>\…
        return -1;
    }

    /// <summary>A path that must never be written by a mod (the game's own programs and base archives).</summary>
    internal static bool IsProtected(string dest)
    {
        string d = dest.Replace('/', '\\');
        return d.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
               || d.StartsWith(@"archive\pc\content\", StringComparison.OrdinalIgnoreCase)
               || d.StartsWith(@"archive\pc\ep1\", StringComparison.OrdinalIgnoreCase)
               || d.StartsWith(@"archive\pc\mod\..", StringComparison.Ordinal);
    }

    // ---- versions ------------------------------------------------------------------------------------

    private static readonly Regex TailVersion = new(@"(?<![\d.])v?\s*(\d{1,2}\.\d{1,3})(?!\d|\.\d)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex KeywordVersion = new(@"\b(?:for|patch|game|cp\s?2077|cyberpunk(?:\s?2077)?)\b[^\d]{0,12}v?\s*(\d{1,2}\.\d{1,3})(?!\d|\.\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The patch a folder name says it is made for: "…For v2.5", "…Patch 2.31". Null when it names none.</summary>
    private static string? NamedPatch(string segment)
    {
        var k = KeywordVersion.Match(segment);
        return k.Success ? CyberpunkPatch.Normalize(k.Groups[1].Value) : null;
    }

    /// <summary>A version at the end of the name ("…v2.5") — a patch only when its siblings differ in exactly that.</summary>
    private static string? TrailingVersion(string segment)
    {
        var m = TailVersion.Match(segment);
        return m.Success ? CyberpunkPatch.Normalize(m.Groups[1].Value) : null;
    }

    /// <summary>Folder name without its version tail ("Virtual Car Dealer For v2.5" → "Virtual Car Dealer").</summary>
    private static string WithoutVersion(string segment)
    {
        string s = Regex.Replace(segment, @"[\s\-_]*\b(?:for|patch|game)?\b?[\s\-_]*(?:v?\s*\d{1,2}\.\d{1,3})\s*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim(' ', '-', '_', '.');
        return s.Length == 0 ? segment : s;
    }

    /// <summary>
    /// What a user gave, as a plan: the mods inside it, the right version of each for <paramref name="gamePatch"/>.
    /// </summary>
    /// <param name="dir">The folder to read (see <see cref="Unpack"/>).</param>
    /// <param name="title">Name for a mod that sits at the very top (the file's or folder's name).</param>
    public static CpModPlan Plan(string dir, string title, string? gamePatch)
    {
        var plan = new CpModPlan();
        var candidates = Scan(dir).Where(c => c.Files.All(f => !IsProtected(f.Dest))).ToList();
        // A mod that touches the game's own programs or base archives isn't one we are sure about.
        if (candidates.Count == 0) { plan.Kind = CpPlanKind.NoMods; return plan; }

        // The patch each folder is made for. A trailing version counts when its siblings (same parent)
        // differ only in that — "A For v2.5" / "A For v2.31" / "B v1.0" is one mod in two builds.
        var tags = candidates.ToDictionary(c => c, c => NamedPatch(LastSegment(c.Prefix)), ReferenceEqualityComparer.Instance as IEqualityComparer<Candidate>);
        foreach (var group in candidates.GroupBy(c => ParentOf(c.Prefix), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 2))
        {
            var trailing = group.Select(c => (c, v: TrailingVersion(LastSegment(c.Prefix)))).Where(x => x.v != null).ToList();
            if (trailing.Select(x => x.v).Distinct().Count() >= 2)
                foreach (var (c, v) in trailing) tags[c] ??= v;
        }

        foreach (var v in tags.Values.Where(v => v != null).Distinct().OrderBy(v => v!, Comparer<string>.Create(CyberpunkPatch.Compare)))
            plan.Versions.Add(v!);

        bool versioned = tags.Values.Any(v => v != null);
        if (versioned && string.IsNullOrWhiteSpace(gamePatch)) { plan.Kind = CpPlanKind.NeedsPatch; return plan; }

        var chosen = new List<Candidate>();
        foreach (var c in candidates)
        {
            string? v = tags[c];
            if (v == null || CyberpunkPatch.Same(v, gamePatch)) chosen.Add(c);
        }
        if (chosen.Count == 0) { plan.Kind = CpPlanKind.WrongVersion; return plan; }

        foreach (var c in chosen)
        {
            string last = LastSegment(c.Prefix);
            string? v = tags[c];
            string name = c.Prefix.Length == 0 ? title : v != null ? NameOf(c.Prefix) : last;
            plan.Items.Add(new CpModItem
            {
                Title = name,
                Location = c.Prefix.Length == 0 ? null : c.Prefix,
                Version = v,
                Files = c.Files
            });
        }
        plan.Kind = CpPlanKind.Ready;
        return plan;
    }

    private static string LastSegment(string prefix) => prefix.Length == 0 ? "" : prefix[(prefix.LastIndexOf('/') + 1)..];

    private static string ParentOf(string prefix) => prefix.LastIndexOf('/') is var i and >= 0 ? prefix[..i] : "";

    /// <summary>The mod's name when its folder only says which patch it is for: the folder above it, or the name without the patch.</summary>
    private static string NameOf(string prefix)
    {
        string parent = LastSegment(ParentOf(prefix));
        return parent.Length > 0 && NamedPatch(parent) == null ? parent : WithoutVersion(LastSegment(prefix));
    }

    // ---- installing ----------------------------------------------------------------------------------

    /// <summary>Copies the mod's files into the game folder, recording each so the mod can be removed exactly.</summary>
    public static ModInstallRecord Install(CpModItem item, string gameFolder, IProgress<OivProgress>? progress = null)
    {
        if (!Directory.Exists(gameFolder)) throw new DirectoryNotFoundException(gameFolder);
        var record = new ModInstallRecord
        {
            Name = item.Title,
            ModType = MxaModType.Loose,
            Game = "Cp2077",
            GameFolder = gameFolder,
            Source = item.SourceName,
            SourcePath = item.SourcePath,
            Version = item.Version
        };

        string root = Path.GetFullPath(gameFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long total = item.Files.Sum(f => new FileInfo(f.Source).Length), done = 0;
        try
        {
            foreach (var (source, dest) in item.Files)
            {
                if (IsProtected(dest)) throw new ModInstallException(ModInstallException.Unsupported, $"Refused destination: {dest}");
                if (!Path.GetFullPath(Path.Combine(gameFolder, dest)).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new ModInstallException(ModInstallException.Unsupported, $"Refused destination: {dest}");
                progress?.Report(new OivProgress(done, total, dest));
                LooseFileInstaller.CopyTracked(record, source, dest);
                done += new FileInfo(source).Length;
            }
            progress?.Report(new OivProgress(total, total, null));
        }
        catch
        {
            LooseFileInstaller.Uninstall(record); // never leave a half-installed mod behind
            throw;
        }
        return record;
    }
}
