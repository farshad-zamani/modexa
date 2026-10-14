using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Modexa.Core.Rpf;

/// <summary>
/// Structural editor for an <b>OPEN</b> RPF7 archive: add, replace and delete files (binary or
/// resource), create folders, and read files back. Changes are staged and written by
/// <see cref="Commit"/>: new data is appended at the end of the archive, then the whole TOC is
/// rebuilt (children sorted by name, as the game requires) and written last, so an interrupted
/// commit leaves the previous TOC intact. Existing data is never moved unless the TOC grows into
/// it, in which case the blocking entries are relocated to the end first.
///
/// Large files are never held in memory: they are streamed from their source at commit time, and
/// big compressed data is spilled to a temp file. <see cref="OpenGameArchive"/> opens the game's own
/// (NG/AES encrypted) archives read-only, e.g. to restore an original file.
/// </summary>
public sealed class RpfEditor : IDisposable
{
    private const uint Rsc7Magic = 0x37435352; // "RSC7"
    private const uint MaxBinaryBlock = 0xFFFFFF, MaxResourceBlock = 0x7FFFFF;
    /// <summary>Staged data larger than this is spilled to a temp file instead of kept in memory.</summary>
    private const int SpillBytes = 4 << 20;
    /// <summary>Binary files larger than this are stored uncompressed (streamed) instead of deflated in memory.</summary>
    private const long CompressLimit = 64L << 20;

    private abstract class Node
    {
        public string Name = "";
        public DirNode? Parent;
    }

    private sealed class DirNode : Node
    {
        public readonly List<Node> Children = new();
    }

    private sealed class FileNode : Node
    {
        public bool IsResource;
        public long Block;            // data offset in 512-byte blocks
        public uint FileSize;         // binary: compressed size (0 = stored); resource: total size
        public uint UncompressedSize; // binary only
        public bool Encrypted;        // binary only
        public uint SystemFlags, GraphicsFlags;

        // Staged replacement data (already in stored form): in memory, in a file, or a stream source.
        public byte[]? NewBytes;
        public string? NewFile;
        public Func<Stream>? NewSource;
        public long NewLength;
        public string? SpillFile;     // temp file owned by the editor (deleted after commit)

        public long StoredLength => IsResource ? FileSize : (FileSize != 0 ? FileSize : UncompressedSize);
        public bool IsPending => NewBytes != null || NewFile != null || NewSource != null;
    }

    private readonly string _path;
    private readonly DirNode _root = new();
    private readonly bool _readOnly;
    private GtaKeys? _keys;
    private bool _aes;
    private bool _dirty;
    private bool _structural;              // entries added/removed -> the TOC must be rebuilt
    private List<Node> _loadOrder = new(); // original TOC order, kept when only contents change
    private long _headerBlocks;

    private RpfEditor(string path, bool readOnly)
    {
        _path = path;
        _readOnly = readOnly;
    }

    public string FilePath => _path;

    /// <summary>True when there are staged changes not yet written by <see cref="Commit"/>.</summary>
    public bool HasChanges => _dirty;

    /// <summary>Opens an OPEN (unencrypted TOC) archive for editing.</summary>
    public static RpfEditor Open(string path)
    {
        var ed = new RpfEditor(path, readOnly: false);
        ed.Load(null);
        return ed;
    }

    /// <summary>
    /// Opens any archive read-only, including the game's own NG/AES-encrypted ones (the TOC is
    /// decrypted in memory; the file is never written). Keys may be null for an OPEN archive.
    /// </summary>
    public static RpfEditor OpenGameArchive(string path, GtaKeys? keys)
    {
        var ed = new RpfEditor(path, readOnly: true) { _keys = keys };
        ed.Load(keys);
        return ed;
    }

    // ---- reading -------------------------------------------------------------------------------

