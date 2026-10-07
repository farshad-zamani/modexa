using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace Modexa.Core.Download;

public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value : null;
    public int? Percent => Fraction is { } f ? (int)Math.Round(f * 100) : null;
}

/// <summary>
/// Streams a file download with progress reporting and optional SHA-256 verification. Used for the
/// "Prepare for mods" bundles so the user visibly sees files arrive from the internet.
/// </summary>
public static class DownloadService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // large files; we cancel via token
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Modexa/1.0");
        return c;
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destPath"/>. If
    /// <paramref name="expectedSha256"/> is supplied, the file is verified and the method throws
    /// on mismatch (the partial file is deleted).
    /// </summary>
    public static async Task DownloadAsync(
        string url,
        string destPath,
        IProgress<DownloadProgress>? progress = null,
        string? expectedSha256 = null,
        CancellationToken ct = default)
    {
        AppPaths.EnsureDir(Path.GetDirectoryName(destPath)!);
        string tempPath = destPath + ".part";

        // No overall timeout (packs are large), but a connection that delivers nothing for a minute is
        // treated as dead instead of hanging the progress dialog forever.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        try
        {
            // ConfigureAwait(false) throughout: the copy loop must not bounce every chunk through the
            // UI thread (that floods the dispatcher and starves input -> "Not Responding").
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;
            await using var src = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                int read;
                int? lastPercent = null;
                long lastReportTicks = 0;
                progress?.Report(new DownloadProgress(0, total));
                while ((read = await src.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;

                    // Throttle: report on percent change, at most ~10x per second.
                    var p = new DownloadProgress(received, total);
                    long now = Environment.TickCount64;
                    if (p.Percent != lastPercent && now - lastReportTicks >= 100)
                    {
                        lastPercent = p.Percent;
                        lastReportTicks = now;
                        progress?.Report(p);
                    }
                }
                progress?.Report(new DownloadProgress(received, total));
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                string actual = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expectedSha256.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tempPath);
                    throw new InvalidDataException("Downloaded file failed checksum verification.");
                }
            }

            if (total is > 0 && new FileInfo(tempPath).Length != total.Value)
                throw new IOException("The download was cut off before it finished. Please try again.");

            File.Move(tempPath, destPath, overwrite: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(tempPath);
            throw new TimeoutException("The download stopped responding. Check your internet connection and try again.");
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
