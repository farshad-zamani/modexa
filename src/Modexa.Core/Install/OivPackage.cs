using System.IO;
using System.IO.Compression;
using Modexa.Core.Engine;

namespace Modexa.Core.Install;

/// <summary>
/// An OIV package read in place — straight from the <c>.oiv</c>/<c>.oivs</c> zip (nothing is
/// extracted up front, so even multi-GB packages open instantly) or from an extracted folder.
/// Paths are package-relative with '/' separators, e.g. <c>assembly.xml</c>, <c>content/a/b.awc</c>.
/// </summary>
public sealed class OivPackage : IDisposable
{
    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _dir;

    /// <summary>The .oiv / .oivs file (or folder) this package was opened from.</summary>
    public string Source { get; }

    private OivPackage(string source, ZipArchive? zip, string? dir)
    {
        Source = source;
        _zip = zip;
        _dir = dir;
        if (zip == null) return;
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue; // folder entries
            _entries.TryAdd(Key(e.FullName), e);
        }
    }

    public static bool IsPackageFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".oiv" or ".oivs";

    public static OivPackage Open(string path)
    {
        if (Directory.Exists(path)) return new OivPackage(path, null, Path.GetFullPath(path));
        try
        {
            return new OivPackage(path, ZipFile.OpenRead(path), null);
        }
        catch (InvalidDataException ex)
        {
            throw new ModInstallException(ModInstallException.BadPackage, "The file is not a valid OIV package (zip).", ex);
        }
    }

    private static string Key(string rel) => rel.Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>A folder package's file on disk, refusing anything outside the package folder.</summary>
    private string DiskPath(string rel)
    {
        string full = Path.GetFullPath(Path.Combine(_dir!, Key(rel).Replace('/', Path.DirectorySeparatorChar)));
        string root = _dir!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ModInstallException(ModInstallException.BadPackage, $"Invalid source path: {rel}");
        return full;
    }

    private static void CheckRelative(string rel)
    {
        if (Key(rel).Split('/').Any(s => s == ".."))
            throw new ModInstallException(ModInstallException.BadPackage, $"Invalid source path: {rel}");
    }

    public bool Exists(string rel)
    {
        CheckRelative(rel);
        return _zip != null ? _entries.ContainsKey(Key(rel)) : File.Exists(DiskPath(rel));
    }

    public long Length(string rel)
    {
        CheckRelative(rel);
        if (_zip != null) return _entries.TryGetValue(Key(rel), out var e) ? e.Length : -1;
        var fi = new FileInfo(DiskPath(rel));
        return fi.Exists ? fi.Length : -1;
    }

    public Stream OpenRead(string rel)
    {
        CheckRelative(rel);
        if (_zip == null) return new FileStream(DiskPath(rel), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        if (!_entries.TryGetValue(Key(rel), out var e))
            throw new ModInstallException(ModInstallException.BadPackage, $"The package is missing {rel}.");
        return e.Open();
    }

    public byte[] ReadAllBytes(string rel)
    {
        using var s = OpenRead(rel);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public byte[]? TryReadAllBytes(string rel) => Exists(rel) ? ReadAllBytes(rel) : null;

    /// <summary>Files below a package folder, as paths relative to that folder.</summary>
    public IEnumerable<string> FilesUnder(string relDir)
    {
        CheckRelative(relDir);
        string prefix = Key(relDir).TrimEnd('/') + "/";
        if (_zip != null)
            return _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k[prefix.Length..]).ToList();
        string dir = DiskPath(relDir);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).ToList()
            : new List<string>();
    }

    public void Dispose() => _zip?.Dispose();
}

/// <summary>An <see cref="IProgress{T}"/> that reports synchronously on the calling thread.</summary>
public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;
    public InlineProgress(Action<T> report) => _report = report;
    public void Report(T value) => _report(value);
}
