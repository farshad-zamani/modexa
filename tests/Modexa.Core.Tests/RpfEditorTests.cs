using System.IO;
using System.Text;
using Modexa.Core.Install;
using Modexa.Core.Rpf;
using Xunit;

namespace Modexa.Core.Tests;

public class RpfEditorTests
{
    private const string DlcListXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>\r\n\t<Paths>\r\n" +
        "\t\t<Item>dlcpacks:/mpheist/</Item>\r\n\t\t<Item>dlcpacks:/patchday1ng/</Item>\r\n\t</Paths>\r\n</SMandatoryPacksData>";

    private static string TempGame(out string updateRpf)
    {
        string game = Path.Combine(Path.GetTempPath(), "mxgame_" + Guid.NewGuid().ToString("N"));
        updateRpf = ModsArchives.ModsPath(game, ModsArchives.UpdateRpf);
        Directory.CreateDirectory(Path.GetDirectoryName(updateRpf)!);
        RpfBuilder.CreateOpen(updateRpf, new Dictionary<string, byte[]>
        {
            ["common/data/dlclist.xml"] = Encoding.UTF8.GetBytes(DlcListXml),
            ["common/data/gameconfig.xml"] = Encoding.UTF8.GetBytes("<CGameConfig/>"),
            ["x64/data/a.meta"] = Encoding.UTF8.GetBytes("<a/>"),
        });
        return game;
    }

    [Fact]
    public void Editor_adds_replaces_and_deletes_and_keeps_other_entries()
    {
        string game = TempGame(out string rpf);
        try
        {
            var big = new byte[200_000];
            new Random(3).NextBytes(big);
            var ed = RpfEditor.Open(rpf);
            ed.SetText("common/data/gameconfig.xml", "<CGameConfig><x/></CGameConfig>");
            ed.SetFile("new/folder/blob.bin", big);
            for (int i = 0; i < 300; i++) ed.SetText($"many/f_{i:D3}_with_a_long_name.meta", "<m/>"); // grows the TOC
            ed.Commit();

            ed = RpfEditor.Open(rpf);
            Assert.Equal("<CGameConfig><x/></CGameConfig>", ed.ReadText("common/data/gameconfig.xml"));
            Assert.Equal(big, ed.ReadFile("new/folder/blob.bin"));
            Assert.Equal("<a/>", ed.ReadText("x64/data/a.meta"));
            Assert.Equal(DlcListXml, ed.ReadText("common/data/dlclist.xml"));
            Assert.True(ed.FileExists("many/f_299_with_a_long_name.meta"));

            Assert.True(ed.DeleteFile("x64/data/a.meta"));
            ed.Commit();
            ed = RpfEditor.Open(rpf);
            Assert.False(ed.FileExists("x64/data/a.meta"));
            Assert.Equal(big, ed.ReadFile("new/folder/blob.bin"));

            // The public reader agrees with the editor.
            var arch = RpfArchive.Open(rpf);
            Assert.Equal(DlcListXml, arch.ExtractText(arch.FindBinary("common/data/dlclist.xml")!));
        }
        finally { try { Directory.Delete(game, true); } catch { } }
    }

    [Fact]
    public void Resources_round_trip_with_their_header()
    {
        string game = TempGame(out string rpf);
        try
        {
            var rsc = new byte[9000];
            new Random(5).NextBytes(rsc);
            BitConverter.GetBytes(0x37435352u).CopyTo(rsc, 0);           // RSC7
            BitConverter.GetBytes(13u).CopyTo(rsc, 4);                    // version 13 = (0 << 4) | 13
            BitConverter.GetBytes(0x00000011u).CopyTo(rsc, 8);            // system flags
            BitConverter.GetBytes(0xD0000022u).CopyTo(rsc, 12);           // graphics flags (version in top bits)

            var ed = RpfEditor.Open(rpf);
            ed.SetFile("x64/textures/test.ytd", rsc);
            ed.Commit();

            ed = RpfEditor.Open(rpf);
            Assert.False(ed.IsBinary("x64/textures/test.ytd"));
            Assert.Equal(rsc, ed.ReadFile("x64/textures/test.ytd"));
        }
        finally { try { Directory.Delete(game, true); } catch { } }
    }

