using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modexa.Core.Format;

/// <summary>
/// On-disk .mxa container layout and the public header reader. The Free build can read the header
/// (to recognize the file and show its name / prompt for a license) but CANNOT read the payload:
/// the payload is encrypted with a per-product key that only the license server hands out, and the
/// decryption lives in the downloadable engine module (see Engine\IModEngine).
///
/// Layout (format 2, little-endian):
///   magic[8] | formatVersion:i32 | flags:i32 | modType:i32 |
///   metadataLen:i32 | metadata[metadataLen] (UTF-8 JSON, public) |
///   noncePrefix[8] | chunkSize:i32 | payloadLen:i64 |
///   chunk[0] … chunk[n-1], each = ciphertext[min(chunkSize, rest)] | tag[16]
///
/// The payload (a zip: manifest.json + files) is encrypted in chunks with AES-256-GCM, so packages
/// of any size are written and read as a stream (never whole in memory). Chunk i uses the nonce
/// noncePrefix | uint32-BE(i) and the AAD SHA-256(header) | isLastChunk — chunks cannot be
/// reordered, dropped, truncated or moved to another header without failing authentication.
/// Renamed to .zip / .oiv the file is just noise: nothing in it is readable without the key.
/// </summary>
public static class MxaFile
{
    public const string Extension = ".mxa";
    public const int CurrentFormatVersion = 2;

    // Non-ASCII magic so the format isn't trivially greppable as "MXA".
    public static readonly byte[] Magic = { 0x7A, 0x6D, 0x58, 0x41, 0x4B, 0x93, 0x11, 0x01 };

    public const int NoncePrefixSize = 8;
    public const int TagSize = 16;
    public const int DefaultChunkSize = 4 << 20;
    private const int MinChunkSize = 64 * 1024, MaxChunkSize = 64 << 20;

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
        var (info, _) = ReadPublic(r);
        return info;
    }

    private static (MxaInfo info, int flags) ReadPublic(BinaryReader r)
    {
        var magic = r.ReadBytes(8);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not a Modexa package.");

        int formatVersion = r.ReadInt32();
        int flags = r.ReadInt32();
        int modType = r.ReadInt32();
        int metaLen = r.ReadInt32();
        if (metaLen < 0 || metaLen > 1_000_000) throw new InvalidDataException("Corrupt header.");
        byte[] metaBytes = r.ReadBytes(metaLen);
        if (metaBytes.Length != metaLen) throw new InvalidDataException("Corrupt header.");

        var info = JsonSerializer.Deserialize<MxaInfo>(Encoding.UTF8.GetString(metaBytes)) ?? new MxaInfo();
        info.FormatVersion = formatVersion;
        info.Licensed = (flags & FlagLicensed) != 0;
        info.ModType = (MxaModType)modType;
        return (info, flags);
    }

    /// <summary>
    /// Reads the framing of the encrypted payload (everything except the key). Throws
    /// <see cref="InvalidDataException"/> for a damaged file or another format version.
    /// </summary>
    public static MxaPayloadFrame ReadFrame(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        var (info, _) = ReadPublic(r);
        if (info.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"Package format {info.FormatVersion} is not supported (expected {CurrentFormatVersion}).");

        byte[] prefix = r.ReadBytes(NoncePrefixSize);
        int chunkSize = r.ReadInt32();
        long payloadLen = r.ReadInt64();
        long dataOffset = fs.Position;
        if (prefix.Length != NoncePrefixSize || chunkSize is < MinChunkSize or > MaxChunkSize || payloadLen < 0)
            throw new InvalidDataException("Corrupt header.");
        long expected = dataOffset + payloadLen + ChunkCount(payloadLen, chunkSize) * TagSize;
        if (fs.Length != expected) throw new InvalidDataException("The package is incomplete or damaged.");

        // The header (everything before the chunks) is bound into every chunk's AAD.
        fs.Position = 0;
        byte[] header = new byte[dataOffset];
        fs.ReadExactly(header);
        return new MxaPayloadFrame(info.FormatVersion, SHA256.HashData(header), prefix, chunkSize, payloadLen, dataOffset);
    }

    /// <summary>
    /// Writes the container header (format 2) and leaves <paramref name="output"/> positioned at the
    /// first chunk. Returns the header hash every chunk's AAD starts with. Used by the packer.
    /// </summary>
    public static byte[] WriteHeader(Stream output, MxaInfo info, byte[] metadataJsonUtf8, byte[] noncePrefix,
        int chunkSize, long payloadLength)
    {
        if (noncePrefix.Length != NoncePrefixSize) throw new ArgumentException("Nonce prefix must be 8 bytes.", nameof(noncePrefix));
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(CurrentFormatVersion);
            w.Write(info.Licensed ? FlagLicensed : 0);
            w.Write((int)info.ModType);
            w.Write(metadataJsonUtf8.Length);
            w.Write(metadataJsonUtf8);
            w.Write(noncePrefix);
            w.Write(chunkSize);
            w.Write(payloadLength);
        }
        byte[] header = ms.ToArray();
        output.Write(header);
        return SHA256.HashData(header);
    }

    /// <summary>Number of chunks for a payload (an empty payload still has one, empty, final chunk).</summary>
    public static long ChunkCount(long payloadLength, int chunkSize)
        => payloadLength == 0 ? 1 : (payloadLength + chunkSize - 1) / chunkSize;

    /// <summary>Nonce of chunk <paramref name="index"/>: prefix | uint32 big-endian index.</summary>
    public static byte[] ChunkNonce(byte[] noncePrefix, long index)
    {
        if (index > uint.MaxValue) throw new InvalidDataException("Too many chunks.");
        var nonce = new byte[12];
        noncePrefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), (uint)index);
        return nonce;
    }

    /// <summary>AAD of a chunk: the header hash and whether it is the last chunk.</summary>
    public static byte[] ChunkAad(byte[] headerHash, bool last)
    {
        var aad = new byte[headerHash.Length + 1];
        headerHash.CopyTo(aad, 0);
        aad[^1] = last ? (byte)1 : (byte)0;
        return aad;
    }
}

/// <summary>Framing of a .mxa payload: what is needed (besides the key) to decrypt it chunk by chunk.</summary>
public sealed record MxaPayloadFrame(int FormatVersion, byte[] HeaderHash, byte[] NoncePrefix, int ChunkSize,
    long PayloadLength, long DataOffset)
{
    public long ChunkCount => MxaFile.ChunkCount(PayloadLength, ChunkSize);
}
