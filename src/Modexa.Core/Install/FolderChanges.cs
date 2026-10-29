using System.IO;
using Modexa.Core.Diagnostics;
using Modexa.Core.Engine;

namespace Modexa.Core.Install;

/// <summary>
/// Brackets every change Modexa makes to a game folder (installing, removing, preparing), so the
/// engine module — when one is installed — can step in before and after. Changes nest (removing all
/// mods removes each one): only the outermost begin and end reach the engine.
/// </summary>
public static class FolderChanges
{
    private static readonly Dictionary<string, int> Depth = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The installed engine (null when none). Tests point this elsewhere.</summary>
    public static Func<IModEngine?> Engine { get; set; } = EngineModule.Load;

    private static string Key(string folder)
    {
        try { return Path.GetFullPath(folder).TrimEnd('\\', '/'); }
        catch { return folder; }
    }

    public static bool InScope(string gameFolder)
    {
        lock (Depth) return Depth.ContainsKey(Key(gameFolder));
    }

    public static void Begin(string gameFolder)
    {
        string key = Key(gameFolder);
        lock (Depth)
        {
            Depth[key] = Depth.TryGetValue(key, out int d) ? d + 1 : 1;
            if (d > 0) return;
        }
        Call(e => e.BeginChanges(key), "begin");
    }

    public static void End(string gameFolder)
    {
        string key = Key(gameFolder);
        lock (Depth)
        {
            if (!Depth.TryGetValue(key, out int d)) return;
            if (d > 1) { Depth[key] = d - 1; return; }
            Depth.Remove(key);
        }
        Call(e => e.EndChanges(key), "end");
    }

    /// <summary>An installed mod is about to be removed.</summary>
    public static void Removing(ModInstallRecord record) => Call(e => e.Removing(record), "removing");

    public static T Run<T>(string gameFolder, Func<T> action)
    {
        Begin(gameFolder);
        try { return action(); }
        finally { End(gameFolder); }
    }

    public static void Run(string gameFolder, Action action) => Run(gameFolder, () => { action(); return 0; });

    public static async Task<T> RunAsync<T>(string gameFolder, Func<Task<T>> action)
    {
        await Task.Run(() => Begin(gameFolder)).ConfigureAwait(true);
        try { return await action().ConfigureAwait(true); }
        finally { await Task.Run(() => End(gameFolder)).ConfigureAwait(true); }
    }

    private static void Call(Action<IModEngine> step, string what)
    {
        try
        {
            if (Engine() is { } engine) step(engine);
        }
        catch (Exception ex) { Log.Error("Folder " + what, ex); } // never stop the change itself
    }
}
