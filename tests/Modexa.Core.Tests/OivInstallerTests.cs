using System.IO;
using System.Text;
using Modexa.Core.Install;
using Modexa.Core.Rpf;
using Xunit;

namespace Modexa.Core.Tests;

public class OivInstallerTests
{
    private const string DlcListXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SMandatoryPacksData>\r\n  <Paths>\r\n" +
        "    <Item>dlcpacks:/mpheist/</Item>\r\n  </Paths>\r\n</SMandatoryPacksData>";

    private const string HandlingXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<CHandlingDataMgr>\n  <HandlingData>\n" +
        "    <Item><handlingName>ADDER</handlingName><fMass value=\"1800\"/></Item>\n" +
        "    <Item><handlingName>ZENTORNO</handlingName><fMass value=\"1500\"/></Item>\n" +
        "  </HandlingData>\n</CHandlingDataMgr>";

    private sealed class Sandbox : IDisposable
    {
        public readonly string Game = Path.Combine(Path.GetTempPath(), "oivgame_" + Guid.NewGuid().ToString("N"));
        public readonly string UpdateRpf;
        private readonly List<string> _pkgs = new();

        public Sandbox()
        {
            UpdateRpf = ModsArchives.ModsPath(Game, ModsArchives.UpdateRpf);
            Directory.CreateDirectory(Path.GetDirectoryName(UpdateRpf)!);
            RpfBuilder.CreateOpen(UpdateRpf, new Dictionary<string, byte[]>
            {
                ["common/data/dlclist.xml"] = Encoding.UTF8.GetBytes(DlcListXml),
                ["common/data/handling.meta"] = Encoding.UTF8.GetBytes(HandlingXml),
                ["common/data/notes.txt"] = Encoding.UTF8.GetBytes("line1\nline2\nline3\n"),
            });
            File.WriteAllText(Path.Combine(Game, "settings.ini"), "original");
            File.WriteAllText(Path.Combine(Game, "trainer.ini"), "a=1\nb=2\n");
        }

        /// <summary>Writes a package folder: assembly.xml + content files.</summary>
        public string Package(string contentXml, Dictionary<string, string> files, string name = "Test Pack")
        {
            string dir = Path.Combine(Path.GetTempPath(), "oivpkg_" + Guid.NewGuid().ToString("N"));
            _pkgs.Add(dir);
            Directory.CreateDirectory(Path.Combine(dir, "content"));
            foreach (var kv in files)
            {
                string p = Path.Combine(dir, "content", kv.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, kv.Value);
            }
            File.WriteAllText(Path.Combine(dir, "assembly.xml"),
                "<?xml version=\"1.0\"?>\n<package version=\"2.2\" target=\"Five\"><metadata><name>" + name + "</name>" +
                "<version><major>1</major><minor>2</minor><tag>Version</tag></version><author><displayName>Tester</displayName></author>" +
                "</metadata><content>\n" + contentXml + "\n</content></package>");
            return dir;
        }

        public ModInstallRecord Install(string pkg)
        {
            var rec = new ModInstallRecord { GameFolder = Game, Source = "x.oiv" };
            OivInstaller.Apply(pkg, Game, rec);
            InstalledModsStore.Add(rec);
            return rec;
        }

        public string Read(string entry) => RpfEditor.Open(UpdateRpf).ReadText(entry);

        public void Dispose()
        {
            foreach (var r in InstalledModsStore.ForFolder(Game)) InstalledModsStore.Remove(r.Id);
            try { Directory.Delete(Game, true); } catch { }
            foreach (var p in _pkgs) try { Directory.Delete(p, true); } catch { }
        }
    }

    [Fact]
    public void Inspect_reads_metadata_and_touched_archives()
    {
        using var s = new Sandbox();
        string pkg = s.Package("<archive path=\"mods\\update\\update.rpf\" type=\"RPF7\"><xml path=\"common\\data\\dlclist.xml\"/></archive>", new());
        var info = OivInstaller.Inspect(pkg);
        Assert.Equal("Test Pack", info.Name);
        Assert.Equal("1.2", info.Version);
        Assert.Equal("Tester", info.Author);
        Assert.Equal(new[] { "update/update.rpf" }, info.Archives);
    }

