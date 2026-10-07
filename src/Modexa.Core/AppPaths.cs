using System.IO;
using System.Text;

namespace Modexa.Core;

/// <summary>
/// Resolves the on-disk locations Modexa uses.
///
///   * <see cref="BaseDir"/> — where the executable lives (read-only for us once installed).
///   * <see cref="DataDir"/> — per-user writable data: %LocalAppData%\Modexa. Settings, license,
///     engine, cache and backups live here, so the app works from Program Files / a per-user install
///     without admin rights and nothing litters the install folder.
///
/// Portable mode: if a file named <c>portable.txt</c> sits next to the exe, data stays next to the
/// exe instead (handy for USB sticks and testing).
/// </summary>
public static class AppPaths
{
    public static string BaseDir
    {
        get
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }
            catch
            {
                // Fall through to the current directory on any unexpected failure.
            }
            return Directory.GetCurrentDirectory();
        }
    }

    public static bool IsPortable => File.Exists(Path.Combine(BaseDir, "portable.txt"));

    private static string? _dataDir;

    public static string DataDir
    {
        get
        {
            if (_dataDir != null) return _dataDir;
            string dir = IsPortable
                ? BaseDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Modexa");
            try { Directory.CreateDirectory(dir); } catch { }
            return _dataDir = dir;
        }
    }

    /// <summary>Points all user data at another folder (automated tests only).</summary>
    public static void UseDataDirForTests(string dir)
    {
        Directory.CreateDirectory(dir);
        _dataDir = dir;
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LicensesFile => Path.Combine(DataDir, "licenses.dat");
    public static string StateFile => Path.Combine(DataDir, "state.dat");
    public static string InstalledModsFile => Path.Combine(DataDir, "installed-mods.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>Downloaded prerequisite bundles and temp unpack folders (safe to clear).</summary>
    public static string CacheDir => Path.Combine(DataDir, "cache");

    /// <summary>The license-gated engine module and per-product content keys (DPAPI-protected).</summary>
    public static string EngineDir => Path.Combine(DataDir, "engine");

    /// <summary>Per-install backups of game files Modexa touches, so a revert can restore vanilla.</summary>
    public static string BackupsDir => Path.Combine(DataDir, "backups");

    /// <summary>
    /// Atomic text write: write a temp file then replace, so a crash/power loss never leaves a
    /// half-written or empty target.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string content, Encoding encoding)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content, encoding);

        if (File.Exists(path))
            File.Replace(tempPath, path, null);
        else
            File.Move(tempPath, path);
    }

    public static void WriteAllBytesAtomic(string path, byte[] content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string tempPath = path + ".tmp";
        File.WriteAllBytes(tempPath, content);

        if (File.Exists(path))
            File.Replace(tempPath, path, null);
        else
            File.Move(tempPath, path);
    }

    public static void EnsureDir(string dir)
    {
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }
}
