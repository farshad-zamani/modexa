using System.IO;
using SharpCompress.Archives;
using SharpCompress.Common;
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
                // Do not replace a working file until its whole entry has been read successfully.
                string temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var input = reader.OpenEntryStream())
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                        CopyEntry(input, output, entry.Size, reader.Type == ArchiveType.Rar && entry.IsEncrypted, ct);
                    ct.ThrowIfCancellationRequested();
                    File.Move(temp, dest, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
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

    /// <summary>Writes exactly the declared file size, consuming any encrypted RAR block padding.</summary>
    internal static void CopyEntry(Stream input, Stream output, long size, bool encryptedRar, CancellationToken ct)
    {
        if (size < 0) throw new InvalidDataException(I18n.Loc.Instance["Archive_InvalidSize"]);
        var buffer = new byte[81920];
        long remaining = size;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException(I18n.Loc.Instance["Archive_Truncated"]);
            output.Write(buffer, 0, read);
            remaining -= read;
        }

        // SharpCompress can expose AES padding after large stored, encrypted RAR files. It belongs
        // to the archive, never to the installed file. Still reach EOF for integrity checks and solid
        // archive state; reject any excess other than the remainder of one 16-byte encrypted block.
        int padding = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (!encryptedRar || read > 15 - padding)
                throw new InvalidDataException(I18n.Loc.Instance["Archive_InvalidSize"]);
            padding += read;
        }
        if (padding > 0 && (size % 16 + padding) % 16 != 0)
            throw new InvalidDataException(I18n.Loc.Instance["Archive_InvalidSize"]);
    }
}
