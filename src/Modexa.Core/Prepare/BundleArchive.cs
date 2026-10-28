using System.IO;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Modexa.Core.Prepare;

/// <summary>Reads prepare bundles (.rar / .zip / .7z) through SharpCompress.</summary>
public static class BundleArchive
{
    private static ReaderOptions Options(string? password)
        => string.IsNullOrEmpty(password) ? new ReaderOptions() : new ReaderOptions { Password = password };

    public static string Normalize(string key) => key.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// True when the archive is protected by a password (its entries, or its whole table of contents,
    /// are encrypted).
    /// </summary>
    public static bool IsEncrypted(string archivePath)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
            return archive.Entries.Any(e => e.IsEncrypted);
        }
        catch (Exception ex)
        {
            // An encrypted table of contents can't even be listed without the password.
            string m = (ex.GetType().Name + " " + ex.Message).ToLowerInvariant();
            return m.Contains("password") || m.Contains("encrypt") || m.Contains("crypt");
        }
    }

    /// <summary>All file paths in the archive (headers only, no decompression).</summary>
    public static List<string> ListFiles(string archivePath, string? password = null)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath, Options(password));
        return archive.Entries.Where(e => !e.IsDirectory && e.Key != null)
            .Select(e => Normalize(e.Key!))
            .ToList();
    }

    /// <summary>
    /// Streams through the archive once (solid RAR friendly) and writes each wanted entry to the path
    /// <paramref name="destinationFor"/> returns (null = skip). Returns the number of files written.
    /// </summary>
    public static int Extract(string archivePath, string? password, Func<string, string?> destinationFor,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        using var fs = File.OpenRead(archivePath);
        using var reader = ReaderFactory.OpenReader(fs, Options(password));
        int written = 0, lastPct = -1;
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory || entry.Key == null) continue;

            string? dest = destinationFor(Normalize(entry.Key));
            if (dest != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var outFs = File.Create(dest);
                reader.WriteEntryTo(outFs);
                written++;
            }

            if (fs.Length > 0)
            {
                int pct = (int)(fs.Position * 100 / fs.Length);
                if (pct != lastPct) { lastPct = pct; progress?.Report(pct); }
            }
        }
        return written;
    }
}
