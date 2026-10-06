using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modexa.Core.Diagnostics;
using Modexa.Core.Licensing;
using Modexa.Core.Remote;

namespace Modexa.Core.Engine;

/// <summary>Failure talking to a license-gated endpoint, with a stable code for the UI.</summary>
public sealed class LicenseGateException : Exception
{
    public const string Refused = "refused";          // server rejected the license (403/401)
    public const string NotEntitled = "not_entitled"; // license doesn't cover this product
    public const string Network = "network";          // offline / timeout / 5xx
    public const string BadModule = "bad_module";     // download isn't a valid engine

    public string Code { get; }
    public LicenseGateException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}

/// <summary>
/// Downloads, stores and loads the license-gated engine (Modexa.Support.dll), and fetches the
/// per-product content keys that open purchased packages.
///
/// At rest the engine and keys are DPAPI-encrypted (CurrentUser + app entropy) in
/// %LocalAppData%\Modexa\engine — copying them to another PC or account yields nothing usable.
/// The module is loaded from memory into its own AssemblyLoadContext; it never sits on disk as a
/// plain DLL.
/// </summary>
public static class EngineModule
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Modexa.Engine.v1");
    private static readonly object Gate = new();
    private static IModEngine? _cached;
    private static int _loadGeneration;

    private static string BlobPath => Path.Combine(AppPaths.EngineDir, "engine.bin");
    private static string KeysDir => Path.Combine(AppPaths.EngineDir, "keys");

    public static bool IsInstalled => File.Exists(BlobPath);

    /// <summary>Flavor of the installed engine, or Free when none is installed/loadable.</summary>
    public static LicenseTier InstalledFlavor => Load()?.Flavor ?? LicenseTier.Free;

    /// <summary>Loads the installed engine (cached). Null if missing, corrupt, foreign or outdated.</summary>
    public static IModEngine? Load()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
            if (!File.Exists(BlobPath)) return null;
            try
            {
                byte[] dll = ProtectedData.Unprotect(File.ReadAllBytes(BlobPath), Entropy, DataProtectionScope.CurrentUser);
                _cached = Instantiate(dll);
                if (_cached == null) Log.Info("Engine blob present but not loadable; it will be re-downloaded.");
                return _cached;
            }
            catch (Exception ex)
            {
                Log.Error("Engine load", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// Makes sure an engine of at least <paramref name="wanted"/> flavor is installed, downloading it
    /// from the license-gated endpoint when needed. Returns the loaded engine.
    /// </summary>
    public static async Task<IModEngine> EnsureAsync(string licenseKey, LicenseTier wanted,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var current = Load();
        if (current != null && current.Flavor >= wanted) return current;

        byte[] dll = await DownloadAsync(wanted, progress, ct).ConfigureAwait(false);
        var engine = Instantiate(dll)
                     ?? throw new LicenseGateException(LicenseGateException.BadModule, "The downloaded engine is not valid.");
        if (engine.Flavor < wanted)
            throw new LicenseGateException(LicenseGateException.BadModule, "The downloaded engine is the wrong tier.");

        lock (Gate)
        {
            AppPaths.EnsureDir(AppPaths.EngineDir);
            AppPaths.WriteAllBytesAtomic(BlobPath, ProtectedData.Protect(dll, Entropy, DataProtectionScope.CurrentUser));
            _cached = engine;
        }
        Log.Info($"Engine installed: flavor={engine.Flavor} api={engine.ApiVersion}");
        return engine;
    }

    /// <summary>Removes the engine and every cached content key (license revoked / signed out).</summary>
    public static void Remove()
    {
        lock (Gate)
        {
            _cached = null;
            try { if (File.Exists(BlobPath)) File.Delete(BlobPath); } catch { }
            try { if (Directory.Exists(KeysDir)) Directory.Delete(KeysDir, true); } catch { }
        }
    }

    /// <summary>Installs an engine from raw bytes (tests / internal tooling only).</summary>
    public static IModEngine? InstallFromBytes(byte[] dll)
    {
        var engine = Instantiate(dll);
        if (engine == null) return null;
        lock (Gate)
        {
            AppPaths.EnsureDir(AppPaths.EngineDir);
            AppPaths.WriteAllBytesAtomic(BlobPath, ProtectedData.Protect(dll, Entropy, DataProtectionScope.CurrentUser));
            _cached = engine;
        }
        return engine;
    }

    // ---- content keys ----------------------------------------------------------------------------

    /// <summary>
    /// The AES key that opens packages of <paramref name="productId"/>. Issued by the server only when
    /// <paramref name="licenseKey"/> covers that product; cached (DPAPI) so reinstalls work offline.
    /// </summary>
    public static async Task<byte[]> GetContentKeyAsync(string? productId, string licenseKey, CancellationToken ct = default)
    {
        string cache = Path.Combine(KeysDir, KeyFileName(productId));
        try
        {
            if (File.Exists(cache))
            {
                byte[] k = ProtectedData.Unprotect(File.ReadAllBytes(cache), Entropy, DataProtectionScope.CurrentUser);
                if (k.Length == 32) return k;
            }
        }
        catch { /* fall through to the server */ }

        byte[] key = DevKey(productId) ?? await RequestKeyAsync(productId, licenseKey, ct).ConfigureAwait(false);

        try
        {
            AppPaths.EnsureDir(KeysDir);
            AppPaths.WriteAllBytesAtomic(cache, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        }
        catch { }
        return key;
    }

    private static async Task<byte[]> RequestKeyAsync(string? productId, string licenseKey, CancellationToken ct)
    {
        using var http = NewClient(TimeSpan.FromSeconds(20));
        using var form = LicenseManager.BuildGateForm(licenseKey, new Dictionary<string, string>
        {
            ["product_id"] = productId ?? ""
        });

        HttpResponseMessage resp;
        try { resp = await http.PostAsync(Endpoints.ContentKey, form, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LicenseGateException(LicenseGateException.Network, "Could not reach the license server.", ex);
        }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)resp.StatusCode >= 500)
                throw new LicenseGateException(LicenseGateException.Network, $"License server error ({(int)resp.StatusCode}).");

            KeyResponse? parsed = null;
            try { parsed = JsonSerializer.Deserialize<KeyResponse>(body); } catch { }

            if (!resp.IsSuccessStatusCode || parsed == null || !string.Equals(parsed.result, "success", StringComparison.OrdinalIgnoreCase))
            {
                string code = parsed?.code == "not_entitled" ? LicenseGateException.NotEntitled : LicenseGateException.Refused;
                throw new LicenseGateException(code, parsed?.message ?? "The license server refused the request.");
            }

            byte[] key;
            try { key = Convert.FromBase64String(parsed.key ?? ""); }
            catch { key = Array.Empty<byte>(); }
            if (key.Length != 32)
                throw new LicenseGateException(LicenseGateException.Refused, "The license server returned an invalid key.");
            return key;
        }
    }

    private sealed class KeyResponse
    {
        public string? result { get; set; }
        public string? code { get; set; }
        public string? message { get; set; }
        public string? key { get; set; }
    }

    private static string KeyFileName(string? productId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
               string.IsNullOrWhiteSpace(productId) ? "default" : productId.Trim())))[..24] + ".key";

    /// <summary>
    /// DEBUG builds only: derive keys locally from MODEXA_DEV_MASTER (Base64 master secret) so the
    /// full install path can be tested before the key endpoint exists. Compiled out of Release.
    /// </summary>
    private static byte[]? DevKey(string? productId)
    {
#if DEBUG
        string? master = Environment.GetEnvironmentVariable("MODEXA_DEV_MASTER");
        if (string.IsNullOrWhiteSpace(master)) return null;
        byte[] salt = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(productId) ? "default" : productId.Trim());
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromBase64String(master), 32, salt,
            Encoding.UTF8.GetBytes("modexa-mxa-v1"));
