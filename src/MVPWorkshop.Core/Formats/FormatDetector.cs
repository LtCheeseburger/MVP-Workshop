using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public enum FormatId
{
    Unknown,
    Empty,
    Folder,
    BigArchive,
    SchlStream,
    SchlContainer,
    EaBank,
    AsfLut,
    SshTexture,
    AptMovie,
    AptConst,
    Elf,
    ElfFragment,
    MpcMovie,
    AudioBankAbk,
    MoirCsi,
    LocBinary,
    LocText,
    Text,
    Csv,
    Ps2IconSys,
    Ps2Icon,
    TrueType,
    SonyHd,
    SonyBd,
    EaFontSfn,
    BilgSkeleton,
    PfrFont,
    AdachiFont,
    RawRgb,
    Ps2Overlay,
    OffsetPack,
    SpeechHeader,
    Vp6Movie,
    XboxExecutable,
    XboxResource,
    Binary,
}

public enum FormatCategory { Folder, Archive, Texture, Audio, Video, Model, Code, Ui, Text, Data, Font, Unknown }

public sealed record FormatInfo(FormatId Id, string Name, FormatCategory Category, string Glyph, string Description, bool IsContainer = false);

public static class FormatDetector
{
    public static readonly IReadOnlyDictionary<FormatId, FormatInfo> Info = new Dictionary<FormatId, FormatInfo>
    {
        [FormatId.Unknown] = new(FormatId.Unknown, "Unknown", FormatCategory.Unknown, "", "Unidentified data"),
        [FormatId.Empty] = new(FormatId.Empty, "Empty", FormatCategory.Unknown, "", "Zero-length entry"),
        [FormatId.Folder] = new(FormatId.Folder, "Folder", FormatCategory.Folder, "", "Directory", true),
        [FormatId.BigArchive] = new(FormatId.BigArchive, "BIGF Archive", FormatCategory.Archive, "", "EA BIG archive (big-endian directory, RefPack-compressed entries)", true),
        [FormatId.SchlStream] = new(FormatId.SchlStream, "EA SCHl Stream", FormatCategory.Audio, "", "EA blocked audio stream (SCHl/SCCl/SCDl/SCEl)"),
        [FormatId.SchlContainer] = new(FormatId.SchlContainer, "SCHl Stream Pack", FormatCategory.Audio, "", "Concatenated EA SCHl streams", true),
        [FormatId.EaBank] = new(FormatId.EaBank, "EA Sound Bank", FormatCategory.Audio, "", "EA BNKl sound bank (PT patch headers)"),
        [FormatId.AsfLut] = new(FormatId.AsfLut, "ASF Lookup", FormatCategory.Audio, "", "ASFT lookup table for ASF banks"),
        [FormatId.SshTexture] = new(FormatId.SshTexture, "SSH Shape", FormatCategory.Texture, "", "EA SHPS texture pack (PS2 .ssh) or SHPX texture pack (Xbox .xsh)"),
        [FormatId.XboxResource] = new(FormatId.XboxResource, "Xbox Resource", FormatCategory.Texture, "", "Xbox packed resource (XPR0) holding a single texture"),
        [FormatId.AptMovie] = new(FormatId.AptMovie, "APT Movie", FormatCategory.Ui, "", "EA APT (Flash-derived) UI movie"),
        [FormatId.AptConst] = new(FormatId.AptConst, "APT Constants", FormatCategory.Ui, "", "APT constant file"),
        [FormatId.Elf] = new(FormatId.Elf, "ELF Object", FormatCategory.Code, "", "MIPS R5900 ELF (executable, IRX, or relocatable model/data object)"),
        [FormatId.XboxExecutable] = new(FormatId.XboxExecutable, "Xbox Executable", FormatCategory.Code, "", "Xbox XBE executable (certificate and section table)"),
        [FormatId.ElfFragment] = new(FormatId.ElfFragment, "ELF Link Fragment", FormatCategory.Model, "", "ORL relocation/section-name fragment paired with an .ord"),
        [FormatId.MpcMovie] = new(FormatId.MpcMovie, "MPC Movie", FormatCategory.Video, "", "MPCh container with MPEG-2 video"),
        [FormatId.Vp6Movie] = new(FormatId.Vp6Movie, "VP6 Movie", FormatCategory.Video, "", "EA MVhd container with On2 VP6 video and EA-XA audio (Xbox)"),
        [FormatId.AudioBankAbk] = new(FormatId.AudioBankAbk, "ABK Bank", FormatCategory.Audio, "", "EA AEMS ABKC audio event bank"),
        [FormatId.MoirCsi] = new(FormatId.MoirCsi, "CSI Script", FormatCategory.Audio, "", "MOIR audio script/state data"),
        [FormatId.LocBinary] = new(FormatId.LocBinary, "LOC Strings", FormatCategory.Text, "", "LOCH binary localisation table"),
        [FormatId.LocText] = new(FormatId.LocText, "LOC Text", FormatCategory.Text, "", "KEY=Value localisation text"),
        [FormatId.Text] = new(FormatId.Text, "Text", FormatCategory.Text, "", "Plain text"),
        [FormatId.Csv] = new(FormatId.Csv, "CSV Table", FormatCategory.Data, "", "Comma/tab separated table"),
        [FormatId.Ps2IconSys] = new(FormatId.Ps2IconSys, "PS2 icon.sys", FormatCategory.Data, "", "PS2 memory card icon descriptor"),
        [FormatId.Ps2Icon] = new(FormatId.Ps2Icon, "PS2 3D Icon", FormatCategory.Model, "", "PS2 memory card 3D icon (.ico)"),
        [FormatId.TrueType] = new(FormatId.TrueType, "TrueType Font", FormatCategory.Font, "", "TrueType font"),
        [FormatId.SonyHd] = new(FormatId.SonyHd, "Sony HD Bank", FormatCategory.Audio, "", "SCEI sound bank header (pairs with .BD)"),
        [FormatId.SonyBd] = new(FormatId.SonyBd, "Sony BD Bank", FormatCategory.Audio, "", "SCEI sound bank body (PS-ADPCM samples)"),
        [FormatId.EaFontSfn] = new(FormatId.EaFontSfn, "SFN Font", FormatCategory.Font, "", "EA bitmap font (FntS/FNTS): glyph table, kerning, PAL4/PAL8 atlas"),
        [FormatId.BilgSkeleton] = new(FormatId.BilgSkeleton, "GLD Skeleton", FormatCategory.Model, "", "BILG skeleton / bone layout"),
        [FormatId.PfrFont] = new(FormatId.PfrFont, "PFR Font", FormatCategory.Font, "", "Bitstream Portable Font Resource"),
        [FormatId.AdachiFont] = new(FormatId.AdachiFont, "PF Font", FormatCategory.Font, "", "Network GUI bitmap font"),
        [FormatId.RawRgb] = new(FormatId.RawRgb, "Raw RGB Image", FormatCategory.Texture, "", "Headerless 24-bit RGB image"),
        [FormatId.Ps2Overlay] = new(FormatId.Ps2Overlay, "Code Overlay", FormatCategory.Code, "", "Raw MIPS code overlay"),
        [FormatId.OffsetPack] = new(FormatId.OffsetPack, "OFF-indexed Pack", FormatCategory.Data, "", "Headerless record pack split by the matching .OFF index", true),
        [FormatId.SpeechHeader] = new(FormatId.SpeechHeader, "Speech Header", FormatCategory.Audio, "", "PROJx speech record header (one per stream in the matching DAT pack)"),
        [FormatId.Binary] = new(FormatId.Binary, "Binary", FormatCategory.Data, "", "Unidentified binary data"),
    };

