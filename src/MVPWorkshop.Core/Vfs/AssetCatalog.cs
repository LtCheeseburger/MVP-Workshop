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
        if (Has("ANIMS")) return "Players/Animations";
        if (Has("GEOMETRY.BIG") || Has("MODELS.BIG")) return "Players/Models and equipment";
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

    public static SortedDictionary<string, List<AssetNode>> Build(IEnumerable<AssetNode> nodes)
    {
        var groups = new SortedDictionary<string, List<AssetNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Where(n => !n.IsFolder && !n.IsContainer))
        {
            var category = Category(node);
            if (!groups.TryGetValue(category, out var list)) groups[category] = list = [];
            list.Add(node);
        }
        foreach (var list in groups.Values) list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return groups;
    }
}
