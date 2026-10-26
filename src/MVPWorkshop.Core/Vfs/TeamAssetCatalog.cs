using System.Text.RegularExpressions;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.Core.Vfs;

public sealed record TeamAssetLink(string Category, AssetNode Asset, string Evidence, string Status = "Confirmed");

public sealed class TeamProfile
{
    public required GameTeam Team { get; set; }
    public required AssetNode Roster { get; init; }
    public string Label => Team.Abbreviation == Team.Name ? Team.Name : $"{Team.Abbreviation} · {Team.Name}";
    public List<TeamAssetLink> Assets { get; } = [];
    public List<TeamPlayer> Players { get; } = [];
    public IReadOnlyList<TeamAssetLink> SharedAssets { get; set; } = [];
    public IReadOnlyList<string> WorkspaceProblems { get; set; } = [];
    public string? StadiumCode { get; set; }
    public string? StadiumName { get; set; }
    public string? RegisteredVenue { get; set; }
    public string StadiumEvidence { get; set; } = "Runtime home-stadium assignment has not been decoded for this school.";
}

public static class TeamAssetCatalog
{
    public static List<TeamProfile> Build(IReadOnlyList<AssetNode> nodes, byte[]? rosterBytes = null, CancellationToken cancellation = default)
    {
        var roster = nodes.FirstOrDefault(n => n.Name.Equals("roster.bin", StringComparison.OrdinalIgnoreCase) && n.Path.Contains("DATABASE.BIG/", StringComparison.OrdinalIgnoreCase));
        if (roster is null) return [];
        var executable = GameExecutable.Find(nodes);
        StadiumAssignments? stadiumMap = null;
        var stadiumError = "Game executable not found; stadium mapping unavailable.";
        if (executable is not null)
            try { stadiumMap = StadiumAssignments.Read(executable.ReadAll()); }
            catch (Exception ex) { stadiumError = "Stadium mapping: " + ex.Message; }
        var localized = new Dictionary<int, string>();
        if (nodes.FirstOrDefault(n => n.Name.Equals("FEENG.LOC", StringComparison.OrdinalIgnoreCase)) is { } loc)
            foreach (var text in LocTable.Read(loc.ReadAll()).Strings) localized[text.Id] = text.Text;
        string Name(StadiumDefinition definition) => localized.GetValueOrDefault(definition.FullNameId, localized.GetValueOrDefault(definition.NameId, $"Stadium {definition.Index}"));
        var uniformAssets = new Dictionary<int, List<(string Category, AssetNode Node)>>();
        var logoAssets = new Dictionary<int, List<AssetNode>>();
        var stadiumAssets = new Dictionary<string, List<AssetNode>>(StringComparer.OrdinalIgnoreCase);
        var native = new Regex("^[uf](\\d{3})[a-z]\\.[sx]sh$", RegexOptions.IgnoreCase);
        var menu = new Regex("^(\\d{3})[a-z]?\\.[sx]sh$", RegexOptions.IgnoreCase);
        var logo = new Regex("^l(\\d{3})\\.[sx]sh$", RegexOptions.IgnoreCase);
        foreach (var node in nodes.Where(n => !n.IsFolder && n.Extension != ".big"))
        {
            cancellation.ThrowIfCancellationRequested();
            var path = node.Path;
            var match = path.Contains("/MODELS.BIG/", StringComparison.OrdinalIgnoreCase) ? native.Match(node.Name) :
                path.Contains("/UNIFORMS.BIG/", StringComparison.OrdinalIgnoreCase) ? menu.Match(node.Name) : Match.Empty;
            if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
            {
                if (!uniformAssets.TryGetValue(id, out var assets)) uniformAssets[id] = assets = [];
                assets.Add((path.Contains("/MODELS.BIG/", StringComparison.OrdinalIgnoreCase) ? "Uniforms/Player textures" : "Uniforms/Menu artwork", node));
            }
            if (path.Contains("/LOGOS.BIG/", StringComparison.OrdinalIgnoreCase) && (match = logo.Match(node.Name)).Success && int.TryParse(match.Groups[1].Value, out id))
            {
                if (!logoAssets.TryGetValue(id, out var assets)) logoAssets[id] = assets = [];
                assets.Add(node);
            }
            var stadiumAt = path.IndexOf("/STADIUM/", StringComparison.OrdinalIgnoreCase);
            if (stadiumAt >= 0)
            {
                var container = path[(stadiumAt + 9)..].Split('/')[0];
                if (container.EndsWith(".big", StringComparison.OrdinalIgnoreCase))
                {
                    var code = System.IO.Path.GetFileNameWithoutExtension(container);
                    if (!stadiumAssets.TryGetValue(code, out var assets)) stadiumAssets[code] = assets = [];
                    assets.Add(node);
                }
            }
        }
        var profiles = new List<TeamProfile>();
        var workspace = new TeamWorkspaceSources(nodes);
        var currentRoster = rosterBytes ?? roster.ReadAll();
        foreach (var team in TeamRoster.Read(currentRoster))
        {
            cancellation.ThrowIfCancellationRequested();
            var profile = new TeamProfile { Team = team, Roster = roster };
            workspace.Populate(profile, currentRoster);
            foreach (var (category, node) in uniformAssets.GetValueOrDefault(team.UniformId, []))
                profile.Assets.Add(new(category, node, $"roster.bin @0x{team.Offset + 164:X}: 9-bit asset slot = {team.UniformId}; executable format u%03d%c / f%03d%c"));
            foreach (var node in logoAssets.GetValueOrDefault(team.LogoId, []))
                profile.Assets.Add(new("Logos", node, $"roster.bin @0x{team.Offset + 164:X}, bits 1–9: logo slot {team.LogoId}; LOGOS.BIG/l{team.LogoId:D3}.ssh"));
            profile.StadiumEvidence = stadiumError;
            void AddStadium(int stadiumId, string prefix, string evidence)
            {
                if (stadiumMap is null) return;
                var resolutions = Enumerable.Range(0, 3).Select(time => stadiumMap.Resolve(stadiumId, time)).OfType<StadiumResolution>().ToList();
                foreach (var resolved in resolutions.DistinctBy(r => r.Code))
                {
                    if (!stadiumAssets.TryGetValue(resolved.Code, out var assets)) continue;
                    var proof = evidence + $"; executable map @0x{resolved.MapOffset:X} → native {resolved.NativeIndex} ({resolved.Code}); " + string.Join("/", resolutions.Where(r => r.Code == resolved.Code).Select(r => r.TimeOfDay)) + (resolved.UsedFallback ? " (time-of-day fallback)" : "");
                    foreach (var node in assets)
                        profile.Assets.Add(new("Stadium/" + prefix + (node.IsTexturePack ? "textures" : node.Extension is ".ord" or ".orl" ? "models" : "configuration"), node, proof));
                    foreach (var config in nodes.Where(n => n.Name.Equals(resolved.Code + ".txt", StringComparison.OrdinalIgnoreCase) && n.Path.Contains("/DATAFILE.BIG/", StringComparison.OrdinalIgnoreCase)))
                        profile.Assets.Add(new("Stadium/" + prefix + "configuration", config, proof));
                }
                var picturePrefix = $"s{stadiumId + 1:x2}";
                foreach (var image in nodes.Where(n => n.IsTexturePack && n.Name.StartsWith(picturePrefix, StringComparison.OrdinalIgnoreCase) && n.Path.Contains("/STADIUMS.BIG/", StringComparison.OrdinalIgnoreCase)))
                    profile.Assets.Add(new("Stadium/" + prefix + "menu panoramas", image, evidence + "; menu panorama enum + 1 (hexadecimal)"));
            }
            if (stadiumMap is not null)
            {
                var definition = stadiumMap.Definitions.FirstOrDefault(d => d.Index == team.StadiumId);
                var resolution = stadiumMap.Resolve(team.StadiumId);
                profile.StadiumName = definition is not null ? Name(definition) : $"Stadium {team.StadiumId}";
                profile.StadiumCode = resolution?.Code;
                profile.StadiumEvidence = $"Base roster stadium: {profile.StadiumName} (enum {team.StadiumId}) → {profile.StadiumCode ?? "unresolved"}. roster.bin @0x{team.Offset + 168:X}, bits 4–9; {executable?.Name} time-of-day map @0x{stadiumMap.MapTableOffset:X}.";
                AddStadium(team.StadiumId, "Assigned ", profile.StadiumEvidence);
                var registered = stadiumMap.Definitions.FirstOrDefault(d => d.SchoolAssetId == team.LogoId);
                if (registered is not null)
                {
                    profile.RegisteredVenue = Name(registered);
                    if (registered.Index != team.StadiumId)
                    {
                        var proof = $"Authentic venue registry: school asset {team.LogoId} → {Name(registered)} (enum {registered.Index}) at {executable?.Name} @0x{registered.FileOffset:X}. Distinct from the base roster stadium enum {team.StadiumId}.";
                        AddStadium(registered.Index, "Registered authentic ", proof);
                        profile.StadiumEvidence += $" Authentic venue registry also links {Name(registered)} (enum {registered.Index}); the base roster assignment is preserved.";
                    }
                }
            }
            profiles.Add(profile);
        }
        return profiles.OrderBy(p => p.Team.Abbreviation, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Team.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}


