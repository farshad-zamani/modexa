using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modexa.Core.Licensing;

/// <summary>One activated license key (each paid mod is its own product with its own key).</summary>
public sealed class StoredLicense
{
    public string Key { get; set; } = "";
    /// <summary>FSLM product the key was activated for (null = account/Pro key).</summary>
    public string? ProductId { get; set; }
    public LicenseTier Tier { get; set; }
    public long ActivatedUtc { get; set; }
}

/// <summary>
/// All license keys the user has activated on this machine, DPAPI-encrypted (CurrentUser + app
/// entropy): the file is useless when copied to another account or PC. The account tier is the
/// highest tier among the stored keys.
/// </summary>
public static class LicenseStore
{
    private static readonly object Gate = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Modexa.Licenses.v1");

    public static IReadOnlyList<StoredLicense> All()
    {
        lock (Gate) return Load();
    }

    public static bool Any() => All().Count > 0;

    /// <summary>Highest-tier key (newest wins on ties), used for the account tier and the engine download.</summary>
    public static StoredLicense? Best()
        => All().OrderByDescending(l => l.Tier).ThenByDescending(l => l.ActivatedUtc).FirstOrDefault();

    public static LicenseTier AccountTier => Best()?.Tier ?? LicenseTier.Free;

    /// <summary>
    /// The stored Plus key sold for <paramref name="productId"/> (its suffix is the product code).
    /// Pro keys never open paid mods on their own: each purchased mod has its own key.
    /// </summary>
    public static StoredLicense? ForProduct(string? productId)
        => All().Where(l => LicenseFormat.Covers(l.Key, productId))
                .OrderByDescending(l => l.ActivatedUtc)
                .FirstOrDefault();

    public static void Add(string key, string? productId)
    {
        key = key.Trim().ToUpperInvariant();
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(l.ProductId, productId, StringComparison.OrdinalIgnoreCase));
            list.Add(new StoredLicense
            {
                Key = key,
                ProductId = string.IsNullOrWhiteSpace(productId) ? null : productId.Trim(),
                Tier = LicenseManager.TierFromKey(key),
                ActivatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            Save(list);
        }
    }

    public static void Remove(string key)
    {
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));
            Save(list);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try { if (File.Exists(AppPaths.LicensesFile)) File.Delete(AppPaths.LicensesFile); } catch { }
        }
    }

    /// <summary>Imports the single-key license.dat written by earlier versions.</summary>
    public static void MigrateLegacy(string? legacyKey)
    {
        if (string.IsNullOrWhiteSpace(legacyKey)) return;
        if (All().Any(l => string.Equals(l.Key, legacyKey.Trim(), StringComparison.OrdinalIgnoreCase))) return;
        Add(legacyKey, null);
    }

    // ---- storage ----

    private static List<StoredLicense> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.LicensesFile)) return new();
            byte[] blob = File.ReadAllBytes(AppPaths.LicensesFile);
            byte[] json = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<StoredLicense>>(json) ?? new();
        }
        catch
        {
            return new(); // corrupt or from another user/machine
        }
    }

    private static void Save(List<StoredLicense> list)
    {
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(list);
            AppPaths.WriteAllBytesAtomic(AppPaths.LicensesFile,
                ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser));
        }
        catch { }
    }
}
