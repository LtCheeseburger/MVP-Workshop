using System.Runtime.CompilerServices;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Vfs;

/// <summary>Preview resource matching. Authored material names are kept separate from runtime MMAP slots.</summary>
public static class NascarModelResources
{
    public sealed record Model(AssetNode Node, string Name, IReadOnlyList<TiburonModel.Material> Materials)
    { public string Label => $"{Name} · {Node.Key}"; }
    public sealed record TexturePack(AssetNode Node, IReadOnlyList<string> Names)
    { public string Label => $"{Node.Key} · {string.Join(", ", Names)}"; }
    public sealed record Vehicle(int TextureId, int ModelId, string ModelName, string Body, string Name);
    public sealed record Catalog(IReadOnlyList<Model> Models, IReadOnlyList<TexturePack> Textures, IReadOnlyDictionary<int, Vehicle> Vehicles);
    private static readonly ConditionalWeakTable<AssetNode, Catalog> Cache = new();
    private sealed record Choice(string Name);
    private static readonly ConditionalWeakTable<AssetNode, Choice> Choices = new();
    public static string? PreferredModel(AssetNode texture) => Choices.TryGetValue(texture, out var choice) ? choice.Name : VehicleFor(texture)?.ModelName;
    public static void RememberModel(AssetNode texture, Model model) { Choices.Remove(texture); Choices.Add(texture, new(model.Name)); }
    public static bool SameVehicleFamily(string a, string b)
    {
        string Family(string name)
        {
            foreach (string prefix in new[] { "rcar", "pcar", "rtruck", "ModFLite" }) if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return prefix.ToUpperInvariant();
            int lod = name.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase); return (lod >= 0 ? name[..lod] : name).ToUpperInvariant();
        }
        return Family(a) == Family(b);
    }
    public static bool IsRelevant(AssetNode node) => Path.GetFileNameWithoutExtension(NascarAssetCatalog.SourceArchive(node).Name).ToUpperInvariant() is "CARDATA" or "CARRESFE" or "CHARDAT" or "CHARTEX";
    private static bool IsCharacter(AssetNode node) => NascarAssetCatalog.SourceArchive(node).Name.StartsWith("CHAR", StringComparison.OrdinalIgnoreCase);
    public static Catalog Get(AssetNode node)
    {
        while (node.Parent is not null) node = node.Parent;
        return Cache.GetValue(node, root =>
        {
            var models = new List<Model>(); var textures = new List<TexturePack>();
            IEnumerable<AssetNode> Packs(AssetNode parent)
            {
                foreach (var child in parent.Children)
                    if (child.IsFolder) { foreach (var pack in Packs(child)) yield return pack; }
                    else if (IsRelevant(child)) yield return child;
            }
            foreach (var pack in Packs(root))
            foreach (var asset in pack.EnumerateDeep())
            {
                try
                {
                    if (asset.Format == FormatId.TiburonModel)
                    {
                        var bytes = asset.ReadAll(); var sections = TiburonModel.Read(new MemoryByteSource(bytes));
                        if (!sections.Any(s => s.Tag == "Geom")) continue;
                        var info = sections.FirstOrDefault(s => s.Tag == "Info");
                        string name = info is null ? asset.Name : Bin.CString(bytes, (int)info.Offset, (int)Math.Min(info.Length, 128));
                        models.Add(new(asset, name, TiburonModel.ReadMaterials(bytes)));
                    }
                    else if (asset.Format == FormatId.MmapTexture)
                    {
                        var file = MmapTexture.Read(asset.ReadAll());
                        textures.Add(new(asset, file.Images.Where(i => i.Mip == 0).Select(i => i.Name).ToArray()));
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException) { /* Other valid resources remain usable. */ }
                finally { if (!asset.IsContainer) asset.DropCache(); }
            }
            var vehicles = new Dictionary<int, Vehicle>();
            AssetNode? FindDatabase(AssetNode parent)
            {
                foreach (var child in parent.Children)
                {
                    if (child.Name.Equals("NASCAR.DB", StringComparison.OrdinalIgnoreCase)) return child;
                    if (child.IsFolder && FindDatabase(child) is { } nested) return nested;
                }
                return null;
            }
            if (FindDatabase(root) is { } database)
            {
                try
                {
                    var db = TiburonDatabase.Read(database.ReadAll()); var cars = db.Tables.FirstOrDefault(t => t.Name == "CARS"); var skins = db.Tables.FirstOrDefault(t => t.Name == "CRSK");
                    if (cars is not null && skins is not null)
                    {
                        string Read(TiburonDatabase.Table table, int row, string name) => db.Get(table, row, table.Fields.Single(f => f.Name == name));
                        var ids = Enumerable.Range(0, cars.Rows).GroupBy(r => Read(cars, r, "CRID")).ToDictionary(g => g.Key, g => g.First());
                        var conflicts = new HashSet<int>();
                        for (int row = 0; row < skins.Rows; row++)
                        {
                            if (!ids.TryGetValue(Read(skins, row, "CRID"), out int car) || !int.TryParse(Read(skins, row, "CTID"), out int texture)
                                || !int.TryParse(Read(cars, car, "CMDL"), out int model)) continue;
                            var body = model switch { 0 => "Chevy", 1 => "Dodge", 2 => "Ford", _ => "" };
                            string name = model switch { 0 or 1 or 2 => "rcar2850.ea3", 3 => "pcar3000.ea3", 4 => "rtruck_LOD1.ea3", 8 => "ModFLite_LOD1.ea3", 10 => "prd_05Viper_LOD1.ea3", _ => "" };
                            if (name.Length == 0) continue;
                            var vehicle = new Vehicle(texture, model, name, body, Read(skins, row, "CNAM"));
                            if (vehicles.TryGetValue(texture, out var prior) && prior.ModelId != model) conflicts.Add(texture);
                            else vehicles[texture] = vehicle;
                        }
                        foreach (int conflict in conflicts) vehicles.Remove(conflict);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or InvalidOperationException or OverflowException) { /* No guessed vehicle identity if a database cannot be read. */ }
            }
            return new(models, textures, vehicles);
        });
    }

    // These are preview slot aliases, not changes to the game material or texture bytes.
    public static string Slot(string texture, bool character)
    {
        string value = texture.ToUpperInvariant();
        if (character)
        {
            if (value.StartsWith("DRIVERFACE")) return "DRIVERFACE";
            if (value.StartsWith("DRIVERHAIR")) return "DRIVERHAIR";
            foreach (string name in new[] { "FACE", "HAIR", "ARM", "PANTS", "TORSO", "HAT", "HELMET", "SHADES", "VISOR", "PEDESTAL", "TIRE" })
                if (value.StartsWith(name + "_") || value == name) return name;
            if (value.StartsWith("DRIVER_ARM")) return "ARM";
        }
        else
        {
            if (value.Contains("WINDOW") || value.StartsWith("FT_AND_LT_BK")) return "DECAL";
            if (value.StartsWith("DRIVER_")) return "DRIVER";
            if (value.StartsWith("FRAME_") || value.StartsWith("TIRE_") || value.StartsWith("WHEEL_")) return "UNIVERSAL";
            if (value.StartsWith("PRODCAR_") || value.StartsWith("UPS_BODY_") || value.StartsWith("PACECAR_")) return "DECAL";
            if (value.Contains("WINDOW") || value.StartsWith("FT_AND_LT_BK") || value.StartsWith("WRW_")) return "DECAL";
            if (new[] { "GR_", "HD_", "LF_", "RF_", "LD_", "LR_", "TK_", "RP_", "RR_", "RD_", "BK_FIN", "FT_FIN", "ROOF_", "SPOILER_", "SIDP_" }.Any(value.StartsWith)) return "DECAL";
        }
        return value;
    }

    public static IReadOnlyList<TexturePack> Nearby(Model model)
    {
        var siblings = model.Node.Parent?.Children;
        if (siblings is null) return [];
        int index = siblings.ToList().IndexOf(model.Node);
        // Character models and their authored face/body/helmet packs form consecutive resource groups.
        return siblings.Skip(index + 1).TakeWhile(n => n.Format is not (FormatId.TiburonModel or FormatId.TiburonSkeleton))
            .Where(n => n.Format == FormatId.MmapTexture).Take(8)
            .Select(n => Get(model.Node).Textures.First(t => ReferenceEquals(t.Node, n))).ToArray();
    }

    public static IReadOnlyList<Model> ModelsFor(AssetNode textureNode)
    {
        var catalog = Get(textureNode); var pack = catalog.Textures.FirstOrDefault(t => ReferenceEquals(t.Node, textureNode));
        if (pack is null) return [];
        bool character = IsCharacter(textureNode);
        var vehicle = VehicleFor(textureNode);
        int Score(Model m) => m.Materials.Count(s => pack.Names.Any(n => Matches(s, n, character)));
        return catalog.Models.Where(m => IsCharacter(m.Node) == character && (Score(m) > 0 || !character && pack.Names.Contains("DECAL") && IsVehicleBody(m)))
            .OrderByDescending(m => vehicle is not null && m.Name.Equals(vehicle.ModelName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => Nearby(m).Any(t => ReferenceEquals(t.Node, textureNode)))
            .ThenByDescending(m => pack.Names.Contains("DECAL") && m.Name.Equals("rcar2850.ea3", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => character && m.Name.Equals("driver_w_shadow.ea3", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(Score).ThenBy(m => m.Node.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static Vehicle? VehicleFor(AssetNode? texture)
    {
        if (texture is null || IsCharacter(texture) || texture.TerfEntry is not { } entry) return null;
        return Get(texture).Vehicles.GetValueOrDefault(entry.Index);
    }
    public static bool IsVehicleBody(Model model) => model.Name.StartsWith("rcar", StringComparison.OrdinalIgnoreCase) && !model.Name.Contains("Unders", StringComparison.OrdinalIgnoreCase)
        || model.Name.StartsWith("pcar", StringComparison.OrdinalIgnoreCase) || model.Name.StartsWith("rtruck_LOD", StringComparison.OrdinalIgnoreCase) && !model.Name.Contains("Unders", StringComparison.OrdinalIgnoreCase)
        || model.Name.StartsWith("ModFLite_LOD", StringComparison.OrdinalIgnoreCase) && !model.Name.Contains("Unders", StringComparison.OrdinalIgnoreCase)
        || model.Name.StartsWith("prd_", StringComparison.OrdinalIgnoreCase) || model.Name == "05UPS3000.ea3";

    public static bool Matches(string materialTexture, string imageName, bool character)
    {
        if (string.Equals(materialTexture, imageName, StringComparison.OrdinalIgnoreCase)) return true;
        string slot = Slot(materialTexture, character);
        string name = imageName.ToUpperInvariant();
        return slot == name || character && ((slot == "FACE" && name == "DRIVERFACE") || (slot == "HAIR" && name == "DRIVERHAIR"));
    }

    private static bool Matches(TiburonModel.Material material, string name, bool character)
    {
        if (Matches(material.Texture, name, character)) return true;
        string semantic = material.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (character && semantic.EndsWith('M')) semantic = semantic[..^1];
        return material.Texture.Length > 0 && Matches(semantic + "_", name, character);
    }

    public static IReadOnlyList<TexturePack> TexturesFor(Model model) => Get(model.Node).Textures
        .Where(t => IsCharacter(t.Node) == IsCharacter(model.Node) && model.Materials.Any(m => t.Names.Any(n => Matches(m, n, IsCharacter(model.Node)))))
        .Where(t => !t.Names.Contains("DECAL") || PreferredModel(t.Node) is { } preferred && SameVehicleFamily(preferred, model.Name))
        .OrderByDescending(t => Nearby(model).Any(n => ReferenceEquals(n.Node, t.Node)))
        .ThenBy(t => t.Node.Key, StringComparer.OrdinalIgnoreCase).ToArray();

    public sealed record Binding(AssetNode? Source, MmapTexture File, MmapTexture.Image Image);
    public static IReadOnlyDictionary<string, Binding> Bind(Model model, AssetNode? selectedTexture = null)
    {
        bool character = IsCharacter(model.Node); var available = new List<(AssetNode? Node, MmapTexture File)>();
        var nodes = Nearby(model).Select(t => t.Node).ToList();
        if (!character)
        {
            foreach (string slot in new[] { "DECAL", "UNIVERSAL", "DRIVER" })
            {
                int universal = model.Name.StartsWith("ModFLite", StringComparison.OrdinalIgnoreCase) ? 529 : model.Name.StartsWith("rtruck", StringComparison.OrdinalIgnoreCase) ? 527 : 513;
                foreach (var family in new (string Name, int Index)[] { ("FordGT", 531), ("Neon", 533), ("Viper", 534), ("Corvette", 535), ("Mustang", 536), ("SVTLightning", 537), ("Magnum", 539), ("DodgeCharger", 540) })
                    if (model.Name.Contains(family.Name, StringComparison.OrdinalIgnoreCase)) { universal = family.Index; break; }
                var pack = slot == "UNIVERSAL" ? Get(model.Node).Textures.FirstOrDefault(t => t.Node.TerfEntry?.Index == universal && NascarAssetCatalog.SourceArchive(t.Node).Name.Equals("CARDATA.DAT", StringComparison.OrdinalIgnoreCase) && t.Names.Contains(slot)) : null;
                pack ??= Get(model.Node).Textures.FirstOrDefault(t => !IsCharacter(t.Node) && t.Names.Contains(slot));
                if (pack is not null) nodes.Add(pack.Node);
            }
        }
        if (selectedTexture is not null) { nodes.Remove(selectedTexture); nodes.Insert(0, selectedTexture); }
        foreach (var node in nodes.Distinct()) available.Add((node, MmapTexture.Read(node.ReadAll())));
        var bytes = model.Node.ReadAll(); var text = TiburonModel.Read(new MemoryByteSource(bytes)).FirstOrDefault(s => s.Tag == "Text");
        if (text is { Length: > 0 }) available.Add((null, MmapTexture.Read(bytes.AsSpan((int)text.Offset, (int)text.Length).ToArray())));
        var result = new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in model.Materials.Where(m => m.Texture.Length > 0))
        {
            // Exact embedded names win; runtime aliases fill otherwise missing material slots.
            var candidates = available.SelectMany(a => a.File.Images.Where(i => i.Mip == 0).Select(i => new Binding(a.Node, a.File, i))).ToArray();
            var match = candidates.FirstOrDefault(b => string.Equals(b.Image.Name, material.Texture, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(b => Matches(material, b.Image.Name, character));
            if (match is not null) result[material.Texture] = match;
        }
        return result;
    }
}
