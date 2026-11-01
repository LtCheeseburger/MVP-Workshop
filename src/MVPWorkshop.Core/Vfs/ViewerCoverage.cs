using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.Core.Vfs;

public static class ViewerCoverage
{
    public static string For(FormatId id) => id switch
    {
        FormatId.BigArchive or FormatId.OffsetPack or FormatId.SshTexture or FormatId.RawRgb or FormatId.XboxResource or
        FormatId.SchlStream or FormatId.SchlContainer or FormatId.EaBank or FormatId.Elf or FormatId.ElfFragment or
        FormatId.Csv or FormatId.Text or FormatId.LocText or FormatId.TrueType or FormatId.LocBinary or FormatId.EaFontSfn or
        FormatId.SonyHd or FormatId.SonyBd or FormatId.AudioBankAbk or FormatId.MoirCsi or FormatId.SpeechHeader or FormatId.AptMovie or FormatId.AptConst or
        FormatId.MpcMovie or FormatId.Vp6Movie => "Specialized preview",
        FormatId.Unknown or FormatId.Binary or FormatId.Empty => "Raw / undecoded",
        FormatId.Folder => "Explorer",
        _ => "Structured fields / header",
    };
}
