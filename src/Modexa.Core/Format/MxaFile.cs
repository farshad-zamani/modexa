using System.IO;
using System.Text;
using System.Text.Json;

namespace Modexa.Core.Format;

/// <summary>
/// On-disk .mxa container layout and the public header reader. The Free build can read the header
/// (to recognize the file and show its name / prompt for a license) but CANNOT read the payload:
/// the payload is AES-GCM encrypted and the decryption lives entirely in the downloadable support
/// engine module (see Engine\IModEngine), keyed per product by the license server. This keeps the format opaque to
/// the public source on GitHub.
///
/// Layout (little-endian):
///   magic[8] | formatVersion:i32 | flags:i32 | modType:i32 |
///   metadataLen:i32 | metadata[metadataLen] (UTF-8 JSON, public) |
///   nonce[12] | tag[16] | payloadLen:i64 | payload[payloadLen] (AES-256-GCM ciphertext)
/// </summary>
public static class MxaFile
{
    public const string Extension = ".mxa";
    public const int CurrentFormatVersion = 1;

    // Non-ASCII magic so the format isn't trivially greppable as "MXA".
    public static readonly byte[] Magic = { 0x7A, 0x6D, 0x58, 0x41, 0x4B, 0x93, 0x11, 0x01 };

    public const int NonceSize = 12;
    public const int TagSize = 16;

    private const int FlagLicensed = 1;

    /// <summary>True if the file starts with the .mxa magic.</summary>
    public static bool IsMxaFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[8];
            if (fs.Read(buf) != 8) return false;
            return buf.SequenceEqual(Magic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the public header info. Throws if the file isn't a valid .mxa.</summary>
    public static MxaInfo ReadInfo(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        var magic = r.ReadBytes(8);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not a Modexa package.");

        int formatVersion = r.ReadInt32();
        int flags = r.ReadInt32();
        int modType = r.ReadInt32();
        int metaLen = r.ReadInt32();
        if (metaLen < 0 || metaLen > 1_000_000) throw new InvalidDataException("Corrupt header.");
        byte[] metaBytes = r.ReadBytes(metaLen);

        var info = JsonSerializer.Deserialize<MxaInfo>(Encoding.UTF8.GetString(metaBytes)) ?? new MxaInfo();
        info.FormatVersion = formatVersion;
        info.Licensed = (flags & FlagLicensed) != 0;
        info.ModType = (MxaModType)modType;
        return info;
    }

    /// <summary>
    /// Reads the raw encrypted sections after the public metadata. Used only by the support module's
    /// unpacker; returns the metadata bytes (as AAD), nonce, tag and ciphertext.
    /// </summary>
    public static MxaRawSections ReadRaw(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        var magic = r.ReadBytes(8);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not a Modexa package.");

        _ = r.ReadInt32(); // formatVersion
        _ = r.ReadInt32(); // flags
        _ = r.ReadInt32(); // modType
        int metaLen = r.ReadInt32();
        byte[] metaBytes = r.ReadBytes(metaLen);

        byte[] nonce = r.ReadBytes(NonceSize);
        byte[] tag = r.ReadBytes(TagSize);
        long payloadLen = r.ReadInt64();
        if (payloadLen < 0 || payloadLen > 10L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Corrupt payload length.");
        byte[] payload = r.ReadBytes((int)payloadLen);

        return new MxaRawSections(metaBytes, nonce, tag, payload);
    }

    /// <summary>
    /// Writes a .mxa container. The payload must already be encrypted by the support module's packer;
    /// this method only frames it. Used by the internal packer tool.
    /// </summary>
    public static void Write(string path, MxaInfo info, byte[] metadataJsonUtf8,
        byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        w.Write(Magic);
        w.Write(CurrentFormatVersion);
        w.Write(info.Licensed ? FlagLicensed : 0);
        w.Write((int)info.ModType);
        w.Write(metadataJsonUtf8.Length);
        w.Write(metadataJsonUtf8);
        w.Write(nonce);
        w.Write(tag);
        w.Write((long)ciphertext.Length);
        w.Write(ciphertext);
    }
}

/// <summary>Raw encrypted sections of a .mxa, consumed by the support module.</summary>
public sealed record MxaRawSections(byte[] Metadata, byte[] Nonce, byte[] Tag, byte[] Ciphertext);
