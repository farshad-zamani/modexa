using Modexa.Core.Games;
using Modexa.Core.Prepare;
using Xunit;

namespace Modexa.Core.Tests;

public class PrepareManifestTests
{
    private static PrepareManifest Sample() => new()
    {
        Steps =
        {
            new StepEntry
            {
                Step = PrepareStep.GameConfig,
                Bundles =
                {
                    new BundleEntry { Url = "u-legacy-3889", Edition = "Legacy", Build = 3889 },
                    new BundleEntry { Url = "u-legacy-any", Edition = "Legacy" },
                    new BundleEntry { Url = "u-enhanced-any", Edition = "Enhanced" },
                }
            }
        }
    };

    [Fact]
    public void Resolve_prefers_exact_edition_and_build()
    {
        var b = Sample().Resolve(GameId.GtaV, PrepareStep.GameConfig, GameEdition.Legacy, 3889);
        Assert.Equal("u-legacy-3889", b!.Url);
    }

    [Fact]
    public void Resolve_falls_back_to_edition_any_build()
    {
        var b = Sample().Resolve(GameId.GtaV, PrepareStep.GameConfig, GameEdition.Legacy, 9999);
        Assert.Equal("u-legacy-any", b!.Url);
    }

    [Fact]
    public void Resolve_matches_other_edition()
    {
        var b = Sample().Resolve(GameId.GtaVEnhanced, PrepareStep.GameConfig, GameEdition.Enhanced, 1234);
        Assert.Equal("u-enhanced-any", b!.Url);
    }

    [Fact]
    public void Default_has_runner_for_each_supported_game()
    {
        var m = PrepareManifest.Default();
        foreach (var g in new[] { GameId.GtaSanAndreas, GameId.GtaIV, GameId.RedDeadRedemption1, GameId.RedDeadRedemption2, GameId.Cyberpunk2077 })
            Assert.True(m.HasStep(g, PrepareStep.ModRunner), $"missing runner for {g}");
        Assert.NotNull(m.Resolve(GameId.GtaV, PrepareStep.ModRunner, GameEdition.Legacy, 3889));
        Assert.NotNull(m.Resolve(GameId.GtaVEnhanced, PrepareStep.ModRunner, GameEdition.Enhanced, 1158));
    }

    [Fact]
    public void Resolve_returns_null_for_unknown_step()
    {
        var b = Sample().Resolve(GameId.GtaV, PrepareStep.Menu, GameEdition.Legacy, 3889);
        Assert.Null(b);
    }
}
