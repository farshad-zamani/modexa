using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Modexa.Core.Rpf;

/// <summary>The game's archive keys could not be obtained (no/unknown game exe).</summary>
public sealed class GtaKeysException : Exception
{
    public GtaKeysException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// GTA V archive key material (AES key, NG keys + decrypt tables, name-hash LUT).
///
/// Nothing usable ships with Modexa. The embedded bundle (<c>Assets/gtav-keys.dat</c>, the
/// "magic.dat" of the MIT-licensed CodeWalker project) is itself AES-encrypted with the game's own
/// AES key, which is located in the user's GTA5.exe / GTA5_Enhanced.exe by its public SHA-1
/// fingerprint. So the keys only exist on a machine that has the game installed.
/// </summary>
public sealed class GtaKeys
{
    public const int NgKeyCount = 101;

    /// <summary>SHA-1 of the 32-byte PC AES key (public; it identifies the key, it isn't the key).</summary>
    private static readonly byte[] AesKeySha1 =
    {
        0xA0, 0x79, 0x61, 0x28, 0xA7, 0x75, 0x72, 0x0A, 0xC2, 0x04,
        0xD9, 0x81, 0x9F, 0x68, 0xC1, 0x72, 0xE3, 0x95, 0x2C, 0x6D
    };

    private static readonly object Gate = new();
    private static GtaKeys? _cached;

    public byte[] Aes { get; }
    /// <summary>101 expanded NG keys, 68 uints (17 rounds x 4) each.</summary>
    public uint[][] NgKeys { get; }
    /// <summary>NG decrypt tables [17 rounds][16][256].</summary>
    public uint[][][] NgTables { get; }
    /// <summary>Character lookup table of the archive-name hash.</summary>
    public byte[] HashLut { get; }

    private GtaKeys(byte[] aes, uint[][] ngKeys, uint[][][] ngTables, byte[] lut)
    {
        Aes = aes;
        NgKeys = ngKeys;
        NgTables = ngTables;
        HashLut = lut;
    }

    /// <summary>Already-loaded keys (null until <see cref="Load"/> succeeded once in this process).</summary>
    public static GtaKeys? Current { get { lock (Gate) return _cached; } }

    /// <summary>
    /// Loads the keys from the GTA V installation in <paramref name="gameFolder"/> (cached for the
    /// process). Scans ~50 MB, so call it off the UI thread.
    /// </summary>
    public static GtaKeys Load(string gameFolder)
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;

            var exes = new[] { "GTA5_Enhanced.exe", "GTA5.exe" }
                .Select(n => Path.Combine(gameFolder, n))
                .Where(File.Exists)
                .ToList();
            if (exes.Count == 0)
                throw new GtaKeysException("GTA5.exe / GTA5_Enhanced.exe was not found in the game folder.");

            byte[]? aes = null;
            foreach (var exe in exes)
            {
                aes = FindAesKey(exe);
                if (aes != null) break;
            }
            if (aes == null)
                throw new GtaKeysException("The game executable did not contain the expected archive key (unsupported game version?).");

