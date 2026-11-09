using System.IO;
using System.IO.Compression;
using System.Text;
using Modexa.Core.Install;
using Modexa.Core.Prepare;
using Xunit;

namespace Modexa.Core.Tests;

public class BundleArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bundle_" + Guid.NewGuid().ToString("N"));
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "archives", "encrypted-stored.rar");
    private const string Password = "fixture-password";

    private static Dictionary<string, byte[]> Contents() => new()
    {
        ["bin/x64/aligned.bin"] = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
        ["bin/x64/unaligned.bin"] = Enumerable.Range(0, 131091).Select(i => (byte)(i % 251)).ToArray(),
        ["r6/scripts/empty.reds"] = Array.Empty<byte>(),
        ["r6/scripts/sample.reds"] = Encoding.UTF8.GetBytes("module Example\r\n// sample\r\n")
    };

    public BundleArchiveTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Encrypted_stored_rar_preserves_every_byte_including_empty_and_unaligned_files()
    {
        var expected = Contents();
        Assert.True(BundleArchive.IsEncrypted(Fixture));
        Assert.Equal(expected.Keys.Order(), BundleArchive.ListFiles(Fixture, Password).Order());
        int count = BundleArchive.Extract(Fixture, Password, key => Path.Combine(_root, key));
        Assert.Equal(expected.Count, count);
        foreach (var (key, bytes) in expected)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, key)));
    }

    [Fact]
    public void Selecting_a_file_after_skipped_encrypted_entries_preserves_its_bytes()
    {
        const string wanted = "r6/scripts/sample.reds";
        string dest = Path.Combine(_root, "selected.reds");
        Assert.Equal(1, BundleArchive.Extract(Fixture, Password, key => key == wanted ? dest : null));
        Assert.Equal(Contents()[wanted], File.ReadAllBytes(dest));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public void Zip_entries_keep_their_exact_bytes(CompressionLevel level)
    {
        string zip = Path.Combine(_root, "bundle.zip");
        var expected = Contents();
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var (key, bytes) in expected)
            {
                using var stream = archive.CreateEntry(key, level).Open();
                stream.Write(bytes);
            }
        string output = Path.Combine(_root, "output");
        Assert.Equal(expected.Count, BundleArchive.Extract(zip, null, key => Path.Combine(output, key)));
        foreach (var (key, bytes) in expected)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(output, key)));
    }

    [Fact]
    public void Cyberpunk_archive_install_and_uninstall_preserve_the_original_files()
    {
        string unpacked = CyberpunkMods.Unpack(Fixture, Path.Combine(_root, "work"), askPassword: _ => Password);
        var plan = CyberpunkMods.Plan(unpacked, "Example", "2.50");
        Assert.Equal(CpPlanKind.Ready, plan.Kind);
        string game = Path.Combine(_root, "game");
        string original = Path.Combine(game, "r6", "scripts", "sample.reds");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        File.WriteAllText(original, "original script");
        var record = CyberpunkMods.Install(Assert.Single(plan.Items), game);
        foreach (var (key, bytes) in Contents())
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(game, key)));
        Assert.True(ModUninstaller.Uninstall(record).Success);
        Assert.Equal("original script", File.ReadAllText(original));
        Assert.False(File.Exists(Path.Combine(game, "bin", "x64", "unaligned.bin")));
    }

    [Theory]
    [InlineData(18, 19, false)]
    [InlineData(20, 19, false)]
    [InlineData(18, 19, true)]
    [InlineData(20, 19, true)]
    [InlineData(48, 19, true)]
    public void Invalid_entry_lengths_are_rejected(int actual, int declared, bool encryptedRar)
    {
        using var input = new MemoryStream(new byte[actual]);
        using var output = new MemoryStream();
        if (actual < declared)
            Assert.Throws<EndOfStreamException>(() => BundleArchive.CopyEntry(input, output, declared, encryptedRar, default));
        else
            Assert.Throws<InvalidDataException>(() => BundleArchive.CopyEntry(input, output, declared, encryptedRar, default));
    }

    [Fact]
    public void Cancelling_a_file_copy_stops_before_writing()
    {
        using var input = new MemoryStream(new byte[32]);
        using var output = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => BundleArchive.CopyEntry(input, output, 32, true, cts.Token));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Cancelling_extraction_keeps_the_destination_and_removes_temporary_files()
    {
        string destination = Path.Combine(_root, "existing.bin");
        File.WriteAllText(destination, "original bytes");
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => BundleArchive.Extract(Fixture, Password, _ =>
        {
            cts.Cancel();
            return destination;
        }, ct: cts.Token));
        Assert.Equal("original bytes", File.ReadAllText(destination));
        Assert.Equal(new[] { destination }, Directory.GetFiles(_root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
