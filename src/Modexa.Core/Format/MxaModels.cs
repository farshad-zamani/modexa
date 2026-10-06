using System.Text.Json.Serialization;

namespace Modexa.Core.Format;

/// <summary>How a packaged mod is installed.</summary>
public enum MxaModType
{
    /// <summary>Add-on pack: a dlc.rpf registered in dlclist.xml (needs the RPF engine).</summary>
    Addon = 0,
    /// <summary>Loose/copy-paste files placed at mapped destinations in the game folder.</summary>
    Loose = 1,
    /// <summary>An OpenIV .oiv package applied through the OIV engine (Pro).</summary>
    Oiv = 2
}

/// <summary>
/// Public, unencrypted header info the Free build can read to recognize a .mxa and prompt for a
/// license. Contains no secrets and no file contents.
/// </summary>
public sealed class MxaInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("type")] public MxaModType ModType { get; set; }
    [JsonPropertyName("licensed")] public bool Licensed { get; set; } = true;

    /// <summary>Target game key, e.g. "GtaV".</summary>
    [JsonPropertyName("game")] public string Game { get; set; } = "GtaV";
    /// <summary>"Legacy", "Enhanced" or "" for any.</summary>
    [JsonPropertyName("edition")] public string Edition { get; set; } = "";
    [JsonPropertyName("minBuild")] public int MinBuild { get; set; }
    [JsonPropertyName("maxBuild")] public int MaxBuild { get; set; }

    /// <summary>FSLM product id this mod's license belongs to (for display / server checks).</summary>
    [JsonPropertyName("productId")] public string? ProductId { get; set; }

    /// <summary>Optional hint shown in the license prompt (e.g. order #, where to find the key).</summary>
    [JsonPropertyName("licenseHint")] public string? LicenseHint { get; set; }

    [JsonIgnore] public int FormatVersion { get; set; }
}

/// <summary>One file carried by the package and where it installs, relative to the game folder.</summary>
public sealed class MxaFileEntry
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";      // path inside the payload
    [JsonPropertyName("dest")] public string Dest { get; set; } = "";          // path relative to game folder
}

/// <summary>
/// The private install manifest, stored inside the encrypted payload. Describes exactly what to
/// place where. For Addon type, <see cref="DlcName"/> is the dlcpacks folder name to register.
/// </summary>
public sealed class MxaManifest
{
    [JsonPropertyName("type")] public MxaModType ModType { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("files")] public List<MxaFileEntry> Files { get; set; } = new();

    /// <summary>Addon only: dlcpacks folder name (the dlc.rpf is placed under it and registered).</summary>
    [JsonPropertyName("dlcName")] public string? DlcName { get; set; }

    /// <summary>Oiv only: the .oiv/.oivs path inside the payload.</summary>
    [JsonPropertyName("oivPath")] public string? OivPath { get; set; }
}

/// <summary>Result of unpacking: the manifest plus the folder the payload was extracted into.</summary>
public sealed class MxaUnpackResult
{
    public MxaManifest Manifest { get; set; } = new();
    public string ExtractedDir { get; set; } = "";
}
