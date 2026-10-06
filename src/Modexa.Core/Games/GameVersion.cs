using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace Modexa.Core.Games;

/// <summary>GTA V comes in two incompatible editions with disjoint build-number namespaces.</summary>
public enum GameEdition
{
    Unknown,
    Legacy,   // GTA5.exe (gen8)
    Enhanced  // GTA5_Enhanced.exe / eboot.bin (gen9, 2025+)
}

/// <summary>
/// A detected GTA V version: edition + build + full file version. Build numbers must never be
/// compared across editions, so the edition is part of the identity.
/// </summary>
public sealed record GameVersion(GameEdition Edition, int Build, string FileVersion)
{
    public override string ToString() => $"{Edition} {FileVersion}";

    /// <summary>
    /// Reads edition + version from a GTA V install folder. Returns null if no known exe is found.
    /// </summary>
    public static GameVersion? Detect(string gameFolder)
    {
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            return null;

        string enhancedExe = Path.Combine(gameFolder, "GTA5_Enhanced.exe");
        string legacyExe = Path.Combine(gameFolder, "GTA5.exe");
        bool isEnhanced = File.Exists(enhancedExe) || File.Exists(Path.Combine(gameFolder, "eboot.bin"));

        string exe = isEnhanced && File.Exists(enhancedExe) ? enhancedExe
                   : File.Exists(legacyExe) ? legacyExe
                   : File.Exists(enhancedExe) ? enhancedExe
                   : string.Empty;

        if (exe.Length == 0) return null;

        var edition = isEnhanced ? GameEdition.Enhanced : GameEdition.Legacy;

        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(exe);
            string fileVersion = fvi.FileVersion ?? $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}";
            return new GameVersion(edition, fvi.FileBuildPart, fileVersion);
        }
        catch
        {
            return new GameVersion(edition, 0, "unknown");
        }
    }

    /// <summary>SHA-256 of the main executable — used to pick the exact prerequisite bundle and to
    /// flag cracked/patched executables. Returns null if it cannot be read.</summary>
    public static string? HashExecutable(string gameFolder)
    {
        try
        {
            string enhancedExe = Path.Combine(gameFolder, "GTA5_Enhanced.exe");
            string legacyExe = Path.Combine(gameFolder, "GTA5.exe");
            string exe = File.Exists(enhancedExe) ? enhancedExe : legacyExe;
            if (!File.Exists(exe)) return null;

            using var fs = File.OpenRead(exe);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
        catch
        {
            return null;
        }
    }
}
