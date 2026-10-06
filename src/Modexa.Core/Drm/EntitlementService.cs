using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modexa.Core.Install;
using Modexa.Core.Security;

namespace Modexa.Core.Drm;

/// <summary>
/// Writes the machine-bound entitlement + a list of Modexa-protected files into the game folder.
/// The Modexa ASI (loaded at game launch) reads these: if the entitlement decrypts on this machine
/// (DPAPI CurrentUser — non-portable), the protected files stay active; otherwise the ASI renames
/// them inert so a copied game folder runs vanilla. This works even when the Modexa app isn't running.
/// </summary>
public static class EntitlementService
{
    // Lives under the game's mods tree so it travels with the (copied) folder but is useless off-machine.
    private static string DrmDir(string gameFolder) => Path.Combine(gameFolder, "mods", "modexa");
    private static string EntitlementPath(string gameFolder) => Path.Combine(DrmDir(gameFolder), "entitlement.dat");
    private static string ProtectedPath(string gameFolder) => Path.Combine(DrmDir(gameFolder), "protected.json");

    private sealed class Entitlement
    {
        public string Hwid { get; set; } = "";
        public List<string> ProductIds { get; set; } = new();
        public long IssuedUtc { get; set; }
    }

    /// <summary>Records a licensed install's files as protected and (re)writes the entitlement.</summary>
    public static void RegisterInstall(string gameFolder, ModInstallRecord record, string? productId)
    {
        AppPaths.EnsureDir(DrmDir(gameFolder));

        // Append this mod's files (and any dlc.rpf) to the protected list.
        var list = LoadProtected(gameFolder);
        foreach (var f in record.Files)
            if (!list.Contains(f.Dest, StringComparer.OrdinalIgnoreCase))
                list.Add(f.Dest);
        SaveProtected(gameFolder, list);

        // Write/refresh the machine-bound entitlement.
        var ent = LoadEntitlement(gameFolder) ?? new Entitlement { Hwid = HardwareId.Get() };
        if (!string.IsNullOrWhiteSpace(productId) && !ent.ProductIds.Contains(productId!))
            ent.ProductIds.Add(productId!);
        ent.IssuedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SaveEntitlement(gameFolder, ent);
    }

    /// <summary>
    /// Removes the entitlement so the ASI deactivates protected content on next launch. Called when
    /// the license is found invalid (e.g. during the periodic re-check).
    /// </summary>
    public static void RevokeEntitlement(string gameFolder)
    {
        try { if (File.Exists(EntitlementPath(gameFolder))) File.Delete(EntitlementPath(gameFolder)); }
        catch { }
    }

    public static bool HasEntitlement(string gameFolder) => File.Exists(EntitlementPath(gameFolder));

    // ---- storage ----

    private static List<string> LoadProtected(string gameFolder)
    {
        try
        {
            if (File.Exists(ProtectedPath(gameFolder)))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ProtectedPath(gameFolder))) ?? new();
        }
        catch { }
        return new();
    }

    private static void SaveProtected(string gameFolder, List<string> list)
    {
        try
        {
            // Normalize to forward slashes so the native ASI reads consistent paths.
            var norm = list.Select(p => p.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllText(ProtectedPath(gameFolder), JsonSerializer.Serialize(norm));
        }
        catch { }
    }

    private static Entitlement? LoadEntitlement(string gameFolder)
    {
        try
        {
            if (!File.Exists(EntitlementPath(gameFolder))) return null;
            byte[] blob = File.ReadAllBytes(EntitlementPath(gameFolder));
            byte[] json = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Entitlement>(Encoding.UTF8.GetString(json));
        }
        catch { return null; }
    }

    private static void SaveEntitlement(string gameFolder, Entitlement ent)
    {
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(ent);
            byte[] blob = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(EntitlementPath(gameFolder), blob);
        }
        catch { }
    }
}
