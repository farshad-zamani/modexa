using System.IO;
using System.IO.Compression;
using System.Text;

namespace Modexa.Core.Rpf;

/// <summary>A parsed entry in an RPF7 archive.</summary>
public abstract class RpfEntry
{
    public int Index;
    public string Name = "";
    public uint NameOffset;
    public string Path = ""; // full path within the archive, '/'-separated
}

public sealed class RpfDirEntry : RpfEntry
{
    public uint EntriesIndex;
    public uint EntriesCount;
}

public sealed class RpfBinaryEntry : RpfEntry
{
    public long FileOffset;          // in 512-byte blocks from archive start
    public uint FileSize;            // compressed size, or 0 if stored uncompressed
    public uint UncompressedSize;
    public uint EncryptionType;      // 0 = not encrypted
    public bool IsCompressed => FileSize != 0;
    public long DataStart => FileOffset * Rpf7.BlockSize;
    public long StoredLength => IsCompressed ? FileSize : UncompressedSize;
}

public sealed class RpfResourceEntry : RpfEntry
{
    public long FileOffset;
    public uint FileSize;
    public uint SystemFlags;
    public uint GraphicsFlags;
}

/// <summary>
/// Reads an RPF7 archive and edits <b>OPEN</b> archives in place without a full rebuild (efficient on
/// multi-GB files): a replaced file is appended at a 512-aligned EOF and its single TOC entry patched.
/// Encrypted (NG/AES) game archives are first copied to the mods folder and converted to OPEN by
/// <see cref="ModsArchives"/>; structural edits (add/delete files) go through <see cref="RpfEditor"/>.
/// </summary>
public sealed class RpfArchive
{
    private readonly string _path;
    public uint Encryption { get; private set; }
    public List<RpfEntry> Entries { get; } = new();

    private RpfArchive(string path) => _path = path;

    public bool IsOpen => Encryption is Rpf7.EncryptionOpen or Rpf7.EncryptionNone;

    public static RpfArchive Open(string path)
    {
        var rpf = new RpfArchive(path);
        rpf.Read();
        return rpf;
    }

