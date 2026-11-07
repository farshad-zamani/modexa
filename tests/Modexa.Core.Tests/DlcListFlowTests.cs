using System.IO;
using System.Text;
using Modexa.Core.Install;
using Modexa.Core.Rpf;
using Xunit;

namespace Modexa.Core.Tests;

/// <summary>
/// Installing and removing add-ons / OIV packages that edit dlclist.xml must leave a user's own file exactly as
/// it was — not a line moved, no spacing changed (the fixtures are a real, irregular dlclist.xml).
/// </summary>
public class DlcListFlowTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed class Sandbox : IDisposable
    {
        public readonly string Game = Path.Combine(Path.GetTempPath(), "dlcflow_" + Guid.NewGuid().ToString("N"));
        public readonly string UpdateRpf;
        private readonly string _pkgs = Path.Combine(Path.GetTempPath(), "dlcpkg_" + Guid.NewGuid().ToString("N"));

        public Sandbox(string dlclist)
        {
            UpdateRpf = ModsArchives.ModsPath(Game, ModsArchives.UpdateRpf);
            Directory.CreateDirectory(Path.GetDirectoryName(UpdateRpf)!);
            RpfBuilder.CreateOpen(UpdateRpf, new Dictionary<string, byte[]> { ["common/data/dlclist.xml"] = Encoding.UTF8.GetBytes(dlclist) });
        }

        public string DlcList() => RpfEditor.Open(UpdateRpf).ReadText("common/data/dlclist.xml");

        public string Package(string archiveOps)
        {
            string dir = Path.Combine(_pkgs, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "content"));
            File.WriteAllText(Path.Combine(dir, "content", "dlc.rpf"), "dlc");
            File.WriteAllText(Path.Combine(dir, "assembly.xml"),
                "<?xml version=\"1.0\"?>\n<package version=\"2.2\" target=\"Five\"><metadata><name>Bugatti</name>" +
                "<version><major>1</major><minor>0</minor></version><author><displayName>T</displayName></author></metadata>" +
                "<content>\n<archive path=\"update\\update.rpf\" type=\"RPF7\">" + archiveOps + "</archive>\n</content></package>");
            return dir;
        }

        public ModInstallRecord Install(string pkg, string source = "bugatti.oiv")
        {
            var rec = new ModInstallRecord { GameFolder = Game, Source = source, ModType = Modexa.Core.Format.MxaModType.Oiv };
            OivInstaller.Apply(pkg, Game, rec);
            InstalledModsStore.Add(rec);
            return rec;
        }

        public void Dispose()
        {
            foreach (var r in InstalledModsStore.ForFolder(Game)) InstalledModsStore.Remove(r.Id);
            try { Directory.Delete(Game, true); } catch { }
            try { Directory.Delete(_pkgs, true); } catch { }
        }
    }

    private const string AddInMiddle =
        "<add source=\"dlc.rpf\">x64\\dlcpacks\\bugatti\\dlc.rpf</add>" +
        "<xml path=\"common\\data\\dlclist.xml\"><add append=\"After\" xpath=\"/SMandatoryPacksData/Paths/Item[3]\"><Item>dlcpacks:/bugatti/</Item></add></xml>";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_oiv_add_on_comes_out_of_dlclist_leaving_the_file_as_it_was(bool lf)
    {
        string original = Fixture("dlclist.without-bugatti.xml");
        if (lf) original = original.Replace("\r\n", "\n");
        using var s = new Sandbox(original);
        var rec = s.Install(s.Package(AddInMiddle));
        Assert.Contains("dlcpacks:/bugatti/", s.DlcList());
        Assert.Equal(lf, !s.DlcList().Contains("\r\n")); // line endings kept while installing too

        Assert.True(ModUninstaller.Uninstall(rec).Success);
        Assert.Equal(original, s.DlcList());
    }

    [Fact]
    public void An_oiv_add_on_is_removed_from_dlclist_even_when_another_mod_edited_the_file_after_it()
    {
        string original = Fixture("dlclist.without-bugatti.xml");
        using var s = new Sandbox(original);
        var rec = s.Install(s.Package(AddInMiddle));
        // Another add-on registers itself afterwards (the file now differs from what the first one wrote).
        DlcList.Register(s.Game, "othercar");

        Assert.True(ModUninstaller.Uninstall(rec).Success);
        string left = s.DlcList();
        Assert.DoesNotContain("bugatti", left);
        Assert.Contains("dlcpacks:/othercar/", left);
        Assert.Equal(original, DlcList.RemoveEntry(left, "othercar"));
    }

    [Fact]
    public void A_leftover_dlclist_line_of_a_removed_oiv_add_on_is_cleaned_up()
    {
        string original = Fixture("dlclist.without-bugatti.xml");
        using var s = new Sandbox(original);
        var rec = s.Install(s.Package(AddInMiddle));
        // Whatever made the exact edit undo impossible (the journal lost): the line must still go.
        foreach (var e in rec.ArchiveEdits) e.Journal = null;
        InstalledModsStore.Update(new[] { rec });
        DlcList.Register(s.Game, "othercar");   // changed since → exact restore is not possible either

        Assert.True(ModUninstaller.Uninstall(rec).Success);
        Assert.DoesNotContain("bugatti", s.DlcList());
        Assert.Contains("dlcpacks:/othercar/", s.DlcList());
    }
}
