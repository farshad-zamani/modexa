using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Modexa.Core.Engine;
using Modexa.Core.Format;
using Modexa.Core.Rpf;

namespace Modexa.Core.Install;

/// <summary>
/// Installs a FREE Modexa package (.mxa made with "Free mod" in the packer): no license and no
/// engine needed, in every edition. The package says exactly what goes where (its manifest), so it
/// is installed and recorded like any other mod and can be removed again with one click.
/// Licensed packages are refused here — they need their license.
/// </summary>
public static class FreePackageInstaller
{
    public static ModInstallRecord Install(string mxaPath, string gameFolder, string game, string workDir,
        IProgress<EngineProgress>? progress = null)
    {
        var info = MxaFile.ReadInfo(mxaPath);
        if (info.Licensed) throw new ModInstallException(ModInstallException.Unsupported, "This package needs its license.");

        var manifest = Unpack(mxaPath, workDir, progress);
        manifest.ModType = info.ModType; // the header type is authenticated (part of every chunk's AAD)
        if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = info.Name;

        var install = progress == null ? null
            : new InlineProgress<OivProgress>(p => progress.Report(new EngineProgress(EngineStage.Installing, p.Done, p.Total, p.Current)));
        ModInstallRecord record;
        switch (manifest.ModType)
        {
            case MxaModType.Loose:
                record = LooseFileInstaller.Install(manifest, workDir, gameFolder, game, install);
                break;
            case MxaModType.Addon:
                // The DLCs are registered in mods\update\update.rpf: make that editable copy first.
                ModsArchives.EnsureOpen(gameFolder, ModsArchives.UpdateRpf);
                record = LooseFileInstaller.Install(manifest, workDir, gameFolder, game, install);
                var dlcs = manifest.AllDlcs.ToList();
                try { if (dlcs.Count > 0) DlcList.RegisterMany(gameFolder, dlcs); }
                catch
                {
                    LooseFileInstaller.Uninstall(record);
                    throw;
                }
                break;
            case MxaModType.Oiv:
                record = new ModInstallRecord { Name = manifest.Name, ModType = MxaModType.Oiv, Game = game, GameFolder = gameFolder };
                OivInstaller.Apply(Path.Combine(workDir, manifest.OivPath ?? "package.oiv"), gameFolder, record,
                    new OivApplyOptions { Progress = install });
                break;
            default:
                throw new ModInstallException(ModInstallException.Unsupported, "Unknown package type.");
        }
        record.ProductId = null;
        return record;
    }

    /// <summary>Decrypts the payload with the free key and extracts it (chunk by chunk, any size).</summary>
    private static MxaManifest Unpack(string mxaPath, string destDir, IProgress<EngineProgress>? progress)
    {
        MxaPayloadFrame frame;
        try { frame = MxaFile.ReadFrame(mxaPath); }
        catch (Exception ex) { throw new ModInstallException(ModInstallException.BadPackage, "This package is damaged.", ex); }

        Directory.CreateDirectory(destDir);
        string payload = Path.Combine(destDir, ".payload-" + Guid.NewGuid().ToString("N") + ".zip");
        long total = frame.PayloadLength * 2;
        try
        {
            using (var input = new FileStream(mxaPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            using (var output = new FileStream(payload, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var gcm = new AesGcm(MxaFile.FreeKey, MxaFile.TagSize))
            {
                input.Position = frame.DataOffset;
                var cipher = new byte[frame.ChunkSize];
                var plain = new byte[frame.ChunkSize];
                var tag = new byte[MxaFile.TagSize];
                long chunks = frame.ChunkCount, left = frame.PayloadLength, done = 0;
                for (long i = 0; i < chunks; i++)
                {
                    int n = (int)Math.Min(frame.ChunkSize, left);
                    input.ReadExactly(cipher, 0, n);
                    input.ReadExactly(tag);
                    try
                    {
                        gcm.Decrypt(MxaFile.ChunkNonce(frame.NoncePrefix, i), cipher.AsSpan(0, n), tag, plain.AsSpan(0, n),
                            MxaFile.ChunkAad(frame.HeaderHash, i == chunks - 1));
                    }
                    catch (CryptographicException ex)
                    {
                        throw new ModInstallException(ModInstallException.BadPackage, "This package is damaged or was changed.", ex);
                    }
                    output.Write(plain, 0, n);
                    left -= n;
                    done += n;
                    progress?.Report(new EngineProgress(EngineStage.Unpacking, done, total));
                }
            }

            string root = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using var zip = ZipFile.OpenRead(payload);
            var me = zip.GetEntry("manifest.json") ?? throw new ModInstallException(ModInstallException.BadPackage, "Package manifest missing.");
            MxaManifest manifest;
            using (var sr = new StreamReader(me.Open(), Encoding.UTF8))
                manifest = JsonSerializer.Deserialize<MxaManifest>(sr.ReadToEnd()) ?? new MxaManifest();
            long extracted = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == "manifest.json" || string.IsNullOrEmpty(entry.Name)) continue;
                string dest = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never outside the work folder
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                extracted += entry.CompressedLength;
                progress?.Report(new EngineProgress(EngineStage.Unpacking, frame.PayloadLength + Math.Min(extracted, frame.PayloadLength), total, entry.Name));
            }
            return manifest;
        }
        finally
        {
            try { if (File.Exists(payload)) File.Delete(payload); } catch { }
        }
    }
}
