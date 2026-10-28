using System.IO;
using Modexa.Core.Games;
using Modexa.Core.Install;

namespace Modexa.Core.Prepare;

/// <summary>What an installed prepare step needs.</summary>
public enum StepUpdateState
{
    /// <summary>Up to date (or nothing to tell).</summary>
    None,
    /// <summary>A newer version is available: the step's button becomes "Update".</summary>
    Available,
    /// <summary>The game was updated, but its pack has no newer version yet (scripts may not run).</summary>
    GameUpdatedNoPack
}

/// <summary>
/// Tells when an installed prepare step should be updated:
///
///  * <b>Mod Runner</b> — after the game was updated (another build than the one it was installed
///    for), when the server's pack is newer than the one installed;
///  * <b>GameConfig</b> — only after the game was updated, when the pack has a different config for
///    the new build (a config is made for a game build: a newer pack alone changes nothing);
///  * <b>Menu, Addon Spawner</b> — every 30 days the pack is checked; a newer one is offered (the
///    update replaces the program only, never the user's saves).
///
/// "Newer" is decided by the pack's content hash (SHA-256), not by its date.
/// </summary>
public static class PrepareUpdates
{
    private static readonly TimeSpan ToolCheckInterval = TimeSpan.FromDays(30);

    public static async Task<StepUpdateState> CheckAsync(PrepareManifest manifest, GameId game, GameEdition edition, int build,
        ModInstallRecord record, CancellationToken ct = default)
    {
        if (record.Setup is not { } step) return StepUpdateState.None;
        var bundle = manifest.Resolve(game, step, edition, build);
        if (bundle?.Url == null) return StepUpdateState.None;

        // Installed by an older Modexa: what it was made for is unknown — start counting from now.
        if (record.PreparedForBuild == 0 && build > 0)
        {
            record.PreparedForBuild = build;
            record.LastUpdateCheckUtc = DateTime.UtcNow;
            InstalledModsStore.Update(new[] { record });
            return StepUpdateState.None;
        }

        bool gameUpdated = build > 0 && record.PreparedForBuild > 0 && build != record.PreparedForBuild;
        switch (step)
        {
            case PrepareStep.ModRunner:
            {
                if (!gameUpdated) return StepUpdateState.None;
                return await PackChangedAsync(bundle, record, ct).ConfigureAwait(false)
                    ? StepUpdateState.Available
                    : StepUpdateState.GameUpdatedNoPack;
            }
            case PrepareStep.GameConfig:
            {
                if (!gameUpdated || bundle.GameConfig is not { } gc) return StepUpdateState.None;
                var (file, _) = await BundleCache.GetAsync(bundle, null, ct).ConfigureAwait(false);
                var files = await Task.Run(() => BundleArchive.ListFiles(file, bundle.Password), ct).ConfigureAwait(false);
                var pick = GameConfigVersions.Pick(files, gc, build);
                if (pick == null) return StepUpdateState.None;
                string name = pick.Folder.TrimEnd('/').Split('/')[^1];
                if (string.Equals(name, record.ConfigName, StringComparison.OrdinalIgnoreCase))
                {
                    // The same config still fits the new build: nothing to update.
                    record.PreparedForBuild = build;
                    InstalledModsStore.Update(new[] { record });
                    return StepUpdateState.None;
                }
                return StepUpdateState.Available;
            }
            default:
            {
                if (record.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < ToolCheckInterval)
                    return record.PackHash != null && await CachedNewerAsync(bundle, record).ConfigureAwait(false)
                        ? StepUpdateState.Available : StepUpdateState.None;
                bool changed = await PackChangedAsync(bundle, record, ct).ConfigureAwait(false);
                record.LastUpdateCheckUtc = DateTime.UtcNow;
                InstalledModsStore.Update(new[] { record });
                return changed ? StepUpdateState.Available : StepUpdateState.None;
            }
        }
    }

    /// <summary>The server's pack (downloaded when it changed) differs from the one installed.</summary>
    private static async Task<bool> PackChangedAsync(BundleEntry bundle, ModInstallRecord record, CancellationToken ct)
    {
        if (record.PackHash == null) return false;
        var (file, _) = await BundleCache.GetAsync(bundle, null, ct).ConfigureAwait(false);
        string hash = await Task.Run(() => PrepareService.FileHash(file), ct).ConfigureAwait(false);
        return !string.Equals(hash, record.PackHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Between checks: a newer pack already downloaded (e.g. by another check) is still offered.</summary>
    private static Task<bool> CachedNewerAsync(BundleEntry bundle, ModInstallRecord record)
        => Task.Run(() =>
        {
            string? file = BundleCache.LocalFile(bundle);
            return file != null && !string.Equals(PrepareService.FileHash(file), record.PackHash, StringComparison.OrdinalIgnoreCase);
        });
}
