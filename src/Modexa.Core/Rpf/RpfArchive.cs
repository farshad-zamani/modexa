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
/// NG/AES reading/writing is intentionally out of scope — Modexa's Prepare step installs an OPEN
/// update.rpf, so add-on registration only ever edits OPEN archives.
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

    /// <summary>The raw 16-byte TOC record of an entry (used to undo an in-place replace exactly).</summary>
    public byte[] ReadTocEntry(int index)
    {
        using var fs = File.OpenRead(_path);
        fs.Seek(16 + (long)index * Rpf7.EntrySize, SeekOrigin.Begin);
        var buf = new byte[Rpf7.EntrySize];
        ReadExact(fs, buf);
        return buf;
    }

    /// <summary>
    /// Restores a TOC record captured by <see cref="ReadTocEntry"/>. A replace only appends new data
    /// and repoints the record, so the original bytes are still in the archive — this is a full undo.
    /// </summary>
    public static void WriteTocEntry(string archivePath, int index, byte[] record)
    {
        if (record.Length != Rpf7.EntrySize) throw new ArgumentException("Invalid TOC record.", nameof(record));
        using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(16 + (long)index * Rpf7.EntrySize, SeekOrigin.Begin);
        fs.Write(record, 0, record.Length);
        fs.Flush();
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
                FileOffset = (long)((BitConverter.ToUInt64(toc, o) >> 40) & 0xFFFFFF),
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

    /// <summary>Extracts a binary file's bytes (decompressing if needed).</summary>
    public byte[] Extract(RpfBinaryEntry entry)
    {
        using var fs = File.OpenRead(_path);
        fs.Seek(entry.DataStart, SeekOrigin.Begin);
        byte[] stored = new byte[entry.StoredLength];
        ReadExact(fs, stored);

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