    [Fact]
    public void Full_package_installs_and_uninstalls_back_to_the_exact_original()
    {
        using var s = new Sandbox();
        byte[] rpfBefore = File.ReadAllBytes(s.UpdateRpf);
        string pkg = s.Package(
            "<add source=\"206\\dlc.rpf\">update\\x64\\dlcpacks\\206\\dlc.rpf</add>\n" +
            "<add source=\"settings.ini\">settings.ini</add>\n" +
            "<delete>trainer.ini</delete>\n" +
            "<archive path=\"update\\update.rpf\" createIfNotExist=\"True\" type=\"RPF7\">\n" +
            "  <xml path=\"common\\data\\dlclist.xml\"><add append=\"Last\" xpath=\"/SMandatoryPacksData/Paths\"><Item>dlcpacks:/206/</Item></add></xml>\n" +
            "  <xml path=\"common\\data\\handling.meta\">\n" +
            "    <replace xpath=\"/CHandlingDataMgr/HandlingData/Item[handlingName='ADDER']/fMass\"><fMass value=\"2500\"/></replace>\n" +
            "    <remove xpath=\"/CHandlingDataMgr/HandlingData/Item[handlingName='ZENTORNO']\"/>\n" +
            "  </xml>\n" +
            "  <text path=\"common\\data\\notes.txt\"><insert where=\"After\" line=\"line1\" condition=\"Equal\">inserted</insert><replace line=\"line3\">LINE3</replace><delete>line2</delete></text>\n" +
            "  <add source=\"car.txt\">x64\\car.txt</add>\n" +
            "</archive>",
            new() { ["206\\dlc.rpf"] = "rpf", ["settings.ini"] = "modded", ["car.txt"] = "model" });

        var rec = s.Install(pkg);
        Assert.Equal("Test Pack", rec.Name);
        Assert.Equal("1.2", rec.Version);
        Assert.True(File.Exists(Path.Combine(s.Game, "mods", "update", "x64", "dlcpacks", "206", "dlc.rpf")));
        Assert.False(Directory.Exists(Path.Combine(s.Game, "update")));            // never into the game files
        Assert.Equal("modded", File.ReadAllText(Path.Combine(s.Game, "settings.ini")));
        Assert.False(File.Exists(Path.Combine(s.Game, "trainer.ini")));
        Assert.Contains("dlcpacks:/206/", s.Read("common/data/dlclist.xml"));
        Assert.Contains("2500", s.Read("common/data/handling.meta"));
        Assert.DoesNotContain("ZENTORNO", s.Read("common/data/handling.meta"));
        Assert.Equal("line1\ninserted\nLINE3\n", s.Read("common/data/notes.txt"));

        var result = ModUninstaller.Uninstall(rec);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        Assert.False(Directory.Exists(Path.Combine(s.Game, "mods", "update", "x64")));  // created folders pruned
        Assert.Equal("original", File.ReadAllText(Path.Combine(s.Game, "settings.ini")));
        Assert.Equal("a=1\nb=2\n", File.ReadAllText(Path.Combine(s.Game, "trainer.ini")));
        Assert.Equal(DlcListXml, s.Read("common/data/dlclist.xml"));
        Assert.Equal(HandlingXml, s.Read("common/data/handling.meta"));
        Assert.Equal("line1\nline2\nline3\n", s.Read("common/data/notes.txt"));
        Assert.False(RpfEditor.Open(s.UpdateRpf).FileExists("x64/car.txt"));
        Assert.Empty(InstalledModsStore.ForFolder(s.Game));
    }