    [Fact]
    public void Nested_archive_is_edited_through_the_chain()
    {
        string game = TempGame(out string rpf);
        string inner = Path.Combine(Path.GetTempPath(), "inner_" + Guid.NewGuid().ToString("N") + ".rpf");
        try
        {
            RpfBuilder.CreateOpen(inner, new Dictionary<string, byte[]> { ["vehicles.meta"] = Encoding.UTF8.GetBytes("<v/>") });
            var ed = RpfEditor.Open(rpf);
            ed.SetNestedArchive("x64/vehicles.rpf", inner);
            ed.Commit();

            ArchiveChain.Edit(game, "mods/update/update.rpf", new[] { "x64/vehicles.rpf" }, e => e.SetText("added.txt", "deep"));

            string check = inner + ".check";
            RpfEditor.Open(rpf).ExtractNestedArchive("x64/vehicles.rpf", check);
            var nested = RpfEditor.Open(check);
            Assert.Equal("deep", nested.ReadText("added.txt"));
            Assert.Equal("<v/>", nested.ReadText("vehicles.meta"));
            File.Delete(check);
        }
        finally
        {
            try { File.Delete(inner); } catch { }
            try { Directory.Delete(game, true); } catch { }
        }
    }

    [Fact]
    public void DlcList_register_unregister_is_idempotent_and_keeps_indentation()
    {
        string game = TempGame(out _);
        try
        {
            Assert.True(DlcList.Register(game, "car206"));
            Assert.False(DlcList.Register(game, "car206"));
            Assert.True(DlcList.IsRegistered(game, "car206"));
            string xml = RpfEditor.Open(ModsArchives.ModsPath(game, ModsArchives.UpdateRpf)).ReadText(DlcList.EntryPath);
            Assert.Contains("\t\t<Item>dlcpacks:/car206/</Item>", xml);

            Assert.True(DlcList.Unregister(game, "car206"));
            Assert.False(DlcList.IsRegistered(game, "car206"));
            Assert.True(DlcList.IsRegistered(game, "mpheist"));
        }
        finally { try { Directory.Delete(game, true); } catch { } }
    }

    [Fact]
    public void Journal_reverts_own_edits_but_keeps_other_mods_entries()
    {
        string game = TempGame(out string rpf);
        try
        {
            var record = new ModInstallRecord { Name = "a", GameFolder = game };
            var rec = new ArchiveEditRecorder(record);
            ArchiveChain.Edit(game, "update/update.rpf", Array.Empty<string>(), ed =>
            {
                var edit = rec.Touch("update/update.rpf", Array.Empty<string>(), ed, DlcList.EntryPath);
                ed.SetText(DlcList.EntryPath, DlcList.AddEntry(ed.ReadText(DlcList.EntryPath), "moda"));
                edit.Journal = new EditJournal { XmlAdded = new() { new XmlAddition { ParentXPath = "/SMandatoryPacksData/Paths", Xml = "<Item>dlcpacks:/moda/</Item>" } } };
                rec.Touch("update/update.rpf", Array.Empty<string>(), ed, "x64/data/a.meta");
                ed.SetText("x64/data/a.meta", "<changed/>");
                rec.Touch("update/update.rpf", Array.Empty<string>(), ed, "new.txt");
                ed.SetText("new.txt", "mine");
                rec.Seal("update/update.rpf", Array.Empty<string>(), ed);
            });

            DlcList.Register(game, "modb"); // another mod edits the shared file afterwards

            ArchiveEditRecorder.Revert(record);
            var after = RpfEditor.Open(rpf);
            Assert.False(DlcList.IsRegistered(game, "moda"));
            Assert.True(DlcList.IsRegistered(game, "modb"));
            Assert.Equal("<a/>", after.ReadText("x64/data/a.meta")); // untouched since -> exact restore
            Assert.False(after.FileExists("new.txt"));
        }
        finally { try { Directory.Delete(game, true); } catch { } }
    }

    [Fact]
    public void Content_only_commit_keeps_entry_order()
    {
        string game = TempGame(out string rpf);
        try
        {
            var before = RpfArchive.Open(rpf).Entries.Select(e => e.Path).ToList();
            var ed = RpfEditor.Open(rpf);
            ed.SetText("common/data/dlclist.xml", DlcList.AddEntry(DlcListXml, "x"));
            ed.Commit();
            var after = RpfArchive.Open(rpf).Entries.Select(e => e.Path).ToList();
            Assert.Equal(before, after);
        }
        finally { try { Directory.Delete(game, true); } catch { } }
    }

    [Fact]
    public void Ensure_open_reports_a_missing_game_archive()
    {
        string game = Path.Combine(Path.GetTempPath(), "mxempty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(game);
        try
        {
            var ex = Assert.Throws<ModsArchiveException>(() => ModsArchives.EnsureOpen(game, "update/update.rpf"));
            Assert.Equal(ModsArchiveException.SourceMissing, ex.Code);
            Assert.Throws<ModsArchiveException>(() => ModsArchives.EnsureOpen(game, "../outside.rpf"));
        }
        finally { try { Directory.Delete(game, true); } catch { } }
    }
}
