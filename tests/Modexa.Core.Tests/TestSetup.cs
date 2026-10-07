using System.IO;
using System.Runtime.CompilerServices;

namespace Modexa.Core.Tests;

internal static class TestSetup
{
    /// <summary>Keeps every test away from the real %LocalAppData%\Modexa (installed mods, backups…).</summary>
    [ModuleInitializer]
    internal static void Init()
        => AppPaths.UseDataDirForTests(Path.Combine(Path.GetTempPath(), "modexa_tests_" + Guid.NewGuid().ToString("N")));
}
