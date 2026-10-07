using Modexa.Core.I18n;
using Modexa.Core.Rpf;

namespace Modexa.App.Services;

/// <summary>User-facing text for a failed mods\update\update.rpf creation.</summary>
public static class ModsArchiveMessages
{
    public static string For(ModsArchiveException ex) => ex.Code switch
    {
        ModsArchiveException.NoKeys => Loc.Instance["ModsRpf_NoKeys"],
        ModsArchiveException.NoSpace => Loc.Instance.Format("ModsRpf_NoSpace", ex.Message),
        ModsArchiveException.SourceMissing => Loc.Instance["ModsRpf_Missing"],
        _ => Loc.Instance.Format("ModsRpf_Failed", ex.Message)
    };
}
