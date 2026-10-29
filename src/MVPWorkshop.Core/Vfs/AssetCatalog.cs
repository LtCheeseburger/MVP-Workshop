using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.Core.Vfs;

/// <summary>Semantic browsing aliases. Leaves retain the original node and archive path.</summary>
public static class AssetCatalog
{
    public static string Category(AssetNode node)
    {
        var p = "/" + node.Path.Replace('\\', '/').ToUpperInvariant() + "/";
        var ext = node.Extension;
        bool Has(string segment) => p.Contains("/" + segment + "/", StringComparison.Ordinal);
        if (ext == ".orl") return "System/Link fragments (.orl, opened with their .ord)";
        if (ModelCategory(node, Has) is { } model) return model;
        if (Has("UNIFORMS.BIG") || Has("UNIFORM") || Has("UNIFORMS")) return "Teams/Uniforms";
        if (Has("STADIUM")) return "Teams/Stadiums/3D stadiums";
        if (Has("STADIUMS.BIG") || Has("SMSTAD.BIG") || Has("CWSTHMBS.BIG")) return "Teams/Stadiums/Menu artwork";
        if (Has("LOGOS.BIG") || Has("CONFLOGO.BIG")) return "Teams/Logos and conferences";
        if (Has("PORTRAIT.BIG")) return "Teams/Player portraits";
        if (ext == ".fel") return "UI/Menus and layouts";
        if (ext is ".apt" or ".const" || Has("APTANIMS")) return "UI/APT movies and resources";
        if (Has("FONTS") || ext is ".sfn" or ".xfn" or ".ffn" or ".ttf") return "UI/Fonts";
        if (ext == ".loc") return "UI/Localization";
        if (Has("FRONTEND") && ext is ".mpc" or ".m2v" or ".vp6") return "Video/Frontend movies";
        if (Has("FRONTEND")) return "UI/Menu artwork and shared resources";
        if (Has("DATABASE")) return "Teams/Rosters and databases";
        if (Has("GHEAD.BIG")) return "Players/Heads and faces";
        if (Has("ANIMS")) return "Animations/" + (ext == ".ord" ? "Animation banks" : "Bank lists and data");
        if (Has("GEOMETRY.BIG") || Has("MODELS.BIG")) return "Players/Uniform and body textures";
        if (Has("AUDIO")) return "Audio/Banks and streams";
        if (Has("IGSHAPES")) return "UI/In-game HUD";
        return node.FormatInfo.Category switch
        {
            FormatCategory.Audio => "Audio/Other audio",
            FormatCategory.Texture => "Textures/Other textures",
            FormatCategory.Font => "UI/Fonts",
            FormatCategory.Code => "System/Executables and code",
            FormatCategory.Text => "Data/Text and configuration",
            _ => "Data/Other assets",
        };
    }

    /// <summary>3D models outside stadium archives, grouped by what they are (stadium models stay with their stadium).</summary>
    private static string? ModelCategory(AssetNode node, Func<string, bool> has)
    {
        var ext = node.Extension;
        if (ext is not (".ord" or ".o") || has("ANIMS") || has("APTANIMS") || has("STADIUM")) return null;
        if (ext == ".o" && !has("MODELS") && !has("MISC") && !has("MISCMOD.BIG")) return null;
        var stem = System.IO.Path.GetFileNameWithoutExtension(node.Name).ToLowerInvariant();
        if (stem.EndsWith("shadow") || stem.EndsWith("shadw")) return "Models/Shadows";
        if (stem.EndsWith("body")) return "Models/Players/Bodies";
        if (stem.EndsWith("head") || stem.EndsWith("_morph") || stem == "heye") return "Models/Players/Heads and eyes";
        if (stem.StartsWith("bat") || stem.StartsWith("brknbat") || stem.StartsWith("ball") || stem.StartsWith("glove") || stem.StartsWith("helm")) return "Models/Equipment";
        if (stem.StartsWith("crowd")) return "Models/Crowd";
        if (has("MISC") || has("MISCMOD.BIG")) return "Models/Scenery and props";
        return "Models/Other";
    }

    /// <summary>What a leaf is, for splitting a mixed archive (a stadium, say) into Models / Textures / Data folders.</summary>
    public static string Kind(AssetNode node) => node.Extension switch
    {
        ".ord" or ".o" => "Models",
        ".orl" => "Link fragments",
        _ => node.FormatInfo.Category switch
        {
            FormatCategory.Texture => "Textures",
            FormatCategory.Audio => "Audio",
            FormatCategory.Ui => "UI",
            _ => "Data",
        },
    };

    /// <summary>The archive (or folder) a leaf is listed under; loose archive folders count as archives and loose texture folders are skipped.</summary>
    public static string PackName(AssetNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
            if (!p.IsLooseTexturePack && (!p.IsFolder || p.IsLooseArchive)) return p.Name;
        return node.Parent?.Name ?? "Files";
    }

    public static SortedDictionary<string, List<AssetNode>> Build(IEnumerable<AssetNode> nodes)
    {
        var groups = new SortedDictionary<string, List<AssetNode>>(StringComparer.OrdinalIgnoreCase);
        // a loose texture folder is listed by its DDS files, not as the .xsh they rebuild
        foreach (var node in nodes.Where(n => !n.IsFolder && !n.IsLooseTexturePack && !n.IsContainer))
        {
            var category = Category(node);
            if (!groups.TryGetValue(category, out var list)) groups[category] = list = [];
            list.Add(node);
        }
        foreach (var list in groups.Values) list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return groups;
    }
}
