using MVPWorkshop.Core.Formats.Eagl;
using System.Numerics;
using System.Text;

namespace MVPWorkshop.Core.Vfs;

public sealed record StadiumComponent(AssetNode Source, EaglModel Model, System.Numerics.Matrix4x4 Transform = default);

/// <summary>Archive-local scene resources. Alternate field/sky meshes are mutually exclusive.</summary>
public sealed class StadiumScene
{
    public List<StadiumComponent> Components { get; } = [];
    public List<string> Diagnostics { get; } = [];
    public Dictionary<string, string> Configuration { get; } = new(StringComparer.OrdinalIgnoreCase);
    public required AssetNode Archive { get; init; }
    public static StadiumScene Load(AssetNode archive, int field = 0, int sky = 0)
    {
        var scene = new StadiumScene { Archive = archive };
        foreach (var node in archive.Children.Where(n => n.Extension is ".ifo" or ".dat" or ".csv"))
            scene.Configuration[node.Name] = Encoding.ASCII.GetString(node.ReadAll());
        foreach (var node in archive.Children.Where(n => n.Extension == ".ord"))
        {
            var name = Path.GetFileNameWithoutExtension(node.Name).ToLowerInvariant();
            if (name.StartsWith("mfield") && name != $"mfield{field}") continue;
            if (name.StartsWith("sky") && name != $"sky{sky}") continue;
            // These are templates instantiated by runtime placement data, not world meshes.
            if (name is "seat" or "crowdpoly" or "3dflag") continue;
            try
            {
                foreach (var model in EaglReader.Read(node.ReadElfJoined().Data).Models.Where(m => m.Mesh.Indices.Count > 0))
                    scene.Components.Add(new(node, model));
            }
            catch (Exception ex) { scene.Diagnostics.Add($"{node.Path}: {ex.Message}"); }
        }
        // Xbox archives build the crowd at runtime from crowd.dat; their crowd.ord carries no indexed mesh.
        var crowdFromData = archive.Children.Any(n => n.Name.Equals("crowd.dat", StringComparison.OrdinalIgnoreCase));
        foreach (var required in new[] { "mstadium.ord", $"mfield{field}.ord", $"sky{sky}.ord" }.Concat(crowdFromData ? [] : ["crowd.ord"]))
            if (!scene.Components.Any(c => c.Source.Name.Equals(required, StringComparison.OrdinalIgnoreCase))) scene.Diagnostics.Add("Missing geometry: " + required);
        // flags.csv: variant, scale XYZ, translation XYZ, animation phase.
        if (scene.Configuration.TryGetValue("flags.csv", out var flags) && archive.Children.FirstOrDefault(n => n.Name == "3dflag.ord") is { } flag)
        {
            var models = EaglReader.Read(flag.ReadElfJoined().Data).Models;
            foreach (var row in flags.Split('\n').Skip(1).Where(l => l.Trim().Length > 0))
            {
                var cells = row.Trim().Split(',');
                var values = new float[cells.Length];
                if (cells.Where((v, i) => !float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[i])).Any())
                { scene.Diagnostics.Add("Invalid flags.csv number: " + row); continue; }
                if (values.Length != 8) { scene.Diagnostics.Add("Invalid flags.csv placement: " + row); continue; }
                if(values.Any(v=>!float.IsFinite(v))) { scene.Diagnostics.Add("Non-finite flag placement"); continue; }
                if (models.Count == 0) { scene.Diagnostics.Add("Missing flag geometry"); continue; }
                scene.Components.Add(new(flag, models[0], System.Numerics.Matrix4x4.CreateScale(values[1],values[2],values[3]) * System.Numerics.Matrix4x4.CreateTranslation(values[4],values[5],values[6])));
            }
        }
        if(archive.Name.StartsWith("VENU",StringComparison.OrdinalIgnoreCase))
            scene.Diagnostics.Add("Customizable venue: runtime field/wall construction and shared CSASSETS selection are unavailable. Authored placeholder triangles are retained; missing field sections are not borrowed from another stadium.");
        // instanced components (one flag per flags.csv row) share a model, so report each warning once with an instance count
        foreach(var group in scene.Components.SelectMany(c=>c.Model.Mesh.Warnings.Select(w=>c.Source.Name+": "+w)).GroupBy(w=>w))
            scene.Diagnostics.Add(group.Count()>1 ? $"{group.Key} (×{group.Count()})" : group.Key);
        return scene;
    }
}



