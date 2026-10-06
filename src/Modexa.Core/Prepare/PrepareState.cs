using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modexa.Core.Prepare;

/// <summary>Which prepare steps were completed for a game folder (drives the "Installed" status).</summary>
public static class PrepareState
{
    public sealed class StepRecord
    {
        public DateTime DoneUtc { get; set; }
        public string? Variant { get; set; }
    }

    private static string FileFor(string gameFolder)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(gameFolder).TrimEnd('\\').ToUpperInvariant())))[..16];
        return Path.Combine(AppPaths.DataDir, "state", $"prepare-{key}.json");
    }

    public static Dictionary<PrepareStep, StepRecord> Load(string gameFolder)
    {
        try
        {
            string f = FileFor(gameFolder);
            if (File.Exists(f))
                return JsonSerializer.Deserialize<Dictionary<PrepareStep, StepRecord>>(File.ReadAllText(f)) ?? new();
        }
        catch { }
        return new();
    }

    public static void MarkDone(string gameFolder, PrepareStep step, string? variant)
    {
        try
        {
            var all = Load(gameFolder);
            all[step] = new StepRecord { DoneUtc = DateTime.UtcNow, Variant = variant };
            string f = FileFor(gameFolder);
            AppPaths.EnsureDir(Path.GetDirectoryName(f)!);
            AppPaths.WriteAllTextAtomic(f, JsonSerializer.Serialize(all), Encoding.UTF8);
        }
        catch { }
    }

    public static void Clear(string gameFolder)
    {
        try { File.Delete(FileFor(gameFolder)); } catch { }
    }
}
