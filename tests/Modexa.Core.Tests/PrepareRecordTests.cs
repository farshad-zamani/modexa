using System.IO;
using System.Text;
using Modexa.Core.Install;
using Modexa.Core.Prepare;
using Modexa.Core.Rpf;
using Xunit;

namespace Modexa.Core.Tests;

/// <summary>A prepare step is an installed item like any mod: listed, and removable on its own.</summary>
public class PrepareRecordTests
{
    [Fact]
    public void GameConfig_step_is_recorded_listed_and_fully_removable()
    {
        string game = Path.Combine(Path.GetTempPath(), "prep_" + Guid.NewGuid().ToString("N"));
        try
        {
            // The game's own update.rpf (OPEN in the test, so no game keys are needed).
            string gameRpf = Path.Combine(game, "update", "update.rpf");
            Directory.CreateDirectory(Path.GetDirectoryName(gameRpf)!);
            RpfBuilder.CreateOpen(gameRpf, new Dictionary<string, byte[]>
            {
                ["common/data/gameconfig.xml"] = Encoding.UTF8.GetBytes("<CGameConfig>vanilla</CGameConfig>"),
            });

            // What the GameConfig step does: make the editable copy, then write gameconfig.xml into it.
            ModsArchives.EnsureOpen(game, ModsArchives.UpdateRpf);
            var record = new ModInstallRecord { Name = "GameConfig", GameFolder = game, Setup = PrepareStep.GameConfig, CreatedArchives = { ModsArchives.UpdateRpf } };
            var outcome = PrepareService.ApplyGameConfig(game, Encoding.UTF8.GetBytes("<CGameConfig>traffic</CGameConfig>"), record);
            Assert.Equal(GameConfigOutcome.Applied, outcome);
            InstalledModsStore.Add(record);

            string copy = ModsArchives.ModsPath(game, ModsArchives.UpdateRpf);
            Assert.Equal("<CGameConfig>traffic</CGameConfig>", RpfEditor.Open(copy).ReadText("common/data/gameconfig.xml"));
            Assert.True(PrepareService.InstalledSteps(game).ContainsKey(PrepareStep.GameConfig));
            Assert.True(record.ArchiveEdits.Single().OriginalInGame); // the game's own file is the backup

            // Removing the step removes the copy it made: the game reads its own gameconfig again.
            Assert.True(ModUninstaller.Uninstall(record).Success);
            Assert.False(File.Exists(copy));
            Assert.Empty(PrepareService.InstalledSteps(game));
            Assert.Equal("<CGameConfig>vanilla</CGameConfig>", RpfEditor.Open(gameRpf).ReadText("common/data/gameconfig.xml"));
        }
        finally
        {
            foreach (var r in InstalledModsStore.ForFolder(game)) InstalledModsStore.Remove(r.Id);
            try { Directory.Delete(game, true); } catch { }
        }
    }
}
