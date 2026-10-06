using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Modexa.Core.Security;

/// <summary>
/// Produces a stable 32-char hex device id for license binding. Walks a WMI fallback chain and
/// hashes the first usable value with SHA-256. Unlike the Backup Manager, the last-resort fallback
/// is fully deterministic (no random GUID), so activation can never drift between runs on a machine
/// where WMI is unavailable.
/// </summary>
public static class HardwareId
{
    // WMI queries can take seconds (and block an STA thread), so compute the id once per process.
    private static readonly Lazy<string> Cached = new(Compute, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Get() => Cached.Value;

    private static string Compute()
    {
        try
        {
            var uuid = GetWmiValue("Win32_ComputerSystemProduct", "UUID");
            if (IsValid(uuid)) return Hash(uuid!);

            var diskId = GetWmiValue("Win32_DiskDrive", "SerialNumber");
            if (IsValid(diskId)) return Hash(diskId!);

            var cpuId = GetWmiValue("Win32_Processor", "ProcessorId");
            if (IsValid(cpuId)) return Hash(cpuId!);

            var winId = GetWindowsInstallationId();
            if (IsValid(winId)) return Hash(winId!);

            return Hash(DeterministicFallback());
        }
        catch
        {
            return Hash(DeterministicFallback());
        }
    }

    private static string? GetWmiValue(string className, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {className}");
            foreach (var o in searcher.Get())
            {
                var value = o[property]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
        }
        catch
        {
            // WMI may be disabled/locked down; move on to the next candidate.
        }
        return null;
    }

    private static string? GetWindowsInstallationId()
    {
        try
        {
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var subKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
            var productId = subKey?.GetValue("ProductId")?.ToString();
            var digitalProductId = subKey?.GetValue("DigitalProductId") as byte[];
            if (digitalProductId is { Length: >= 128 })
                return BitConverter.ToString(digitalProductId, 52, 8).Replace("-", "");
            return productId;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValid(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        string[] junk =
        {
            "00000000-0000-0000-0000-000000000000",
            "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
            "AAAAAAA", "XXXXXX", "123456", "DEFAULT"
        };
        foreach (var p in junk)
            if (id.Contains(p, StringComparison.OrdinalIgnoreCase)) return false;

        return id.Length >= 8;
    }

    private static string Hash(string raw)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..32];
    }

    // Deterministic across runs (no Guid), so a WMI-less machine still gets a stable id.
    private static string DeterministicFallback()
        => $"{Environment.MachineName}|{Environment.UserName}|{Environment.OSVersion.Version}";
}
