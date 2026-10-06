using Modexa.Core.Licensing;

namespace Modexa.Core.Engine;

/// <summary>
/// Expected SHA-256 of each engine build, baked in by build\release.ps1 at publish time. A download
/// whose hash doesn't match is rejected (tamper / wrong file / MITM). Empty = not pinned (dev builds).
/// </summary>
public static class EngineHashes
{
    public const string Plus = ""; /*AUTO_ENGINE_PLUS*/
    public const string Pro = ""; /*AUTO_ENGINE_PRO*/

    /// <summary>Expected hex hash for a tier, or null when not pinned.</summary>
    public static string? For(LicenseTier tier)
    {
        string h = tier == LicenseTier.Pro ? Pro : Plus;
        return string.IsNullOrWhiteSpace(h) ? null : h;
    }
}