    public static FormatId Detect(string name, IByteSource src)
    {
        if (src.Length == 0) return FormatId.Empty;
        var head = src.ReadBytes(0, 512);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var magic = Bin.FourCC(head);

        switch (magic)
        {
            case "BIGF" or "BIG4": return FormatId.BigArchive;
            case "SCHl": return LooksLikeSchlContainer(src) ? FormatId.SchlContainer : FormatId.SchlStream;
            case "BNKl" or "BNKb": return FormatId.EaBank;
            case "ASFT": return FormatId.AsfLut;
            case "SHPS" or "ShpS" or "SHPX": return FormatId.SshTexture;
            case "MPCh": return FormatId.MpcMovie;
            case "MVhd": return FormatId.Vp6Movie;
            case "XBEH": return FormatId.XboxExecutable;
            case "XPR0": return FormatId.XboxResource;
            case "ABKC": return FormatId.AudioBankAbk;
            case "MOIR": return FormatId.MoirCsi;
            case "LOCH": return FormatId.LocBinary;
            case "PS2D": return FormatId.Ps2IconSys;
            case "IECS": return ext == ".bd" ? FormatId.SonyBd : FormatId.SonyHd;
            case "FntS" or "FNTS" or "FntX" or "FNTX": return FormatId.EaFontSfn;
            case "BILG": return FormatId.BilgSkeleton;
            case "PFR1": return FormatId.PfrFont;
        }
        if (head.Length >= 4 && head[0] == 0x7F && magic == ".ELF") return FormatId.Elf;
        if (magic == "Apt ")
            return head.Length >= 12 && System.Text.Encoding.ASCII.GetString(head, 4, 8) == "constant" ? FormatId.AptConst : FormatId.AptMovie;
        if (head.Length >= 4 && head[0] == 0 && head[1] == 1 && head[2] == 0 && head[3] == 0 && ext == ".ttf") return FormatId.TrueType;
        if (ext == ".ico" && head.Length >= 4 && head[0] == 0 && head[1] == 0 && head[2] == 1 && head[3] == 0) return FormatId.Ps2Icon;
        if (ext == ".bd") return FormatId.SonyBd;
        if (ext == ".pf" && head.Length > 0x20 && Bin.CString(head, 0x10).Contains("font", StringComparison.OrdinalIgnoreCase)) return FormatId.AdachiFont;
        if (ext == ".orl" && head.Length > 1 && head[0] == 0 && head[1] == (byte)'.') return FormatId.ElfFragment;
        if (ext == ".rgb" && RawRgbDims(src.Length) is not null) return FormatId.RawRgb;
        if (ext == ".bin" && name.Contains("overlay", StringComparison.OrdinalIgnoreCase)) return FormatId.Ps2Overlay;
        if (ext == ".hdr" && src.Length >= 18 && head[4] == 0xFF && head[5] == 0xFF) return FormatId.SpeechHeader;

        if (LooksLikeText(head))
        {
            if (ext == ".loc") return FormatId.LocText;
            if (ext is ".csv" or ".tsv") return FormatId.Csv;
            return FormatId.Text;
        }
        return FormatId.Binary;
    }

