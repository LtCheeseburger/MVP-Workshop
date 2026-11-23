using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.Core.Vfs;

public static class ViewerCoverage
{
    public static string For(FormatId id) => id switch
    {
        FormatId.TerfArchive => "Entry directory / compression 0 and 5 / verified mod export",
        FormatId.MmapTexture => "Texture preview / PNG import and export / mipmaps",
        FormatId.TiburonModel => "Partial 3D preview / OBJ export / unsupported declarations listed",
        FormatId.TiburonDatabase => "Table/field editor / text decoding / checksum updates",
        FormatId.QklCache => "Source/cache directory / automatic export synchronization",
        FormatId.TiburonSkeleton => "Bone hierarchy / names / local translations; rotation and binding data undecoded",
        FormatId.SpeechHeader => "Partial speech header / payload undecoded",
        FormatId.SpeechEvents => "Event directory / bytecode undecoded",
        FormatId.TeamRoster => "Roster records / remaining fields undecoded",
        FormatId.BigArchive or FormatId.OffsetPack or FormatId.SshTexture or FormatId.RawRgb or FormatId.XboxResource or
        FormatId.SchlStream or FormatId.SchlContainer or FormatId.EaBank or FormatId.Elf or FormatId.ElfFragment or
        FormatId.Csv or FormatId.Text or FormatId.LocText or FormatId.TrueType or FormatId.LocBinary or FormatId.EaFontSfn or
        FormatId.SonyHd or FormatId.SonyBd or FormatId.AudioBankAbk or FormatId.MoirCsi or FormatId.AptMovie or FormatId.AptConst or
        FormatId.MpcMovie or FormatId.Vp6Movie or FormatId.MadMovie => "Specialized preview",
        FormatId.Unknown or FormatId.Binary or FormatId.Empty => "Raw / undecoded",
        FormatId.Folder => "Explorer",
        _ => "Structured fields / header",
    };
}