    /// <summary>Encryption tag of an RPF7 file without parsing it; null if it isn't an RPF7.</summary>
    public static uint? PeekEncryption(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (fs.Length < 16 || r.ReadUInt32() != Rpf7.Version) return null;
            r.ReadUInt32(); r.ReadUInt32();
            return r.ReadUInt32();
        }
        catch
        {
            return null;
        }
    }

    private void Read()
    {
        using var fs = File.OpenRead(_path);
        using var r = new BinaryReader(fs);

        uint version = r.ReadUInt32();
        if (version != Rpf7.Version)
            throw new InvalidDataException("Not an RPF7 archive.");
        uint entryCount = r.ReadUInt32();
        uint namesLength = r.ReadUInt32();
        Encryption = r.ReadUInt32();

        if (!IsOpen)
            throw new NotSupportedException(
                "This RPF is encrypted (NG/AES). Modexa edits OPEN archives only — run 'Prepare for mods' first.");

        byte[] tocBytes = r.ReadBytes((int)(entryCount * Rpf7.EntrySize));
        byte[] nameBytes = r.ReadBytes((int)namesLength);

        for (int i = 0; i < entryCount; i++)
            Entries.Add(ParseEntry(tocBytes, i, nameBytes));

        // Resolve names + build full paths from the directory tree (root = entry 0).
        if (Entries.Count > 0 && Entries[0] is RpfDirEntry root)
        {
            root.Name = "";
            root.Path = "";
            BuildPaths(root);
        }
    }

    private static RpfEntry ParseEntry(byte[] toc, int index, byte[] names)
    {
        int o = index * Rpf7.EntrySize;
        uint y = BitConverter.ToUInt32(toc, o);
        uint x = BitConverter.ToUInt32(toc, o + 4);

        RpfEntry entry;
        if (x == Rpf7.DirectoryIdentifier)
        {
            entry = new RpfDirEntry
            {
                NameOffset = y,
                EntriesIndex = BitConverter.ToUInt32(toc, o + 8),
                EntriesCount = BitConverter.ToUInt32(toc, o + 12),
            };
        }
        else if ((x & 0x80000000) == 0)
        {
            ulong packed = BitConverter.ToUInt64(toc, o);
            entry = new RpfBinaryEntry
            {
                NameOffset = (uint)(packed & 0xFFFF),
                FileSize = (uint)((packed >> 16) & 0xFFFFFF),
                FileOffset = (long)((packed >> 40) & 0xFFFFFF),
                UncompressedSize = BitConverter.ToUInt32(toc, o + 8),
                EncryptionType = BitConverter.ToUInt32(toc, o + 12),
            };
        }
        else
        {
            entry = new RpfResourceEntry
            {
                NameOffset = (uint)(y & 0xFFFF),
                FileSize = (uint)((BitConverter.ToUInt64(toc, o) >> 16) & 0xFFFFFF),
                // Bit 23 of the offset field is the "resource" flag, not part of the offset.
                FileOffset = (long)((BitConverter.ToUInt64(toc, o) >> 40) & 0x7FFFFF),
                SystemFlags = BitConverter.ToUInt32(toc, o + 8),
                GraphicsFlags = BitConverter.ToUInt32(toc, o + 12),
            };
        }

        entry.Index = index;
        entry.Name = ReadName(names, entry.NameOffset);
        return entry;
    }

    private static string ReadName(byte[] names, uint offset)
    {
        if (offset >= names.Length) return "";
        int end = (int)offset;
        while (end < names.Length && names[end] != 0) end++;
        return Encoding.ASCII.GetString(names, (int)offset, end - (int)offset);
    }

    private void BuildPaths(RpfDirEntry dir)
    {
        for (uint i = 0; i < dir.EntriesCount; i++)
        {
            int idx = (int)(dir.EntriesIndex + i);
            if (idx < 0 || idx >= Entries.Count) continue;
            var child = Entries[idx];
            child.Path = string.IsNullOrEmpty(dir.Path) ? child.Name : dir.Path + "/" + child.Name;
            if (child is RpfDirEntry cd) BuildPaths(cd);
        }
    }

    public RpfBinaryEntry? FindBinary(string path)
    {
        path = path.Replace('\\', '/').TrimStart('/');
        return Entries.OfType<RpfBinaryEntry>()
            .FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts a binary file's bytes (decrypting + decompressing if needed). Game archives converted
    /// to OPEN keep their per-file NG encryption (e.g. dlclist.xml, gameconfig.xml); those entries are
    /// decrypted with the keys of the game the archive belongs to.
    /// </summary>
    public byte[] Extract(RpfBinaryEntry entry)
    {
        using var fs = File.OpenRead(_path);
        fs.Seek(entry.DataStart, SeekOrigin.Begin);
        byte[] stored = new byte[entry.StoredLength];
        ReadExact(fs, stored);

        if (entry.EncryptionType != 0)
            GtaKeys.LoadForPath(_path).DecryptNg(stored, entry.Name, entry.UncompressedSize);

        if (!entry.IsCompressed) return stored;

        using var ms = new MemoryStream(stored);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream((int)entry.UncompressedSize);
        ds.CopyTo(outMs);
        return outMs.ToArray();
    }

    public string ExtractText(RpfBinaryEntry entry)
    {
        byte[] bytes = Extract(entry);
        // strip a UTF-8 BOM if present
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Replaces an existing binary file's contents (stored uncompressed) by appending the new data at
    /// a 512-aligned EOF and patching the file's TOC entry in place. OPEN archives only.
    /// </summary>
    public void ReplaceFileUncompressed(RpfBinaryEntry entry, byte[] newData)
    {
        if (!IsOpen) throw new NotSupportedException("ReplaceFile requires an OPEN archive.");

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        long newOffset = Rpf7.AlignUp(fs.Length, Rpf7.BlockSize);
        fs.SetLength(newOffset + Rpf7.AlignUp(newData.Length, Rpf7.BlockSize));
        fs.Seek(newOffset, SeekOrigin.Begin);
        fs.Write(newData, 0, newData.Length);
        // remaining bytes up to the 512 boundary are already zero (SetLength grows with zeros)

        // Update the in-memory entry.
        entry.FileOffset = newOffset / Rpf7.BlockSize;
        entry.FileSize = 0;                       // stored uncompressed
        entry.UncompressedSize = (uint)newData.Length;
        entry.EncryptionType = 0;

        // Patch just this entry's 16 bytes in the TOC (TOC starts at offset 16).
        ulong packed = (ulong)entry.NameOffset
                     | ((ulong)entry.FileSize << 16)
                     | ((ulong)(entry.FileOffset & 0xFFFFFF) << 40);
        byte[] entryBytes = new byte[Rpf7.EntrySize];
        BitConverter.GetBytes(packed).CopyTo(entryBytes, 0);
        BitConverter.GetBytes(entry.UncompressedSize).CopyTo(entryBytes, 8);
        BitConverter.GetBytes(entry.EncryptionType).CopyTo(entryBytes, 12);

        long entryPos = 16 + (long)entry.Index * Rpf7.EntrySize;
        fs.Seek(entryPos, SeekOrigin.Begin);
        fs.Write(entryBytes, 0, entryBytes.Length);
        fs.Flush();
    }

    public void ReplaceText(RpfBinaryEntry entry, string text)
        => ReplaceFileUncompressed(entry, Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Rewrites an NG/AES archive's TOC as OPEN in place — the same conversion OpenIV performs when an
    /// archive is copied to the mods folder. File data is untouched (NG-encrypted files stay encrypted
    /// and flagged, which the game reads fine); for AES archives the flagged files are decrypted too,
    /// since an OPEN header no longer says which cipher they used.
    /// </summary>
    /// <param name="keyName">Name the NG key is derived from (the archive's original file/entry name).</param>
    /// <param name="keyLength">Length the NG key is derived from (default: the file's current length).</param>
    /// <returns>False when the archive already was OPEN.</returns>
    public static bool ConvertToOpen(string path, string keyName, GtaKeys keys, long? keyLength = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var r = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);
        if (fs.Length < 16 || r.ReadUInt32() != Rpf7.Version) throw new InvalidDataException("Not an RPF7 archive.");
        uint count = r.ReadUInt32(), namesLen = r.ReadUInt32(), enc = r.ReadUInt32();
        if (enc is Rpf7.EncryptionOpen or Rpf7.EncryptionNone) return false;

        byte[] toc = r.ReadBytes(checked((int)count * Rpf7.EntrySize));
        byte[] names = r.ReadBytes(checked((int)namesLen));
        if (toc.Length != count * Rpf7.EntrySize || names.Length != namesLen) throw new InvalidDataException("Truncated RPF header.");

        bool aes = enc == Rpf7.EncryptionAes;
        if (aes)
        {
            toc = keys.DecryptAes(toc);
            names = keys.DecryptAes(names);
        }
        else
        {
            // Rpf7.EncryptionNg — and, like the game, any unknown tag is treated as NG.
            var key = keys.NgKeyFor(keyName, (uint)(keyLength ?? fs.Length));
            keys.DecryptNg(toc, key);
            keys.DecryptNg(names, key);
        }

        // Plausibility check before anything is written: a wrong key yields garbage.
        if (count == 0 || BitConverter.ToUInt32(toc, 4) != Rpf7.DirectoryIdentifier || namesLen == 0 || names[0] != 0)
            throw new InvalidDataException("The archive could not be decrypted with this game's keys.");
        for (int i = 0; i < count; i++)
        {
            uint w0 = BitConverter.ToUInt32(toc, i * 16), w1 = BitConverter.ToUInt32(toc, i * 16 + 4);
            uint nameOffset = w1 == Rpf7.DirectoryIdentifier ? w0 : w0 & 0xFFFF;
            if (nameOffset >= namesLen) throw new InvalidDataException("The archive could not be decrypted with this game's keys.");
        }

        if (aes)
        {
            for (int i = 0; i < count; i++)
            {
                int o = i * 16;
                uint w1 = BitConverter.ToUInt32(toc, o + 4);
                if (w1 == Rpf7.DirectoryIdentifier || (w1 & 0x80000000) != 0) continue;
                if (BitConverter.ToUInt32(toc, o + 12) == 0) continue;
                ulong packed = BitConverter.ToUInt64(toc, o);
                uint size = (uint)((packed >> 16) & 0xFFFFFF);
                if (size == 0) size = BitConverter.ToUInt32(toc, o + 8);
                long at = (long)((packed >> 40) & 0xFFFFFF) * Rpf7.BlockSize;
                var data = new byte[size];
                fs.Position = at;
                ReadExact(fs, data);
                data = keys.DecryptAes(data);
                fs.Position = at;
                fs.Write(data, 0, data.Length);
                BitConverter.TryWriteBytes(toc.AsSpan(o + 12, 4), 0u);
            }
        }

        fs.Position = 12;
        fs.Write(BitConverter.GetBytes(Rpf7.EncryptionOpen));
        fs.Write(toc);
        fs.Write(names);
        fs.Flush(true);
        return true;
    }

    private static void ReadExact(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }
}
