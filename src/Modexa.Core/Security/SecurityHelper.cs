using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Modexa.Core.Security;

/// <summary>
/// Lightweight anti-debug / anti-tamper / integrity checks. Mirrors the Backup Manager's approach:
/// cheap debugger checks run often; the heavy process scan runs once; the exe-size check is baked
/// in at publish time (0 = disabled for dev builds). Failures are reported to the caller, which
/// decides whether to shut down.
/// </summary>
public static class SecurityHelper
{
    // The publish pipeline injects the real single-file exe size here; 0 disables the check so
    // dev/debug builds never close over a size mismatch. Keeping the field a long means swapping
    // the value doesn't change IL size, which keeps the published size stable.
    private const long ExpectedExeSize = 0; /*AUTO_EXE_SIZE*/

    // Exact (extension-less) process names of debuggers / dumpers. Exact match avoids closing the
    // app because an unrelated process merely contains a substring like "ida".
    private static readonly string[] DebuggerProcessNames =
    {
        "ollydbg", "x64dbg", "x32dbg", "ida", "ida64", "windbg",
        "dnspy", "dnspy-x86", "de4dot", "megadumper", "scylla", "cheatengine"
    };

    private static bool _integrityVerified;
    private static bool _debuggerProcScanned;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref bool pbDebuggerPresent);

    /// <summary>True when the process looks untampered. Cached after the first success.</summary>
    public static bool ValidateIntegrity()
    {
        if (_integrityVerified)
            return true;

        try
        {
#pragma warning disable CS0162 // ExpectedExeSize is 0 in source; the publish pipeline bakes in the real size.
            if (ExpectedExeSize > 0)
            {
                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                    return false;

                var exeInfo = new FileInfo(exePath);
                if (!exeInfo.Exists || exeInfo.Length != ExpectedExeSize)
                    return false;
            }
#pragma warning restore CS0162

            if (IsDebuggerPresent() || IsProcessTampered())
                return false;

            _integrityVerified = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Exits the process if a debugger is attached or a known debugger/dumper is running.
    /// The heavy enumeration runs only once per session.
    /// </summary>
    public static void CheckDebugger()
    {
        if (IsDebuggerPresent())
            Environment.Exit(-1);

        if (_debuggerProcScanned)
            return;
        _debuggerProcScanned = true;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                string name = process.ProcessName;
                foreach (var dbg in DebuggerProcessNames)
                {
                    if (string.Equals(name, dbg, StringComparison.OrdinalIgnoreCase))
                        Environment.Exit(-1);
                }
            }
            catch { /* some processes deny access; ignore them */ }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static bool IsDebuggerPresent()
    {
        bool present = false;
        try
        {
            CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, ref present);
        }
        catch
        {
            // Fall back to the managed check only.
        }
        return present || Debugger.IsAttached;
    }

    private static bool IsProcessTampered()
    {
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                string n = module.ModuleName ?? string.Empty;
                if (n.Contains("injected", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("hack", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("crack", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return true;
        }
    }
}
