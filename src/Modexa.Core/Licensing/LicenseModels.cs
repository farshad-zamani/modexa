using System.Text.Json.Serialization;

namespace Modexa.Core.Licensing;

/// <summary>Feature/entitlement tier. Richer tiers also change the app theme.</summary>
public enum LicenseTier
{
    Free = 0,
    Plus = 1,
    Pro = 2
}

/// <summary>Raw FSLM API response (same shape as the Backup Manager backend).</summary>
public sealed class LicenseApiResponse
{
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }

    [JsonIgnore] public bool IsSuccess => string.Equals(Result, "success", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Result of a user-facing activate/verify, with a friendly message for the UI.</summary>
public sealed class LicenseValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public LicenseApiResponse? Data { get; init; }
    public LicenseTier Tier { get; init; } = LicenseTier.Free;
}

/// <summary>DPAPI-encrypted offline-grace state (hardware + key locked, monotonic clock).</summary>
public sealed class LicenseState
{
    public string? LicenseKeyHash { get; set; }
    public string? HardwareId { get; set; }
    public long LastOnlineValidationUtc { get; set; }
    public long LastSeenUtc { get; set; }
    public LicenseTier Tier { get; set; } = LicenseTier.Free;
}
