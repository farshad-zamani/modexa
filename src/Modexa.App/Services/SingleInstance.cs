using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace Modexa.App.Services;

/// <summary>
/// One Modexa per user session. A second launch (e.g. double-clicking a .mxa in Explorer) hands its
/// file argument to the running instance over a named pipe and exits, so the package opens in the
/// window the user already has.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Per-user, deterministic (string.GetHashCode is randomized per process in .NET).
    private static readonly string Id = "Modexa_" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..12];
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    /// <summary>Raised on a worker thread with the path sent by a second instance (may be empty).</summary>
    public event Action<string>? Received;

    public SingleInstance()
    {
        _mutex = new Mutex(true, Id + "_Mutex", out bool createdNew);
        IsFirst = createdNew;
        if (IsFirst) _ = ListenAsync(_cts.Token);
    }

    /// <summary>Second instance: forward the argument (best effort, short timeout).</summary>
    public static bool Forward(string? path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id + "_Pipe", PipeDirection.Out);
            client.Connect(1500);
            byte[] data = Encoding.UTF8.GetBytes(path ?? "");
            client.Write(data, 0, data.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(Id + "_Pipe", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                await server.CopyToAsync(ms, ct).ConfigureAwait(false);
                Received?.Invoke(Encoding.UTF8.GetString(ms.ToArray()));
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(250, CancellationToken.None).ConfigureAwait(false); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (IsFirst) _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