    private void Load(GtaKeys? keys)
    {
        using var fs = File.OpenRead(_path);
        using var r = new BinaryReader(fs);
        if (fs.Length < 16 || r.ReadUInt32() != Rpf7.Version) throw new InvalidDataException("Not an RPF7 archive.");
        uint count = r.ReadUInt32(), namesLen = r.ReadUInt32(), enc = r.ReadUInt32();

        byte[] toc = r.ReadBytes(checked((int)count * Rpf7.EntrySize));
        byte[] names = r.ReadBytes(checked((int)namesLen));
        if (toc.Length != count * Rpf7.EntrySize || names.Length != namesLen) throw new InvalidDataException("Truncated RPF header.");

        if (enc is not (Rpf7.EncryptionOpen or Rpf7.EncryptionNone))
        {
            if (keys == null) throw new NotSupportedException("The archive is encrypted; convert it to OPEN first.");
            if (enc == Rpf7.EncryptionAes)
            {
                _aes = true;
                toc = keys.DecryptAes(toc);
                names = keys.DecryptAes(names);
            }
            else
            {
                var key = keys.NgKeyFor(Path.GetFileName(_path), (uint)fs.Length);
                keys.DecryptNg(toc, key);
                keys.DecryptNg(names, key);
            }
            if (count == 0 || BitConverter.ToUInt32(toc, 4) != Rpf7.DirectoryIdentifier)
                throw new InvalidDataException("The archive could not be decrypted with this game's keys.");
        }
        _headerBlocks = (16 + (long)count * Rpf7.EntrySize + namesLen + Rpf7.BlockSize - 1) / Rpf7.BlockSize;

        var nodes = new Node[count];
        var dirRanges = new Dictionary<DirNode, (uint index, uint count)>();
        for (int i = 0; i < count; i++)
        {
            int o = i * Rpf7.EntrySize;
            uint w0 = BitConverter.ToUInt32(toc, o), w1 = BitConverter.ToUInt32(toc, o + 4);
            ulong packed = BitConverter.ToUInt64(toc, o);
            Node n;
            if (w1 == Rpf7.DirectoryIdentifier)
            {
                var d = new DirNode { Name = ReadName(names, w0) };
                dirRanges[d] = (BitConverter.ToUInt32(toc, o + 8), BitConverter.ToUInt32(toc, o + 12));
                n = d;
            }
            else if ((w1 & 0x80000000) == 0)
            {
                uint encType = BitConverter.ToUInt32(toc, o + 12);
                n = new FileNode
                {
                    Name = ReadName(names, (uint)(packed & 0xFFFF)),
                    FileSize = (uint)((packed >> 16) & 0xFFFFFF),
                    Block = (long)((packed >> 40) & 0xFFFFFF),
                    UncompressedSize = BitConverter.ToUInt32(toc, o + 8),
                    Encrypted = encType != 0
                };
            }
            else
            {
                var f = new FileNode
                {
                    IsResource = true,
                    Name = ReadName(names, (uint)(packed & 0xFFFF)),
                    FileSize = (uint)((packed >> 16) & 0xFFFFFF),
                    Block = (long)((packed >> 40) & 0x7FFFFF),
                    SystemFlags = BitConverter.ToUInt32(toc, o + 8),
                    GraphicsFlags = BitConverter.ToUInt32(toc, o + 12)
                };
                if (f.FileSize == 0xFFFFFF) f.FileSize = ReadLargeResourceSize(fs, f.Block);
                n = f;
            }
            nodes[i] = n;
        }

        if (count == 0 || nodes[0] is not DirNode root) throw new InvalidDataException("RPF has no root folder.");
        // The parsed root's children hang off _root (the editor's root node).
        nodes[0] = _root;
        foreach (var (dir, range) in dirRanges)
        {
            var target = ReferenceEquals(dir, root) ? _root : dir;
            for (uint i = 0; i < range.count; i++)
            {
                long idx = range.index + i;
                if (idx <= 0 || idx >= nodes.Length) continue;
                var child = nodes[idx];
                child.Parent = target;
                target.Children.Add(child);
            }
        }

        // Keep the original order only if it is a clean tree (every entry reachable exactly once).
        var reachable = new HashSet<Node>();
        var walk = new Stack<DirNode>();
        walk.Push(_root);
        reachable.Add(_root);
        bool clean = true;
        while (walk.Count > 0 && clean)
            foreach (var c in walk.Pop().Children)
            {
                if (!reachable.Add(c)) { clean = false; break; }
                if (c is DirNode d) walk.Push(d);
            }
        _loadOrder = clean && reachable.Count == nodes.Length ? nodes.ToList() : new List<Node>();
        if (_loadOrder.Count == 0) _structural = true;
    }