    public static (int W, int H)? RawRgbDims(long size)
    {
        foreach (var (w, h) in new[] { (640, 448), (640, 480), (512, 448), (512, 512), (320, 240), (256, 256), (640, 224), (512, 256), (128, 128) })
            if ((long)w * h * 3 == size) return (w, h);
        return null;
    }

    private static bool LooksLikeSchlContainer(IByteSource src)
    {
        // A single stream ends at its SCEl block; anything after that starting with SCHl is another stream.
        long o = 0;
        for (var guard = 0; guard < 200000 && o + 8 <= src.Length; guard++)
        {
            var id = src.ReadBytes(o, 4);
            var size = src.U32LE(o + 4);
            if (size < 8 || size > 0x1000000) size = src.U32BE(o + 4);
            if (size < 8) return false;
            o += size;
            if (Bin.FourCC(id) == "SCEl")
            {
                // skip zero padding
                while (o + 4 <= src.Length && src.U32LE(o) == 0) o += 4;
                return o + 4 <= src.Length && Bin.FourCC(src.ReadBytes(o, 4)) == "SCHl";
            }
        }
        return false;
    }

    public static bool LooksLikeText(ReadOnlySpan<byte> d)
    {
        if (d.Length == 0) return false;
        var printable = 0;
        foreach (var b in d)
        {
            if (b == 0) return false;
            if (b is 9 or 10 or 13 || b >= 0x20) printable++;
        }
        return printable >= d.Length * 0.97;
    }
}