#else
        return null;
#endif
    }

    // ---- download / load -------------------------------------------------------------------------

    private static async Task<byte[]> DownloadAsync(LicenseTier tier, IProgress<int>? progress, CancellationToken ct)
    {
#if DEBUG
        // Local testing: point MODEXA_DEV_ENGINE at a built Modexa.Support.dll (skips the download + pin).
        string? devDll = Environment.GetEnvironmentVariable("MODEXA_DEV_ENGINE");
        if (!string.IsNullOrWhiteSpace(devDll) && File.Exists(devDll))
        {
            progress?.Report(100);
            return await File.ReadAllBytesAsync(devDll, ct).ConfigureAwait(false);
        }
#endif
        string url = Endpoints.EngineFor(tier);
        using var http = NewClient(TimeSpan.FromSeconds(90));

        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LicenseGateException(LicenseGateException.Network, "Could not reach the download server.", ex);
        }

        using (resp)
        {
            int status = (int)resp.StatusCode;
            if (status >= 500) throw new LicenseGateException(LicenseGateException.Network, $"Download server error ({status}).");
            if (!resp.IsSuccessStatusCode)
                throw new LicenseGateException(LicenseGateException.BadModule, $"The engine download failed ({status}).");

            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read, last = -1;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > 64 * 1024 * 1024)
                    throw new LicenseGateException(LicenseGateException.BadModule, "The engine download is too large.");
                if (total is > 0)
                {
                    int pct = (int)(ms.Length * 100 / total.Value);
                    if (pct != last) { last = pct; progress?.Report(pct); }
                }
            }

            byte[] bytes = ms.ToArray();
            if (bytes.Length < 1024 || bytes[0] != 0x4D || bytes[1] != 0x5A) // PE "MZ"
                throw new LicenseGateException(LicenseGateException.BadModule, "The downloaded engine is not valid.");

            // Integrity pin: a release build knows the exact hash of each engine it ships with.
            string? expected = EngineHashes.For(tier);
            if (expected != null)
            {
                string actual = Convert.ToHexString(SHA256.HashData(bytes));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new LicenseGateException(LicenseGateException.BadModule, "The downloaded engine failed its integrity check.");
            }

            progress?.Report(100);
            return bytes;
        }
    }

    /// <summary>Loads the module from memory and creates its engine; null if it isn't a compatible engine.</summary>
    private static IModEngine? Instantiate(byte[] dll)
    {
        try
        {
            // A fresh context per load so a re-downloaded (e.g. Plus -> Pro) module replaces the old one.
            // It resolves Modexa.Core from the default context, so the interfaces are shared.
            var ctx = new AssemblyLoadContext("ModexaEngine" + Interlocked.Increment(ref _loadGeneration), isCollectible: false);
            Assembly asm = ctx.LoadFromStream(new MemoryStream(dll));
            var type = asm.GetTypes().FirstOrDefault(t =>
                typeof(IModEngine).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
            if (type == null) return null;

            var engine = (IModEngine?)Activator.CreateInstance(type);
            if (engine == null || engine.ApiVersion != ModEngineContract.ApiVersion) return null;
            if (engine.Flavor == LicenseTier.Pro && engine is not IProModEngine) return null;
            return engine;
        }
        catch (Exception ex)
        {
            Log.Error("Engine instantiate", ex);
            return null;
        }
    }

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Modexa/{typeof(EngineModule).Assembly.GetName().Version}");
        return c;
    }
}