    private static uint ReadLargeResourceSize(FileStream fs, long block)
    {
        long keep = fs.Position;
        var buf = new byte[16];
        fs.Position = block * Rpf7.BlockSize;
        fs.ReadExactly(buf);
        fs.Position = keep;
        return (uint)(buf[7] | buf[14] << 8 | buf[5] << 16 | buf[2] << 24);
    }

    private static string ReadName(byte[] names, uint offset)
    {
        if (offset >= names.Length) return "";
        int end = (int)offset;
        while (end < names.Length && names[end] != 0) end++;
        return Encoding.ASCII.GetString(names, (int)offset, end - (int)offset);
    }

    private static string[] Split(string path)
        => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private DirNode? FindDir(string[] parts, int count, bool create)
    {
        var cur = _root;
        for (int i = 0; i < count; i++)
        {
            var next = cur.Children.OfType<DirNode>().FirstOrDefault(d => d.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (next == null)
            {
                if (!create) return null;
                if (cur.Children.OfType<FileNode>().Any(f => f.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase)))
                    throw new IOException($"'{parts[i]}' is a file inside the archive, not a folder.");
                next = new DirNode { Name = parts[i].ToLowerInvariant(), Parent = cur };
                cur.Children.Add(next);
                _dirty = _structural = true;
            }
            cur = next;
        }
        return cur;
    }

