namespace MVPWorkshop.Core.Formats.Eagl;

[Flags]
public enum PlayerRole { None = 0, Batter = 1, Fielder = 2, Catcher = 4, All = Batter | Fielder | Catcher }

/// <summary>Readable identity of one model part, from the model's name table (<c>G_HELM1_Shape~…</c>).</summary>
public sealed record EaglPartInfo(string Code, string Group, string Title, PlayerRole Roles);

/// <summary>
/// Player bodies (hi/me/lobody) carry every piece of kit at once — three caps, three helmets, both gloves, catcher
/// gear, three pants styles, wristbands, glasses — and the game toggles them per player. The model's name table
/// lists one shape name per part in part order; this maps those names to body areas and to the role loadouts
/// used for the Batter / Fielder / Catcher presets. Role assignments are inferred from the names (not from game
/// code), and alternates such as HAT2/HAT3 stay off in every preset.
/// </summary>
public static class EaglPartNames
{
    public static readonly string[] GroupOrder =
        ["Head & headwear", "Eyewear", "Torso", "Arms & sleeves", "Hands & gloves", "Legs", "Feet", "Catcher gear", "Batter gear", "Wristbands", "Shadows", "Other"];

    private const PlayerRole B = PlayerRole.Batter, F = PlayerRole.Fielder, C = PlayerRole.Catcher, A = PlayerRole.All, N = PlayerRole.None;

    private static readonly Dictionary<string, (string Group, string Title, PlayerRole Roles)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HAT1"] = ("Head & headwear", "Cap", F),
        ["HAT2"] = ("Head & headwear", "Cap (alt 2)", N),
        ["HAT3"] = ("Head & headwear", "Cap (alt 3)", N),
        ["BRIM"] = ("Head & headwear", "Cap brim", N),
        ["HELM1"] = ("Head & headwear", "Batting helmet", B),
        ["HELM2"] = ("Head & headwear", "Batting helmet (alt 2)", N),
        ["HELM3"] = ("Head & headwear", "Batting helmet (alt 3)", N),
        ["MASK_"] = ("Head & headwear", "Helmet face guard", N),
        ["GGL1"] = ("Eyewear", "Glasses 1", N),
        ["GGL2"] = ("Eyewear", "Glasses 2", N),
        ["GGL3"] = ("Eyewear", "Glasses 3", N),
        ["TORSO"] = ("Torso", "Jersey", A),
        ["TORSO_M"] = ("Torso", "Jersey (alt)", N),
        ["NBFA"] = ("Torso", "Front number", A),
        ["ARMS"] = ("Arms & sleeves", "Arms", A),
        ["LSLV"] = ("Arms & sleeves", "Long sleeves", N),
        ["MAJAL"] = ("Arms & sleeves", "Undershirt cuff L", N),
        ["MAJAR"] = ("Arms & sleeves", "Undershirt cuff R", N),
        ["MAJML"] = ("Arms & sleeves", "Undershirt seam L", N),
        ["MAJMR"] = ("Arms & sleeves", "Undershirt seam R", N),
        ["RHD"] = ("Hands & gloves", "Right hand", A),
        ["LHD"] = ("Hands & gloves", "Left hand", B),
        ["GLVF"] = ("Hands & gloves", "Fielder's glove", F),
        ["GLVC"] = ("Hands & gloves", "Catcher's mitt", C),
        ["LEGSP"] = ("Legs", "Pants", A),
        ["LEGS_L"] = ("Legs", "Pants (style L)", N),
        ["LEGS_A"] = ("Legs", "Pants (style A)", N),
        ["SHOE"] = ("Feet", "Shoes", A),
        ["HHLM"] = ("Catcher gear", "Catcher's helmet", C),
        ["HCAGE"] = ("Catcher gear", "Helmet cage", N),
        ["MASK"] = ("Catcher gear", "Catcher's mask", C),
        ["CHSTP"] = ("Catcher gear", "Chest protector", C),
        ["SHIN"] = ("Catcher gear", "Shin guards", C),
        ["BATTERSHIN"] = ("Batter gear", "Shin guard", B),
        ["ELBOGH"] = ("Batter gear", "Elbow guard (hard)", B),
        ["ELBOGS"] = ("Batter gear", "Elbow guard (soft)", N),
        ["WRISTBR1"] = ("Wristbands", "Wristband R1", N),
        ["WRISTBR2"] = ("Wristbands", "Wristband R2", N),
        ["WRISTBR3"] = ("Wristbands", "Wristband R3", N),
        ["WRISTBL1"] = ("Wristbands", "Wristband L1", N),
        ["WRISTBL2"] = ("Wristbands", "Wristband L2", N),
        ["WRISTBL3"] = ("Wristbands", "Wristband L3", N),
        ["HATSHD"] = ("Shadows", "Cap shadow", F),
        ["CHSTSHD"] = ("Shadows", "Chest shadow", A),
    };

    /// <summary>"G_HELM1_Shape~MVPIrradTextureEnvmap_Skin" → "HELM1", "G_MASK_Shape" → "MASK_".</summary>
    public static string Code(string shape)
    {
        var s = shape;
        if (s.IndexOf('~') is var t and >= 0) s = s[..t];
        if (s.StartsWith("G_", StringComparison.Ordinal)) s = s[2..];
        if (s.EndsWith("Shape", StringComparison.Ordinal)) s = s[..^5];
        // "HAT1_" → HAT1 but keep "MASK_" (distinct from MASK)
        if (s.EndsWith('_') && s.Length > 1 && char.IsDigit(s[^2])) s = s[..^1];
        return s;
    }

    /// <summary>Part infos for a model, or null when its name table does not line up with its parts.</summary>
    public static List<EaglPartInfo>? For(EaglModel m)
    {
        if (m.Names.Count != m.Parts.Count || m.Names.Count == 0 || !m.Names.All(n => n.StartsWith("G_", StringComparison.Ordinal))) return null;
        return m.Names.Select(n =>
        {
            var code = Code(n);
            return Known.TryGetValue(code, out var k) ? new EaglPartInfo(code, k.Group, k.Title, k.Roles) : new EaglPartInfo(code, "Other", code, PlayerRole.All);
        }).ToList();
    }

    /// <summary>True when the model has the role-specific kit that makes presets meaningful.</summary>
    public static bool HasLoadouts(IEnumerable<EaglPartInfo> parts) => parts.Any(p => p.Roles != PlayerRole.All);
}
