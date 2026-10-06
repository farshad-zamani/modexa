using System.IO;
using System.Text;

namespace Modexa.Core.Rpf;

/// <summary>
/// Creates a minimal, valid <b>OPEN</b> RPF7 archive from a set of files (all stored uncompressed).
/// Used for tests and as a fallback when an expected archive is missing. Not a full packer — real
/// game archives are produced by the Prepare bundles; Modexa only edits them.
/// </summary>
public static class RpfBuilder
{
    private sealed class Node
    {
        public string Name = "";
        public bool IsFile;
        public byte[]? Data;
        public SortedDictionary<string, Node> Children = new(StringComparer.OrdinalIgnoreCase);
        public int Index;
        public uint NameOffset;
        public long FileOffset;
    }

    public static void CreateOpen(string path, IDictionary<string, byte[]> files)
    {
        var root = new Node { Name = "" };

        // Build the directory tree.
        foreach (var kv in files)
        {
            string[] parts = kv.Key.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                if (!cur.Children.TryGetValue(parts[i], out var next))
                {
                    next = new Node { Name = parts[i] };
                    cur.Children[parts[i]] = next;
                }
                if (last) { next.IsFile = true; next.Data = kv.Value; }
                cur = next;
            }
        }

        // Assign entry indices breadth-first so each directory's children are contiguous.
        var entries = new List<Node> { root };
        var queue = new Queue<Node>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            foreach (var child in dir.Children.Values)
            {
                child.Index = entries.Count;
                entries.Add(child);
                if (!child.IsFile) queue.Enqueue(child);
            }
        }

        // Names blob.
        using var namesMs = new MemoryStream();
        foreach (var e in entries)
        {
            e.NameOffset = (uint)namesMs.Length;
            byte[] nb = Encoding.ASCII.GetBytes(e.Name);
            namesMs.Write(nb, 0, nb.Length);
            namesMs.WriteByte(0);
        }
        byte[] names = namesMs.ToArray();

        int entryCount = entries.Count;
        long tocNames = 16 + (long)entryCount * Rpf7.EntrySize + names.Length;
        long dataStart = Rpf7.AlignUp(tocNames, Rpf7.BlockSize);

        // Lay out file data at 512-aligned offsets.
        long cursor = dataStart;
        foreach (var e in entries.Where(e => e.IsFile))
        {
            e.FileOffset = cursor / Rpf7.BlockSize;
            cursor += Rpf7.AlignUp(e.Data!.Length, Rpf7.BlockSize);
        }
        long totalLen = Math.Max(cursor, dataStart);

        // Precompute each directory's child index range.
        var childRange = new Dictionary<Node, (uint index, uint count)>();
        void ComputeRanges(Node dir)
        {
            if (dir.Children.Count > 0)
            {
                uint first = (uint)dir.Children.Values.Min(c => c.Index);
                childRange[dir] = (first, (uint)dir.Children.Count);
            }
            else childRange[dir] = (0, 0);
            foreach (var c in dir.Children.Values.Where(c => !c.IsFile)) ComputeRanges(c);
        }
        ComputeRanges(root);

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        w.Write(Rpf7.Version);
        w.Write((uint)entryCount);
        w.Write((uint)names.Length);
        w.Write(Rpf7.EncryptionOpen);

        foreach (var e in entries)
        {
            if (!e.IsFile)
            {
                var (ci, cc) = childRange[e];
                w.Write(e.NameOffset);
                w.Write(Rpf7.DirectoryIdentifier);
                w.Write(ci);
                w.Write(cc);
            }
            else
            {
                // Stored uncompressed: FileSize = 0, UncompressedSize = length.
                ulong packed = (e.NameOffset & 0xFFFFUL)
                             | (0UL << 16)
                             | ((ulong)(e.FileOffset & 0xFFFFFF) << 40);
                w.Write(packed);
                w.Write((uint)e.Data!.Length);
                w.Write(0u); // encType
            }
        }

        w.Write(names);

        // Pad to dataStart.
        long pos = 16 + (long)entryCount * Rpf7.EntrySize + names.Length;
        while (pos < dataStart) { w.Write((byte)0); pos++; }

        // File data, each padded to 512.
        foreach (var e in entries.Where(e => e.IsFile))
        {
            w.Write(e.Data!);
            long rem = Rpf7.AlignUp(e.Data!.Length, Rpf7.BlockSize) - e.Data!.Length;
            for (long i = 0; i < rem; i++) w.Write((byte)0);
        }

        w.Flush();
        if (fs.Length < totalLen) fs.SetLength(totalLen);
    }
}
