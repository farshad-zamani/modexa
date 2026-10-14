using System.IO;
using System.IO.Compression;
using System.Text;
using Modexa.Core.Install;
using Modexa.Core.Rpf;
using Xunit;

namespace Modexa.Core.Tests;

/// <summary>
/// Archive copies in mods\: made from the game for an install, shared between mods, deleted again
/// when the last mod that needs them is removed — never when something else changed them. Also the
/// multi-part package case (same name, different packages).
/// </summary>
public class ArchiveOwnershipTests
{
    private sealed class Sandbox : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "own_" + Guid.NewGuid().ToString("N"));
        public string Game => Path.Combine(Root, "game");

        public Sandbox()
        {
            // Two "game archives" (OPEN here, so no game keys are needed in the test).
            foreach (var name in new[] { "RADIO_A.rpf", "RADIO_B.rpf" })
            {
                string p = Path.Combine(Game, "x64", "audio", "sfx", name);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                RpfBuilder.CreateOpen(p, new Dictionary<string, byte[]>
                {
                    ["voice_1.awc"] = Encoding.ASCII.GetBytes("vanilla-1-" + name),
                    ["voice_2.awc"] = Encoding.ASCII.GetBytes("vanilla-2-" + name),
                });
            }
        }

        public string ModsCopy(string name) => Path.Combine(Game, "mods", "x64", "audio", "sfx", name);

        /// <summary>A real .oiv (zip) replacing entries in one or more sound archives.</summary>
        public string Oiv(string file, string id, string name, Dictionary<string, string[]> archives, byte[]? icon = null)
        {
            string path = Path.Combine(Root, file);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var content = new StringBuilder();
            int n = 0;
            foreach (var (archive, entries) in archives)
            {
                content.Append($"<archive path=\"x64\\audio\\sfx\\{archive}\" createIfNotExist=\"True\" type=\"RPF7\">");
                foreach (var entry in entries)
                {
                    string src = $"f{n++}/{entry}";
                    content.Append($"<add source=\"{src.Replace('/', '\\')}\">\\{entry}</add>");
                    var e = zip.CreateEntry("content/" + src);
                    using var w = new StreamWriter(e.Open());
                    w.Write($"dub-{file}-{entry}");
                }
                content.Append("</archive>");
            }
            var asm = zip.CreateEntry("assembly.xml");
            using (var w = new StreamWriter(asm.Open(), new UTF8Encoding(false)))
                w.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><package version=\"2.0\" id=\"{id}\" target=\"Five\">" +
                        $"<metadata><name>{name}</name><version><major>1</major><minor>0</minor><tag>Stable</tag></version>" +
                        $"<author><displayName>Gamedub</displayName></author></metadata><content>{content}</content></package>");
            if (icon != null)
            {
                var ie = zip.CreateEntry("icon.png");
                using var s = ie.Open();
                s.Write(icon);
            }
            return path;
        }

        public ModInstallRecord Install(string oiv, List<OivProgress>? progress = null)
        {
            var rec = new ModInstallRecord { GameFolder = Game, Source = Path.GetFileName(oiv) };
            OivInstaller.Apply(oiv, Game, rec, new OivApplyOptions
            {
                Progress = progress == null ? null : new InlineProgress<OivProgress>(progress.Add)
            });
            InstalledModsStore.Add(rec);
            Thread.Sleep(15); // distinct install times
            return rec;
        }

        public string Read(string archive, string entry) => RpfEditor.Open(ModsCopy(archive)).ReadText(entry);

        public void Dispose()
        {
            foreach (var r in InstalledModsStore.ForFolder(Game)) InstalledModsStore.Remove(r.Id);
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    [Fact]
    public void Two_parts_with_the_same_name_install_side_by_side_and_uninstall_separately()
    {
        using var s = new Sandbox();
        string p1 = s.Oiv("Package.1.oiv", "{D9A64643-597D-4BE3-97B5-476FEB327E98}", "Farsidub Fullpack", new() { ["RADIO_A.rpf"] = new[] { "voice_1.awc" } });
        string p2 = s.Oiv("Package.2.oiv", "{A7F4495B-8420-4B76-A0C1-0D6A3B511DA8}", "Farsidub Fullpack", new() { ["RADIO_B.rpf"] = new[] { "voice_2.awc" } });

        var r1 = s.Install(p1);
        // Part 2 is a different file, so a different mod (never merged with part 1)…
        Assert.False(OivInstaller.IsSamePackage(r1, p2));
        // …even an identical package under another file name is its own mod…
        string copy = Path.Combine(s.Root, "Package.1 - copy.oiv");
        File.Copy(p1, copy);
        Assert.False(OivInstaller.IsSamePackage(r1, copy));
        // …while the very same file again is a reinstall.
        Assert.True(OivInstaller.IsSamePackage(r1, p1));

        var r2 = s.Install(p2);
        Assert.Equal("dub-Package.1.oiv-voice_1.awc", s.Read("RADIO_A.rpf", "voice_1.awc"));
        Assert.Equal("dub-Package.2.oiv-voice_2.awc", s.Read("RADIO_B.rpf", "voice_2.awc"));
        // Copies made from the game: no backup copies of the game's files were kept.
        Assert.All(r1.ArchiveEdits, e => { Assert.True(e.OriginalInGame); Assert.Null(e.OriginalBackup); });
        Assert.Equal(new[] { "x64/audio/sfx/RADIO_A.rpf" }, r1.CreatedArchives);

        // Removing part 1 deletes only its archive copy; part 2 keeps working.
        var u1 = ModUninstaller.Uninstall(r1);
        Assert.True(u1.Success, string.Join("; ", u1.Errors));
        Assert.True(u1.FreedBytes > 0);
        Assert.False(File.Exists(s.ModsCopy("RADIO_A.rpf")));
        Assert.Equal("dub-Package.2.oiv-voice_2.awc", s.Read("RADIO_B.rpf", "voice_2.awc"));

        Assert.True(ModUninstaller.Uninstall(r2).Success);
        Assert.False(Directory.Exists(Path.Combine(s.Game, "mods", "x64")));
        Assert.Empty(ModsArchives.CreatedCopies(s.Game));
    }

    [Fact]
    public void Shared_archive_copy_stays_until_the_last_mod_and_gets_the_game_originals_back()
    {
        using var s = new Sandbox();
        string a = s.Oiv("a.oiv", "{AAAAAAAA-0000-0000-0000-000000000001}", "Mod A", new() { ["RADIO_A.rpf"] = new[] { "voice_1.awc" } });
        string b = s.Oiv("b.oiv", "{BBBBBBBB-0000-0000-0000-000000000002}", "Mod B", new() { ["RADIO_A.rpf"] = new[] { "voice_2.awc" } });
        var ra = s.Install(a);
        var rb = s.Install(b);

        // A goes first: B still needs the copy, so only A's file is put back — from the game itself.
        Assert.True(ModUninstaller.Uninstall(ra).Success);
        Assert.True(File.Exists(s.ModsCopy("RADIO_A.rpf")));
        Assert.Equal("vanilla-1-RADIO_A.rpf", s.Read("RADIO_A.rpf", "voice_1.awc"));
        Assert.Equal("dub-b.oiv-voice_2.awc", s.Read("RADIO_A.rpf", "voice_2.awc"));

        // B was the last user: the copy goes.
        Assert.True(ModUninstaller.Uninstall(rb).Success);
        Assert.False(File.Exists(s.ModsCopy("RADIO_A.rpf")));
    }

    [Fact]
    public void A_copy_changed_by_another_tool_is_never_deleted_silently()
    {
        using var s = new Sandbox();
        string a = s.Oiv("a.oiv", "{AAAAAAAA-0000-0000-0000-000000000003}", "Mod A", new() { ["RADIO_B.rpf"] = new[] { "voice_1.awc" } });
        var ra = s.Install(a);

        // "OpenIV" adds a file to the copy afterwards.
        var ed = RpfEditor.Open(s.ModsCopy("RADIO_B.rpf"));
        ed.SetText("user_added.awc", "from openiv");
        ed.Commit();

        Assert.True(ModUninstaller.Uninstall(ra).Success);
        Assert.True(File.Exists(s.ModsCopy("RADIO_B.rpf")));                          // kept
        Assert.Equal("from openiv", s.Read("RADIO_B.rpf", "user_added.awc"));        // the other change survives
        Assert.Equal("vanilla-1-RADIO_B.rpf", s.Read("RADIO_B.rpf", "voice_1.awc")); // ours is undone
        // It is offered for clean-up instead (the user decides).
        Assert.Contains(ModsArchives.Unused(s.Game), u => u.Archive == "x64/audio/sfx/RADIO_B.rpf");
    }

    [Fact]
    public void Package_is_read_from_the_zip_with_icon_and_steady_progress()
    {
        using var s = new Sandbox();
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        string a = s.Oiv("a.oiv", "{AAAAAAAA-0000-0000-0000-000000000004}", "Mod A",
            new() { ["RADIO_A.rpf"] = new[] { "voice_1.awc", "voice_2.awc" }, ["RADIO_B.rpf"] = new[] { "voice_1.awc" } }, png);

        var info = OivInstaller.Inspect(a);
        Assert.Equal("{AAAAAAAA-0000-0000-0000-000000000004}", info.Id);
        Assert.Equal(png, info.Icon);
        Assert.Equal(2, info.Archives.Count);
        Assert.True(OivInstaller.SpaceNeeded(info, s.Game) > info.TotalBytes); // + the two archive copies

        var progress = new List<OivProgress>();
        var rec = s.Install(a, progress);
        Assert.NotNull(rec.IconFile);
        Assert.True(File.Exists(rec.IconFile));
        Assert.Contains(progress, p => p.Copying);
        Assert.All(progress.Zip(progress.Skip(1)), p => Assert.True(p.Second.Done >= p.First.Done));
        Assert.Equal(progress[^1].Total, progress[^1].Done);
        Assert.True(ModUninstaller.Uninstall(rec).Success);
    }
}