    private FileNode? FindFile(string path)
    {
        var parts = Split(path);
        if (parts.Length == 0) return null;
        var dir = FindDir(parts, parts.Length - 1, create: false);
        return dir?.Children.OfType<FileNode>().FirstOrDefault(f => f.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase));
    }

    public bool FileExists(string path) => FindFile(path) != null;

    public bool DirectoryExists(string path)
    {
        var parts = Split(path);
        return FindDir(parts, parts.Length, create: false) != null;
    }

    /// <summary>True if the entry is a binary (non-resource) file.</summary>
    public bool IsBinary(string path) => FindFile(path) is { IsResource: false };

    /// <summary>
    /// A file's content as the game sees it: binary files decrypted + inflated; resources returned
    /// whole, RSC7 header included (the form <see cref="SetFile"/> accepts back).
    /// </summary>
    public byte[] ReadFile(string path)
    {
        var f = FindFile(path) ?? throw new FileNotFoundException($"'{path}' is not in the archive.");
        byte[] stored = ReadStored(f);
        if (f.IsResource)
        {
            // Inside game archives the 16 header bytes are not a usable RSC7 header; rebuild it from
            // the TOC so the result is a standard .ytd/.yft/... file (and re-imports as a resource).
            if (stored.Length >= 16)
            {
                BitConverter.TryWriteBytes(stored.AsSpan(0, 4), Rsc7Magic);
                BitConverter.TryWriteBytes(stored.AsSpan(4, 4), ((f.SystemFlags >> 28) << 4) | (f.GraphicsFlags >> 28));
                BitConverter.TryWriteBytes(stored.AsSpan(8, 4), f.SystemFlags);
                BitConverter.TryWriteBytes(stored.AsSpan(12, 4), f.GraphicsFlags);
            }
            return stored;
        }

        if (f.Encrypted)
        {
            var keys = _keys ?? GtaKeys.Current ?? GtaKeys.LoadForPath(_path);
            if (_aes) stored = keys.DecryptAes(stored);
            else keys.DecryptNg(stored, f.Name, f.UncompressedSize);
        }
        if (f.FileSize == 0) return stored;

        using var ms = new MemoryStream(stored);
        using var inflate = new DeflateStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream((int)Math.Max(f.UncompressedSize, 16));
        inflate.CopyTo(outMs);
        return outMs.ToArray();
    }

    public string ReadText(string path)
    {
        byte[] b = ReadFile(path);
        int skip = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(b, skip, b.Length - skip);
    }

    /// <summary>
    /// SHA-256 (hex) of a file's content as <see cref="ReadFile"/> returns it. Plain stored files are
    /// hashed as a stream, so multi-hundred-MB entries never load into memory.
    /// </summary>
    public string HashFile(string path)
    {
        var f = FindFile(path) ?? throw new FileNotFoundException($"'{path}' is not in the archive.");
        if (f.IsResource || f.Encrypted || f.FileSize != 0)
            return Convert.ToHexString(SHA256.HashData(ReadFile(path)));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var s = OpenStored(f))
        {
            var buf = new byte[1 << 20];
            long left = f.IsPending ? StagedLength(f) : f.StoredLength;
            while (left > 0)
            {
                int n = s.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0) throw new EndOfStreamException();
                hash.AppendData(buf, 0, n);
                left -= n;
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static long StagedLength(FileNode f)
        => f.NewBytes?.LongLength ?? (f.NewSource != null ? f.NewLength : f.NewFile != null ? new FileInfo(f.NewFile).Length : 0);

    /// <summary>The stored bytes of an entry (staged data, or the archive region), as a stream.</summary>
    private Stream OpenStored(FileNode f)
    {
        if (f.NewBytes != null) return new MemoryStream(f.NewBytes, writable: false);
        if (f.NewFile != null) return File.OpenRead(f.NewFile);
        if (f.NewSource != null) return f.NewSource();
        var fs = File.OpenRead(_path);
        fs.Position = f.Block * Rpf7.BlockSize;
        return fs;
    }

    private byte[] ReadStored(FileNode f)
    {
        if (f.NewBytes != null) return (byte[])f.NewBytes.Clone();
        if (f.NewFile != null) return File.ReadAllBytes(f.NewFile);
        long len = f.IsPending ? StagedLength(f) : f.StoredLength;
        var buf = new byte[len];
        using var s = OpenStored(f);
        s.ReadExactly(buf);
        return buf;
    }

    /// <summary>
    /// Copies a nested archive (a stored, uncompressed binary entry such as <c>x64/vehicles.rpf</c>)
    /// out to <paramref name="destFile"/>. Returns the entry's name and size — what the game derives
    /// the nested archive's NG key from.
    /// </summary>
    public (string name, long size) ExtractNestedArchive(string path, string destFile)
    {
        var f = FindFile(path) ?? throw new FileNotFoundException($"'{path}' is not in the archive.");
        if (f.IsResource || f.FileSize != 0 || f.Encrypted)
            throw new InvalidDataException($"'{path}' is not a nested archive.");
        long len = f.IsPending ? StagedLength(f) : f.UncompressedSize;
        using (var src = OpenStored(f))
        using (var dst = File.Create(destFile))
            CopyBytes(src, dst, len, null);
        return (f.Name, len);
    }

    // ---- editing -------------------------------------------------------------------------------

    private void EnsureWritable()
    {
        if (_readOnly) throw new InvalidOperationException("This archive was opened read-only.");
    }

    private FileNode GetOrAddFile(string path)
    {
        var parts = Split(path);
        if (parts.Length == 0) throw new ArgumentException("Empty archive path.", nameof(path));
        var dir = FindDir(parts, parts.Length - 1, create: true)!;
        string name = parts[^1];

        var f = dir.Children.OfType<FileNode>().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (f == null)
        {
            if (dir.Children.OfType<DirNode>().Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"'{path}' is a folder inside the archive.");
            f = new FileNode { Name = name.ToLowerInvariant(), Parent = dir };
            dir.Children.Add(f);
            _structural = true;
        }
        return f;
    }

    /// <summary>Adds or replaces a file. RSC7 data becomes a resource entry; other data a binary entry.</summary>
    public void SetFile(string path, byte[] data)
    {
        EnsureWritable();
        var f = GetOrAddFile(path);
        Stage(f, data.Length <= SpillBytes ? (byte[])data.Clone() : data, owned: data.Length <= SpillBytes);
        _dirty = true;
    }

    public void SetText(string path, string text) => SetFile(path, Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Adds or replaces a file from a source that can be opened (a file on disk, an entry of a zip…).
    /// Small or compressible data is processed in memory; large data is streamed straight into the
    /// archive at commit time (so the source must stay readable until then).
    /// </summary>
    public void SetFileFrom(string path, long length, Func<Stream> open)
    {
        EnsureWritable();
        if (length > uint.MaxValue) throw new IOException($"'{path}' is too large to store in an archive.");
        var f = GetOrAddFile(path);

        // Peek the header: RSC7 data becomes a resource entry.
        var head = new byte[16];
        int got;
        using (var s = open()) got = ReadUpTo(s, head);
        bool resource = got == 16 && BitConverter.ToUInt32(head, 0) == Rsc7Magic;

        if (resource || (length <= CompressLimit && !IsRawName(f.Name)))
        {
            var data = new byte[length];
            using (var s = open()) s.ReadExactly(data);
            Stage(f, data, owned: true);
        }
        else
        {
            ClearStaged(f);
            f.IsResource = false;
            f.Encrypted = false;
            f.FileSize = 0;
            f.UncompressedSize = (uint)length;
            f.NewSource = open;
            f.NewLength = length;
        }
        _dirty = true;
    }

    /// <summary>Adds or replaces a nested archive from a file on disk (stored raw, streamed on commit).</summary>
    public void SetNestedArchive(string path, string sourceFile)
    {
        EnsureWritable();
        long len = new FileInfo(sourceFile).Length;
        if (len > uint.MaxValue) throw new IOException("Nested archive is too large.");
        var f = GetOrAddFile(path);
        ClearStaged(f);
        f.IsResource = false;
        f.Encrypted = false;
        f.FileSize = 0;
        f.UncompressedSize = (uint)len;
        f.NewFile = sourceFile;
        _dirty = true;
    }

    /// <summary>True when a staged file will be streamed from its source at commit (not processed in memory).</summary>
    public bool IsStreamed(string path) => FindFile(path) is { NewSource: not null };

    public bool DeleteFile(string path)
    {
        EnsureWritable();
        var f = FindFile(path);
        if (f == null) return false;
        ClearStaged(f);
        f.Parent!.Children.Remove(f);
        _dirty = _structural = true;
        return true;
    }

    /// <summary>Files the game streams as they are (never deflated inside the archive).</summary>
    private static bool IsRawName(string name)
        => name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".awc", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".bik", StringComparison.OrdinalIgnoreCase);

    private void Stage(FileNode f, byte[] data, bool owned)
    {
        ClearStaged(f);
        uint len = (uint)data.Length;
        if (data.Length >= 16 && BitConverter.ToUInt32(data, 0) == Rsc7Magic)
        {
            var stored = data;
            if (len >= 0xFFFFFF)
            {
                if (!owned) stored = (byte[])data.Clone();
                stored[7] = (byte)len; stored[14] = (byte)(len >> 8); stored[5] = (byte)(len >> 16); stored[2] = (byte)(len >> 24);
            }
            f.IsResource = true;
            f.SystemFlags = BitConverter.ToUInt32(data, 8);
            f.GraphicsFlags = BitConverter.ToUInt32(data, 12);
            f.FileSize = len;
            f.UncompressedSize = 0;
            f.Encrypted = false;
            Keep(f, stored);
            return;
        }

        f.IsResource = false;
        f.Encrypted = false;
        f.UncompressedSize = len;
        if (!IsRawName(f.Name))
        {
            byte[] packed = Deflate(data);
            if (packed.Length < 0xFFFFFF && packed.Length < data.Length)
            {
                f.FileSize = (uint)packed.Length;
                Keep(f, packed);
                return;
            }
        }
        f.FileSize = 0;
        Keep(f, data);
    }

    /// <summary>Keeps staged bytes in memory, or in a temp file when they are big.</summary>
    private static void Keep(FileNode f, byte[] stored)
    {
        if (stored.Length <= SpillBytes) { f.NewBytes = stored; return; }
        string dir = Path.Combine(AppPaths.CacheDir, "rpf-stage");
        AppPaths.EnsureDir(dir);
        string file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(file, stored);
        f.SpillFile = file;
        f.NewFile = file;
    }

    private static void ClearStaged(FileNode f)
    {
        if (f.SpillFile != null) TryDelete(f.SpillFile);
        f.NewBytes = null;
        f.NewFile = null;
        f.NewSource = null;
        f.NewLength = 0;
        f.SpillFile = null;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    // ---- layout --------------------------------------------------------------------------------

    private sealed record Layout(List<Node> Order, Dictionary<DirNode, (uint index, uint count)> Ranges,
        byte[] Names, Dictionary<string, uint> NameOffsets, long HeaderBytes, long HeaderBlocks);

    /// <summary>
    /// Entry order + names. Content-only changes keep the original TOC order (entry indexes stay
    /// stable); added/removed entries rebuild it: root first, each folder's children contiguous and
    /// sorted by name, as the game requires.
    /// </summary>
    private Layout BuildLayout(bool structural)
    {
        var order = new List<Node>();
        var ranges = new Dictionary<DirNode, (uint index, uint count)>();
        if (!structural)
        {
            order.AddRange(_loadOrder);
            var at = new Dictionary<Node, int>();
            for (int i = 0; i < order.Count; i++) at[order[i]] = i;
            foreach (var dir in order.OfType<DirNode>())
                ranges[dir] = dir.Children.Count == 0 ? (0u, 0u) : ((uint)dir.Children.Min(c => at[c]), (uint)dir.Children.Count);
        }
        else
        {
            order.Add(_root);
            var stack = new Stack<DirNode>();
            stack.Push(_root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                var kids = dir.Children.ToList();
                kids.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                ranges[dir] = ((uint)order.Count, (uint)kids.Count);
                foreach (var k in kids)
                {
                    order.Add(k);
                    if (k is DirNode d) stack.Push(d);
                }
            }
        }

        // Names blob (deduplicated, padded to 16 bytes).
        var nameOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        using var namesMs = new MemoryStream();
        foreach (var n in order)
        {
            if (nameOffsets.ContainsKey(n.Name)) continue;
            nameOffsets[n.Name] = (uint)namesMs.Length;
            byte[] nb = Encoding.ASCII.GetBytes(n.Name);
            namesMs.Write(nb);
            namesMs.WriteByte(0);
        }
        while (namesMs.Length % 16 != 0) namesMs.WriteByte(0);
        byte[] names = namesMs.ToArray();
        if (order.OfType<FileNode>().Any(f => nameOffsets[f.Name] > 0xFFFF))
            throw new IOException("The archive has too many file names.");

        long headerBytes = 16 + (long)order.Count * Rpf7.EntrySize + names.Length;
        long headerBlocks = (headerBytes + Rpf7.BlockSize - 1) / Rpf7.BlockSize;
        return new Layout(order, ranges, names, nameOffsets, headerBytes, headerBlocks);
    }

    private static void WriteHeader(Stream fs, Layout l)
    {
        var toc = new byte[l.Order.Count * Rpf7.EntrySize];
        for (int i = 0; i < l.Order.Count; i++)
        {
            var span = toc.AsSpan(i * Rpf7.EntrySize, Rpf7.EntrySize);
            switch (l.Order[i])
            {
                case DirNode d:
                    var (ci, cc) = l.Ranges[d];
                    BitConverter.TryWriteBytes(span[0..4], l.NameOffsets[d.Name]);
                    BitConverter.TryWriteBytes(span[4..8], Rpf7.DirectoryIdentifier);
                    BitConverter.TryWriteBytes(span[8..12], ci);
                    BitConverter.TryWriteBytes(span[12..16], cc);
                    break;
                case FileNode { IsResource: true } r:
                    uint rs = Math.Min(r.FileSize, 0xFFFFFFu);
                    ulong rp = l.NameOffsets[r.Name] | ((ulong)rs << 16) | ((ulong)((r.Block & 0x7FFFFF) | 0x800000) << 40);
                    BitConverter.TryWriteBytes(span[0..8], rp);
                    BitConverter.TryWriteBytes(span[8..12], r.SystemFlags);
                    BitConverter.TryWriteBytes(span[12..16], r.GraphicsFlags);
                    break;
                case FileNode b:
                    ulong bp = l.NameOffsets[b.Name] | ((ulong)(b.FileSize & 0xFFFFFF) << 16) | ((ulong)(b.Block & 0xFFFFFF) << 40);
                    BitConverter.TryWriteBytes(span[0..8], bp);
                    BitConverter.TryWriteBytes(span[8..12], b.UncompressedSize);
                    BitConverter.TryWriteBytes(span[12..16], b.Encrypted ? 1u : 0u);
                    break;
            }
        }

        fs.Position = 0;
        using var w = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);
        w.Write(Rpf7.Version);
        w.Write((uint)l.Order.Count);
        w.Write((uint)l.Names.Length);
        w.Write(Rpf7.EncryptionOpen);
        w.Write(toc);
        w.Write(l.Names);
        long pad = l.HeaderBlocks * Rpf7.BlockSize - l.HeaderBytes;
        if (pad > 0) w.Write(new byte[pad]);
    }

    private static void CheckAddressable(IEnumerable<FileNode> files)
    {
        foreach (var f in files)
        {
            long limit = f.IsResource ? MaxResourceBlock : MaxBinaryBlock;
            if (f.Block > limit) throw new IOException("The archive grew beyond the size the game can address.");
        }
    }

    // ---- commit --------------------------------------------------------------------------------

    /// <summary>Writes staged changes. No-op when nothing changed.</summary>
    /// <param name="streamed">Reports the bytes copied so far from streamed sources (large files).</param>
    public void Commit(IProgress<long>? streamed = null)
    {
        EnsureWritable();
        if (!_dirty) return;

        var l = BuildLayout(_structural);
        var files = l.Order.OfType<FileNode>().ToList();

        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // New data never goes below the (possibly grown) header, even in a tiny archive.
            long end = Math.Max(Rpf7.AlignUp(fs.Length, Rpf7.BlockSize), l.HeaderBlocks * Rpf7.BlockSize);

            // Relocate untouched entries that the (possibly bigger) header would overwrite.
            foreach (var f in files.Where(f => !f.IsPending && f.Block < l.HeaderBlocks).OrderBy(f => f.Block))
            {
                long len = f.StoredLength;
                fs.Position = f.Block * Rpf7.BlockSize;
                var buf = new byte[len];
                fs.ReadExactly(buf);
                fs.Position = end;
                fs.Write(buf);
                f.Block = end / Rpf7.BlockSize;
                end = Rpf7.AlignUp(end + len, Rpf7.BlockSize);
            }

            // Append staged data.
            long copied = 0;
            foreach (var f in files.Where(f => f.IsPending))
            {
                fs.Position = end;
                long len;
                if (f.NewBytes != null)
                {
                    fs.Write(f.NewBytes);
                    len = f.NewBytes.Length;
                }
                else if (f.NewFile != null)
                {
                    using var src = File.OpenRead(f.NewFile);
                    len = src.Length;
                    CopyBytes(src, fs, len, null);
                }
                else
                {
                    using var src = f.NewSource!();
                    len = f.NewLength;
                    long start = copied;
                    CopyBytes(src, fs, len, n => streamed?.Report(start + n));
                    copied += len;
                }
                f.Block = end / Rpf7.BlockSize;
                end = Rpf7.AlignUp(end + len, Rpf7.BlockSize);
            }
            if (fs.Length < end) fs.SetLength(end);
            CheckAddressable(files);

            // Data is safely on disk; now write the new header (header-last = crash-safe).
            fs.Flush();
            WriteHeader(fs, l);
            fs.Flush(true);
        }

        foreach (var f in files) ClearStaged(f);
        _loadOrder = l.Order;
        _headerBlocks = l.HeaderBlocks;
        _dirty = _structural = false;
    }

    /// <summary>Discards staged changes (and their temp files).</summary>
    public void Dispose()
    {
        foreach (var f in AllFiles()) if (f.IsPending) ClearStaged(f);
    }

    private IEnumerable<FileNode> AllFiles()
    {
        var stack = new Stack<DirNode>();
        stack.Push(_root);
        while (stack.Count > 0)
            foreach (var c in stack.Pop().Children)
            {
                if (c is DirNode d) stack.Push(d);
                else yield return (FileNode)c;
            }
    }

    // ---- compaction ----------------------------------------------------------------------------

    /// <summary>
    /// Bytes in the archive no entry uses any more: every replace appends the new data and leaves
    /// the old copy behind (exactly like OpenIV). Valid when there are no staged changes.
    /// </summary>
    public long DeadBytes()
    {
        long live = _headerBlocks * Rpf7.BlockSize + AllFiles().Sum(f => Rpf7.AlignUp(f.StoredLength, Rpf7.BlockSize));
        return Math.Max(0, new FileInfo(_path).Length - live);
    }

    /// <summary>
    /// Rewrites the archive without the dead space (the "defragment" OpenIV offers). Written to a
    /// temp file first and swapped in at the end, so a failure leaves the archive untouched.
    /// </summary>
    public void Compact(IProgress<long>? copied = null)
    {
        EnsureWritable();
        if (_dirty) throw new InvalidOperationException("Commit or discard the changes before compacting.");

        var l = BuildLayout(structural: true);
        var files = l.Order.OfType<FileNode>().OrderBy(f => f.Block).ToList();
        var oldBlocks = files.ToDictionary(f => f, f => f.Block);
        string tmp = _path + ".modexa-compact";
        try
        {
            using (var src = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                long end = l.HeaderBlocks * Rpf7.BlockSize, done = 0;
                dst.SetLength(end);
                foreach (var f in files)
                {
                    long len = f.StoredLength;
                    src.Position = f.Block * Rpf7.BlockSize;
                    dst.Position = end;
                    long start = done;
                    CopyBytes(src, dst, len, n => copied?.Report(start + n));
                    done += len;
                    f.Block = end / Rpf7.BlockSize;
                    end = Rpf7.AlignUp(end + len, Rpf7.BlockSize);
                }
                dst.SetLength(end);
                CheckAddressable(files);
                WriteHeader(dst, l);
                dst.Flush(true);
            }
            File.Move(tmp, _path, overwrite: true);
            _loadOrder = l.Order;
            _headerBlocks = l.HeaderBlocks;
            _structural = false;
        }
        catch
        {
            foreach (var (f, block) in oldBlocks) f.Block = block;
            TryDelete(tmp);
            throw;
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static int ReadUpTo(Stream s, byte[] buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read;
    }

    private static void CopyBytes(Stream src, Stream dst, long count, Action<long>? progress)
    {
        var buf = new byte[1 << 20];
        long done = 0, lastReport = 0;
        while (done < count)
        {
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, count - done));
            if (n <= 0) throw new EndOfStreamException();
            dst.Write(buf, 0, n);
            done += n;
            if (progress != null && (done - lastReport >= 4 << 20 || done == count))
            {
                lastReport = done;
                progress(done);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