            _cached = Unpack(aes);
            return _cached;
        }
    }

    /// <summary>
    /// Loads the keys for an archive anywhere inside a game folder (e.g. <c>…\mods\update\update.rpf</c>)
    /// by walking up to the folder that holds the game executable.
    /// </summary>
    public static GtaKeys LoadForPath(string pathInsideGame)
    {
        if (Current is { } k) return k;
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(pathInsideGame))!);
        for (; dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GTA5.exe")) || File.Exists(Path.Combine(dir.FullName, "GTA5_Enhanced.exe")))
                return Load(dir.FullName);
        }
        throw new GtaKeysException("The archive is encrypted and no GTA V executable was found above it.");
    }

    /// <summary>Searches an exe for the 32-byte block whose SHA-1 is the AES key fingerprint (8-byte aligned).</summary>
    private static byte[]? FindAesKey(string exePath)
    {
        byte[] data;
        try { data = File.ReadAllBytes(exePath); }
        catch (Exception ex) { throw new GtaKeysException($"Could not read {Path.GetFileName(exePath)}: {ex.Message}", ex); }

        const int Chunk = 1 << 20, Align = 8, KeyLen = 32;
        int chunks = (data.Length + Chunk - 1) / Chunk;
        long found = -1;

        Parallel.For(0, chunks, (c, state) =>
        {
            Span<byte> hash = stackalloc byte[20];
            long start = (long)c * Chunk, end = Math.Min(start + Chunk, data.Length - KeyLen + 1);
            for (long pos = start; pos < end; pos += Align)
            {
                if (state.ShouldExitCurrentIteration) return;
                SHA1.HashData(data.AsSpan((int)pos, KeyLen), hash);
                if (hash.SequenceEqual(AesKeySha1))
                {
                    Interlocked.CompareExchange(ref found, pos, -1);
                    state.Stop();
                    return;
                }
            }
        });

        return found < 0 ? null : data.AsSpan((int)found, KeyLen).ToArray();
    }

    /// <summary>Decodes the embedded bundle with the AES key (layout: NG keys, NG tables, LUT, AWC key).</summary>
    private static GtaKeys Unpack(byte[] aes)
    {
        byte[] blob;
        using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("Modexa.Core.Assets.gtav-keys.dat")
                         ?? throw new GtaKeysException("The key bundle is missing from this build."))
        using (var ms = new MemoryStream())
        {
            res.CopyTo(ms);
            blob = ms.ToArray();
        }

        // Undo the four seeded noise layers (System.Random's seeded sequence is stable across .NET versions).
        var rnd = new Random((int)JenkinsOneAtATime(aes));
        int n = blob.Length;
        var noise = new byte[n];
        for (int layer = 0; layer < 4; layer++)
        {
            rnd.NextBytes(noise);
            for (int i = 0; i < n; i++) blob[i] -= noise[i];
        }

        byte[] plain = AesEcbDecrypt(blob, aes);
        byte[] raw;
        try
        {
            using var src = new MemoryStream(plain);
            using var inflate = new DeflateStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();
            inflate.CopyTo(dst);
            raw = dst.ToArray();
        }
        catch (Exception ex)
        {
            throw new GtaKeysException("The key bundle could not be opened with this game's key.", ex);
        }

        const int KeysLen = NgKeyCount * 272, TablesLen = 17 * 16 * 256 * 4, LutLen = 256;
        if (raw.Length < KeysLen + TablesLen + LutLen)
            throw new GtaKeysException("The key bundle is truncated.");

        var keys = new uint[NgKeyCount][];
        for (int k = 0; k < NgKeyCount; k++)
            keys[k] = MemoryMarshal.Cast<byte, uint>(raw.AsSpan(k * 272, 272)).ToArray();

        var tables = new uint[17][][];
        int p = KeysLen;
        for (int r = 0; r < 17; r++)
        {
            tables[r] = new uint[16][];
            for (int t = 0; t < 16; t++, p += 1024)
                tables[r][t] = MemoryMarshal.Cast<byte, uint>(raw.AsSpan(p, 1024)).ToArray();
        }

        byte[] lut = raw.AsSpan(KeysLen + TablesLen, LutLen).ToArray();
        return new GtaKeys(aes, keys, tables, lut);
    }

    private static uint JenkinsOneAtATime(byte[] data)
    {
        uint h = 0;
        foreach (byte b in data)
        {
            h += b;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }

    // ---- ciphers ---------------------------------------------------------------------------------

    /// <summary>AES-256-ECB, no padding; a trailing partial block is left as is (game convention).</summary>
    public static byte[] AesEcbDecrypt(byte[] data, byte[] key)
    {
        var buffer = (byte[])data.Clone();
        int len = data.Length - data.Length % 16;
        if (len == 0) return buffer;
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.DecryptEcb(data.AsSpan(0, len), buffer.AsSpan(0, len), PaddingMode.None);
        return buffer;
    }

    public byte[] DecryptAes(byte[] data) => AesEcbDecrypt(data, Aes);

    /// <summary>GTA V archive-name hash (selects the NG key).</summary>
    public uint NameHash(string name)
    {
        uint result = 0;
        foreach (char ch in name)
        {
            uint temp = 1025 * (HashLut[ch & 0xFF] + result);
            result = (temp >> 6) ^ temp;
        }
        return 32769 * ((9 * result >> 11) ^ (9 * result));
    }

    /// <summary>NG key for a file name + length (archive TOC: archive name + archive size).</summary>
    public uint[] NgKeyFor(string name, uint length)
        => NgKeys[(NameHash(name) + length + (NgKeyCount - 40)) % NgKeyCount];

    /// <summary>Decrypts NG data in place (whole 16-byte blocks; a trailing partial block is plain).</summary>
    public void DecryptNg(Span<byte> data, uint[] key)
    {
        Span<uint> s = stackalloc uint[4];
        int blocks = data.Length / 16;
        for (int b = 0; b < blocks; b++)
        {
            var block = data.Slice(b * 16, 16);
            RoundA(block, key, 0, NgTables[0], s);
            RoundA(block, key, 1, NgTables[1], s);
            for (int r = 2; r <= 15; r++) RoundB(block, key, r, NgTables[r], s);
            RoundA(block, key, 16, NgTables[16], s);
        }
    }

    public void DecryptNg(Span<byte> data, string name, uint length) => DecryptNg(data, NgKeyFor(name, length));

    private static void RoundA(Span<byte> d, uint[] key, int round, uint[][] t, Span<uint> s)
    {
        int k = round * 4;
        s[0] = t[0][d[0]] ^ t[1][d[1]] ^ t[2][d[2]] ^ t[3][d[3]] ^ key[k];
        s[1] = t[4][d[4]] ^ t[5][d[5]] ^ t[6][d[6]] ^ t[7][d[7]] ^ key[k + 1];
        s[2] = t[8][d[8]] ^ t[9][d[9]] ^ t[10][d[10]] ^ t[11][d[11]] ^ key[k + 2];
        s[3] = t[12][d[12]] ^ t[13][d[13]] ^ t[14][d[14]] ^ t[15][d[15]] ^ key[k + 3];
        MemoryMarshal.Cast<uint, byte>(s).CopyTo(d);
    }

    private static void RoundB(Span<byte> d, uint[] key, int round, uint[][] t, Span<uint> s)
    {
        int k = round * 4;
        s[0] = t[0][d[0]] ^ t[7][d[7]] ^ t[10][d[10]] ^ t[13][d[13]] ^ key[k];
        s[1] = t[1][d[1]] ^ t[4][d[4]] ^ t[11][d[11]] ^ t[14][d[14]] ^ key[k + 1];
        s[2] = t[2][d[2]] ^ t[5][d[5]] ^ t[8][d[8]] ^ t[15][d[15]] ^ key[k + 2];
        s[3] = t[3][d[3]] ^ t[6][d[6]] ^ t[9][d[9]] ^ t[12][d[12]] ^ key[k + 3];
        MemoryMarshal.Cast<uint, byte>(s).CopyTo(d);
    }
}
