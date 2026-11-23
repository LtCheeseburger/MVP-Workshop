using System.Runtime.CompilerServices;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Vfs;

/// <summary>Browsing aliases for NASCAR's source packs. Asset identity and export paths are never renamed.</summary>
public static class NascarAssetCatalog
{
    public sealed record Entry(AssetNode Asset, string Category, string SourcePack, string Label, string Kind);
    private static readonly ConditionalWeakTable<AssetNode, Entry> Cache = new();
    public static readonly string[] CategoryOrder = ["Cars", "Drivers", "Tracks", "Menus and HUD", "Audio", "Video", "Databases", "Effects and scenery", "System", "Other game data"];
    private static readonly IReadOnlyDictionary<string, string> Packs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CARDATA"] = "Cars/Paint schemes and car assets", ["CARPARTS"] = "Cars/Parts and equipment", ["CARRESFE"] = "Cars/Showroom and selection", ["UI_CARS"] = "Cars/Menu car artwork",
        ["PLATSLRG"] = "Cars/Paint schemes/Large textures", ["PLATSSML"] = "Cars/Paint schemes/Small textures", ["SPONLOGO"] = "Cars/Sponsor logos", ["TEAMLOGO"] = "Cars/Team logos",
        ["DRIVNUMB"] = "Drivers/Numbers/Large numbers", ["DRIVSNUM"] = "Drivers/Numbers/Small numbers", ["DRIVSIGS"] = "Drivers/Signatures", ["DRVRPICS"] = "Drivers/Portraits", ["MMDRVIMG"] = "Drivers/Menu artwork",
        ["CHARDAT"] = "Drivers/Character models and assets", ["CHARTEX"] = "Drivers/Character textures",
        ["TK_REND"] = "Tracks/Track geometry and artwork", ["TK_PHYS"] = "Tracks/Physics and collision", ["TK_SKY"] = "Tracks/Skies and backgrounds", ["TK_SHAD"] = "Tracks/Shadows", ["TK_PBNR"] = "Tracks/Banners", ["TK_SCORE"] = "Tracks/Scoreboards", ["TK_SWAP"] = "Tracks/Swappable track assets",
        ["TRCKLOGO"] = "Tracks/Logos", ["TRCKPICT"] = "Tracks/Preview pictures", ["TMAPDAT"] = "Tracks/Maps and track data", ["AI_SPDS"] = "Tracks/AI speed data", ["SPDCRNRS"] = "Tracks/Corner speed data", ["CAMERAS"] = "Tracks/Cameras",
        ["NMUSIC"] = "Audio/Music", ["NSOUND"] = "Audio/Sound effects", ["NSPCHDAT"] = "Audio/Speech/Streams", ["NSPCHHDR"] = "Audio/Speech/Headers", ["CAFEOSND"] = "Audio/Online menu sounds",
        ["MOVIES"] = "Video/Movies", ["PRMOVIES"] = "Video/Promotional movies", ["CHALSTIL"] = "Video/Challenge stills",
        ["NASCAR11"] = "Video/NASCAR movie packs", ["NASCAR12"] = "Video/NASCAR movie packs", ["NASCAR13"] = "Video/NASCAR movie packs",
        ["NASCAR"] = "Databases/Main racing database", ["GMODEDB"] = "Databases/Game modes",
        ["BACKPICT"] = "Menus and HUD/Backgrounds", ["CHNL_IMG"] = "Menus and HUD/Channel artwork", ["EVSELART"] = "Menus and HUD/Event selection", ["FTTT_PIC"] = "Menus and HUD/Game mode artwork", ["SNMNIMGS"] = "Menus and HUD/Menu artwork", ["RACELOGO"] = "Menus and HUD/Race logos", ["MERCHIMG"] = "Menus and HUD/Merchandise", ["ICONS"] = "Menus and HUD/Icons",
        ["FONTS"] = "Menus and HUD/Fonts", ["OSDKFONT"] = "Menus and HUD/Fonts", ["OSDKSTRN"] = "Menus and HUD/Text and localization", ["UI_PROJ"] = "Menus and HUD/UI project data", ["UIONLINE"] = "Menus and HUD/Online menus",
        ["EFFECT"] = "Effects and scenery/Effects", ["SIMPOBJ"] = "Effects and scenery/Scene objects",
        ["LOADDATA"] = "System/Loading resources", ["NV_REC"] = "System/Recorded data",
    };

    public static AssetNode SourceArchive(AssetNode node)
    {
        var source = node;
        for (var p = node; p.Parent is not null; p = p.Parent)
            if (!p.IsFolder && (p.DiskPath is not null || p.DiscEntry is not null)) source = p;
        return source;
    }

    public static string Category(AssetNode node)
    {
        var source = SourceArchive(node);
        string stem = Path.GetFileNameWithoutExtension(source.Name).ToUpperInvariant();
        if (source.Extension == ".qkl") return "System/Quick-load caches";
        if (Packs.TryGetValue(stem, out var category)) return category;
        if (stem.StartsWith("CHALFMV", StringComparison.Ordinal)) return "Video/Challenge movies";
        if (stem.StartsWith("CAFE", StringComparison.Ordinal)) return stem.StartsWith("CAFEO", StringComparison.Ordinal) ? "Menus and HUD/Online menus and resources" : "Menus and HUD/Frontend menus and resources";
        if (stem.StartsWith("SON", StringComparison.Ordinal)) return "Menus and HUD/Online scripts";
        if (stem.StartsWith("SCR", StringComparison.Ordinal)) return stem is "SCRINGAM" or "SCRRACE" or "SCRSPEED" ? "Menus and HUD/In-race scripts and HUD" : "Menus and HUD/Frontend scripts";
        if (stem.StartsWith("LIBS", StringComparison.Ordinal)) return "Menus and HUD/Shared script libraries";
        if (stem.StartsWith("TK_", StringComparison.Ordinal)) return "Tracks/Other track resources";
        return node.FormatInfo.Category switch
        {
            FormatCategory.Audio => "Audio/Other audio packs",
            FormatCategory.Video => "Video/Other movies",
            FormatCategory.Code => "System/Executables and code",
            FormatCategory.Font => "Menus and HUD/Fonts",
            _ when node.Format == FormatId.TiburonDatabase => "Databases/Other databases",
            _ => "Other game data/Unclassified resources",
        };
    }

    public static string Kind(AssetNode node) => node.Format switch
    {
        FormatId.TiburonModel => "3D models",
        FormatId.TiburonDatabase => "Databases",
        FormatId.TerfArchive => "Archives",
        _ => node.FormatInfo.Category switch
        {
            FormatCategory.Texture => "Textures", FormatCategory.Audio => "Audio", FormatCategory.Font => "Fonts",
            FormatCategory.Video => "Movies", FormatCategory.Text => "Text", FormatCategory.Ui => "UI resources",
            _ => "Data and other resources",
        },
    };

    public static Entry Describe(AssetNode node) => Cache.GetValue(node, n =>
    {
        var source = SourceArchive(n);
        string id = n == source ? n.Name : n.Key[(source.Key.Length + 1)..].Replace(".bin", "", StringComparison.OrdinalIgnoreCase).Replace('/', '›');
        string label = n.Format switch
        {
            FormatId.MmapTexture => "Texture pack",
            FormatId.TiburonModel => "3D model",
            FormatId.TiburonDatabase => "Database",
            _ => n.FormatInfo.Name,
        };
        try
        {
            // Read only names/dimensions, not texture pixels. A pack can contain several named textures.
            if (n.Format == FormatId.MmapTexture)
            {
                var bytes = n.OpenOriginal(); var header = bytes.ReadBytes(0, 40);
                if (header.Length == 40)
                {
                    uint bitmap = Bin.U32LE(header, 24), names = Bin.U32LE(header, 32);
                    int files = Bin.U16LE(header, 12); var size = bytes.ReadBytes(bitmap, 4);
                    var nameBytes = bytes.ReadBytes(names, 16); string name = Bin.CString(nameBytes, 0, 16);
                    if (name.Length > 0 && name.All(c => c is >= ' ' and <= '~')) label = name;
                    else label = files > 1 ? $"{files} textures" : "Texture";
                    if (files > 1 && name.Length > 0) label += $" (+{files - 1})";
                    if (size.Length == 4) label += $" · {Bin.U16LE(size, 0)}×{Bin.U16LE(size, 2)}";
                }
            }
            else if (n.Format == FormatId.TiburonModel)
            {
                var bytes = n.OpenOriginal(); var info = TiburonModel.Read(bytes).FirstOrDefault(s => s.Tag == "Info");
                if (info is not null)
                {
                    string name = Bin.CString(bytes.ReadBytes(info.Offset, (int)Math.Min(info.Length, 128)), 0, 128);
                    if (name.Length > 0 && name.All(c => c is >= ' ' and <= '~')) label = name;
                }
            }
        }
        catch { /* Keep the entry and its stable source identifier visible even if its metadata cannot be decoded. */ }
        return new(n, Category(n), source.Name, $"{id} · {label}", Kind(n));
    });

    public static string SearchText(AssetNode node)
    {
        var entry = Describe(node);
        return $"{node.Path} {entry.Category} {entry.Kind} {entry.Label}";
    }

    public static IReadOnlyList<Entry> Build(IEnumerable<AssetNode> nodes) => nodes
        .Where(n => !n.IsFolder && (!n.IsContainer || n.Children.Count == 0))
        .Select(Describe).OrderBy(e => Array.IndexOf(CategoryOrder, e.Category.Split('/')[0]))
        .ThenBy(e => e.Category, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.SourcePack, StringComparer.OrdinalIgnoreCase)
        .ThenBy(e => e.Asset.Key, StringComparer.OrdinalIgnoreCase).ToArray();
}