    [Fact]
    public void Removing_mods_in_any_order_keeps_the_others_and_ends_at_the_original()
    {
        using var s = new Sandbox();
        string a = s.Package(
            "<add source=\"settings.ini\">settings.ini</add>" +
            "<archive path=\"update\\update.rpf\" type=\"RPF7\"><xml path=\"common\\data\\dlclist.xml\"><add append=\"Last\" xpath=\"/SMandatoryPacksData/Paths\"><Item>dlcpacks:/moda/</Item></add></xml>" +
            "<xml path=\"common\\data\\handling.meta\"><replace xpath=\"/CHandlingDataMgr/HandlingData/Item[handlingName='ADDER']/fMass\"><fMass value=\"111\"/></replace></xml></archive>",
            new() { ["settings.ini"] = "from A" }, "Mod A");
        string b = s.Package(
            "<add source=\"settings.ini\">settings.ini</add>" +
            "<archive path=\"update\\update.rpf\" type=\"RPF7\"><xml path=\"common\\data\\dlclist.xml\"><add append=\"Last\" xpath=\"/SMandatoryPacksData/Paths\"><Item>dlcpacks:/modb/</Item></add></xml></archive>",
            new() { ["settings.ini"] = "from B" }, "Mod B");

        var ra = s.Install(a);
        Thread.Sleep(20);
        var rb = s.Install(b);
        Assert.Equal("from B", File.ReadAllText(Path.Combine(s.Game, "settings.ini")));
        var fa = InstalledModsStore.All().First(r => r.Id == ra.Id).Files.Single();
        var fb = InstalledModsStore.All().First(r => r.Id == rb.Id).Files.Single();
        Assert.NotEqual(fa.WrittenStamp, fb.WrittenStamp);
        Assert.Equal(fb.WrittenStamp, FileStamp.Of(Path.Combine(s.Game, "settings.ini")));

        // Remove the OLDER mod first: B keeps its file and its dlclist entry.
        Assert.True(ModUninstaller.Uninstall(ra).Success);
        Assert.Equal("from B", File.ReadAllText(Path.Combine(s.Game, "settings.ini")));
        string dl = s.Read("common/data/dlclist.xml");
        Assert.DoesNotContain("moda", dl);
        Assert.Contains("modb", dl);
        Assert.DoesNotContain("111", s.Read("common/data/handling.meta"));

        // Then B: everything is back to the true original (A's entry must not come back).
        Assert.True(ModUninstaller.Uninstall(rb).Success);
        Assert.Equal("original", File.ReadAllText(Path.Combine(s.Game, "settings.ini")));
        Assert.Equal(DlcListXml, s.Read("common/data/dlclist.xml"));
        Assert.Equal(HandlingXml, s.Read("common/data/handling.meta"));
    }

    [Fact]
    public void Failed_package_rolls_back_everything()
    {
        using var s = new Sandbox();
        string pkg = s.Package(
            "<add source=\"settings.ini\">settings.ini</add>" +
            "<archive path=\"update\\update.rpf\" type=\"RPF7\"><xml path=\"common\\data\\dlclist.xml\"><add append=\"Last\" xpath=\"/SMandatoryPacksData/Paths\"><Item>dlcpacks:/x/</Item></add></xml></archive>" +
            "<add source=\"missing.dat\">x.dat</add>",
            new() { ["settings.ini"] = "modded" });
        var rec = new ModInstallRecord { GameFolder = s.Game };
        Assert.ThrowsAny<Exception>(() => OivInstaller.Apply(pkg, s.Game, rec));
        Assert.Equal("original", File.ReadAllText(Path.Combine(s.Game, "settings.ini")));
        Assert.Equal(DlcListXml, s.Read("common/data/dlclist.xml"));
    }

    [Fact]
    public void Loose_text_edit_is_reversed_even_after_the_user_changed_the_file()
    {
        using var s = new Sandbox();
        string pkg = s.Package("<text path=\"trainer.ini\"><add>c=3</add></text>", new());
        var rec = s.Install(pkg);
        string ini = Path.Combine(s.Game, "trainer.ini");
        Assert.Contains("c=3", File.ReadAllText(ini));

        File.AppendAllText(ini, "user=1\n"); // edited afterwards by someone else
        Assert.True(ModUninstaller.Uninstall(rec).Success);
        string after = File.ReadAllText(ini);
        Assert.DoesNotContain("c=3", after);
        Assert.Contains("user=1", after);
    }

    [Fact]
    public void Package_for_another_game_is_refused()
    {
        using var s = new Sandbox();
        string pkg = s.Package("", new());
        File.WriteAllText(Path.Combine(pkg, "assembly.xml"), "<package version=\"2.2\" target=\"IV\"><content/></package>");
        var ex = Assert.Throws<Modexa.Core.Engine.ModInstallException>(() => OivInstaller.Inspect(pkg));
        Assert.Equal(Modexa.Core.Engine.ModInstallException.Unsupported, ex.Code);
    }
}
