using System.IO;
using Modexa.Core.Install;
using Xunit;

namespace Modexa.Core.Tests;

/// <summary>dlclist.xml edits must change nothing but the one line (tests use a real, irregular file).</summary>
public class DlcListTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Removing_a_pack_in_the_middle_changes_nothing_else()
    {
        string with = Fixture("dlclist.with-bugatti.xml"), without = Fixture("dlclist.without-bugatti.xml");
        Assert.Equal(without, DlcList.RemoveEntry(with, "bugatti"));
    }

    [Fact]
    public void Adding_then_removing_a_pack_gives_the_file_back_exactly()
    {
        string original = Fixture("dlclist.without-bugatti.xml");
        string added = DlcList.AddEntry(original, "newcar");
        Assert.Contains("dlcpacks:/newcar/", added);
        Assert.Equal(original, DlcList.RemoveEntry(added, "newcar"));
    }
}
