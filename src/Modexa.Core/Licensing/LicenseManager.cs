using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Modexa.Core.Security;

namespace Modexa.Core.Licensing;

/// <summary>
/// License activation/verification against the FSLM backend, plus local DPAPI storage and a
/// hardware+key-locked offline grace window. Mirrors the Backup Manager's contract:
///   POST https://rockstargame.ir/  (application/x-www-form-urlencoded)
///   fields: fslm_v2_api_request=activate|verify, fslm_api_key, license_key, device_id
///   response JSON: { result, code, message }  (success = result == "success")
/// </summary>
public static class LicenseManager
{
    private const string BaseApiUrl = Remote.Endpoints.LicenseApi;
    private const string UserAgent = "Modexa/1.0";

    // Key shape lives in LicenseFormat (prefix = tier, suffix = product code for Plus keys).

    private const int OfflineGraceDays = 7;
    private const int ClockSkewToleranceMinutes = 10;

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        c.DefaultRequestHeaders.Referrer = new Uri(BaseApiUrl);
        return c;
    }

    private static string LicenseFilePath => AppPaths.LicenseFile;
    private static string StateFilePath => AppPaths.StateFile;

    public static bool HasLocalLicense()
    {
        MigrateLegacyFile();
        return LicenseStore.Any();
    }

    // ---- Format + tier ----------------------------------------------------------------------

    public static bool IsFormatValid(string? licenseKey) => LicenseFormat.IsValid(licenseKey);

    /// <summary>Tier implied by the key's prefix (MDXPRO / MDXPLS). Does not prove validity.</summary>
    public static LicenseTier TierFromKey(string? licenseKey) => LicenseFormat.TierOf(licenseKey);

    // ---- Server calls -----------------------------------------------------------------------

    private enum ServerVerdict { Success, Rejected, Unreachable }

    /// <param name="productId">FSLM product the key must belong to (a paid mod); null for Pro/account keys.</param>
    public static async Task<LicenseValidationResult> ActivateAsync(string licenseKey, string? productId = null)
        => await CallUserFacingAsync("activate", licenseKey, productId).ConfigureAwait(false);

    public static async Task<LicenseValidationResult> VerifyAsync(string licenseKey, string? productId = null)
        => await CallUserFacingAsync("verify", licenseKey, productId).ConfigureAwait(false);

    private static async Task<LicenseValidationResult> CallUserFacingAsync(string op, string licenseKey, string? productId)
    {
        SecurityHelper.CheckDebugger();
        if (!SecurityHelper.ValidateIntegrity())
            return new LicenseValidationResult { IsValid = false, ErrorCode = "TAMPERING_DETECTED" };

        try
        {
            using var content = BuildForm(op, licenseKey, productId);
            using var response = await Http.PostAsync(BaseApiUrl, content).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                return new LicenseValidationResult
                {
                    IsValid = false,
                    // 401 from FSLM = the app's API key was rejected -> the app needs an update.
                    ErrorCode = status == 401 ? "APP_UPDATE" : status.ToString()
                };
            }

            var api = Deserialize(body);
            bool ok = api?.IsSuccess == true;

            // Same handling as the Backup Manager: a revoked/rotated API key means "update the app".
            string? code = api?.Code;
            if (api?.Message?.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase) == true
                || code?.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase) == true)
                code = "APP_UPDATE";

            if (ok && op == "activate")
                UpdateStateOnOnlineSuccess(licenseKey, TierFromKey(licenseKey));

            return new LicenseValidationResult
            {
                IsValid = ok,
                ErrorMessage = api?.Message,
                ErrorCode = code,
                Data = api,
                Tier = ok ? TierFromKey(licenseKey) : LicenseTier.Free
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"License {op} error: {ex}");
            return new LicenseValidationResult { IsValid = false, ErrorCode = "NETWORK_ERROR" };
        }
    }

    /// <summary>
    /// Single source of truth for "is the stored license currently valid", with offline grace.
    /// Returns the effective tier (Free when invalid).
    /// </summary>
    public static async Task<LicenseTier> GetEffectiveTierAsync()
    {
        SecurityHelper.CheckDebugger();
        if (!SecurityHelper.ValidateIntegrity()) return LicenseTier.Free;
        if (!HasLocalLicense()) return LicenseTier.Free;

        string? key = LoadLicense();
        if (string.IsNullOrEmpty(key)) return LicenseTier.Free;

        var verdict = await VerifyOnServerAsync(key).ConfigureAwait(false);
        switch (verdict)
        {
            case ServerVerdict.Success:
                var tier = TierFromKey(key);
                UpdateStateOnOnlineSuccess(key, tier);
                return tier;
            case ServerVerdict.Rejected:
                return LicenseTier.Free;
            default: // Unreachable -> offline grace
                return IsWithinOfflineGrace(key, out var graceTier) ? graceTier : LicenseTier.Free;
        }
    }

    private static async Task<ServerVerdict> VerifyOnServerAsync(string licenseKey)
    {
        try
        {
            using var content = BuildForm("verify", licenseKey, null);
            using var response = await Http.PostAsync(BaseApiUrl, content).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                return code >= 500 ? ServerVerdict.Unreachable : ServerVerdict.Rejected;
            }

            var api = Deserialize(body);
            return api?.IsSuccess == true ? ServerVerdict.Success : ServerVerdict.Rejected;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return ServerVerdict.Unreachable;
        }
        catch
        {
            return ServerVerdict.Rejected;
        }
    }

    private static FormUrlEncodedContent BuildForm(string op, string licenseKey, string? productId)
    {
        var fields = new Dictionary<string, string>
        {
            ["fslm_v2_api_request"] = op,
            ["fslm_api_key"] = Secrets.ApiKey,
            ["license_key"] = licenseKey.Trim(),
            ["device_id"] = HardwareId.Get()
        };
        // The server must reject a key that doesn't belong to this product (see docs/DEPLOYMENT.md).
        if (!string.IsNullOrWhiteSpace(productId)) fields["product_id"] = productId.Trim();
        return new FormUrlEncodedContent(fields);
    }

    /// <summary>Common form for the license-gated Modexa endpoints (engine / content key).</summary>
    internal static FormUrlEncodedContent BuildGateForm(string licenseKey, IDictionary<string, string> extra)
    {
        var fields = new Dictionary<string, string>
        {
            ["fslm_api_key"] = Secrets.ApiKey,
            ["license_key"] = licenseKey.Trim(),
            ["device_id"] = HardwareId.Get()
        };
        foreach (var kv in extra) fields[kv.Key] = kv.Value;
        return new FormUrlEncodedContent(fields);
    }

    private static LicenseApiResponse? Deserialize(string body)
    {
        try { return JsonSerializer.Deserialize<LicenseApiResponse>(body); }
        catch { return null; }
    }

    // ---- Local storage (DPAPI) --------------------------------------------------------------

    /// <summary>Stores an activated key (per product for paid mods; productId null for Pro keys).</summary>
    public static void SaveLicense(string licenseKey, string? productId = null)
        => LicenseStore.Add(licenseKey, productId);

    /// <summary>The account's best key (highest tier) — used for verification and the engine download.</summary>
    public static string? LoadLicense()
    {
        MigrateLegacyFile();
        return LicenseStore.Best()?.Key;
    }

    public static void ClearLicense()
    {
        LicenseStore.Clear();
        try { if (File.Exists(LicenseFilePath)) File.Delete(LicenseFilePath); } catch { }
        try { if (File.Exists(StateFilePath)) File.Delete(StateFilePath); } catch { }
    }

    /// <summary>Earlier versions stored one key in license.dat; fold it into the multi-key store.</summary>
    private static void MigrateLegacyFile()
    {
        if (!File.Exists(LicenseFilePath)) return;
        try
        {
            var encrypted = File.ReadAllText(LicenseFilePath);
            string key = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser));
            LicenseStore.MigrateLegacy(key);
            File.Delete(LicenseFilePath);
        }
        catch
        {
            // Unreadable (other user/PC): leave it; the user simply re-enters the key.
        }
    }

    // ---- Offline grace state ----------------------------------------------------------------

    private static string HashKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void UpdateStateOnOnlineSuccess(string licenseKey, LicenseTier tier)
    {
        try
        {
            long now = DateTime.UtcNow.ToBinary();
            var existing = LoadState();
            long lastSeen = now;
            if (existing != null && existing.LastSeenUtc > now)
                lastSeen = existing.LastSeenUtc; // never move LastSeen backwards

            SaveState(new LicenseState
            {
                LicenseKeyHash = HashKey(licenseKey),
                HardwareId = HardwareId.Get(),
                LastOnlineValidationUtc = now,
                LastSeenUtc = lastSeen,
                Tier = tier
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"State update error: {ex.Message}");
        }
    }

    private static bool IsWithinOfflineGrace(string licenseKey, out LicenseTier tier)
    {
        tier = LicenseTier.Free;
        try
        {
            var state = LoadState();
            if (state == null) return false;
            if (!string.Equals(state.HardwareId, HardwareId.Get(), StringComparison.Ordinal)) return false;
            if (!string.Equals(state.LicenseKeyHash, HashKey(licenseKey), StringComparison.Ordinal)) return false;

            var now = DateTime.UtcNow;
            var lastSeen = DateTime.FromBinary(state.LastSeenUtc);
            var lastOnline = DateTime.FromBinary(state.LastOnlineValidationUtc);

            if (now < lastSeen.AddMinutes(-ClockSkewToleranceMinutes)) return false; // clock rolled back
            if (now - lastOnline > TimeSpan.FromDays(OfflineGraceDays)) return false; // grace expired

            if (now.ToBinary() > state.LastSeenUtc)
            {
                state.LastSeenUtc = now.ToBinary();
                SaveState(state);
            }

            tier = state.Tier;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Grace check error: {ex.Message}");
            return false;
        }
    }

    private static void SaveState(LicenseState state)
    {
        try
        {
            string json = JsonSerializer.Serialize(state);
            var encrypted = Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser));
            AppPaths.WriteAllTextAtomic(StateFilePath, encrypted, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SaveState error: {ex.Message}");
        }
    }

    private static LicenseState? LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return null;
            var encrypted = File.ReadAllText(StateFilePath);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<LicenseState>(json);
        }
        catch
        {
            return null;
        }
    }
}
