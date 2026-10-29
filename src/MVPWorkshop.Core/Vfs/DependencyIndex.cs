using System.Text;
using System.Text.RegularExpressions;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Formats.Eagl;

namespace MVPWorkshop.Core.Vfs;

public enum RelationshipKind { ExplicitReference, DecodedAssignment, HeuristicCandidate }
public sealed record DependencySubject(string Id, string Label, AssetNode Source, string Location);
public sealed record AssetRelationship(DependencySubject From, DependencySubject? To, string Target,
    RelationshipKind Kind, string Evidence, string SourceLocation, bool Missing = false);

/// <summary>Immutable snapshot. Candidates never participate in confirmed impact traversal.</summary>
public sealed class DependencyIndex
{
    public IReadOnlyList<DependencySubject> Subjects { get; }
    public IReadOnlyList<AssetRelationship> Relationships { get; }
    public IReadOnlyList<string> Problems { get; }
    private readonly ILookup<string, AssetRelationship> _out, _in;
    private DependencyIndex(List<DependencySubject> subjects, List<AssetRelationship> edges, List<string> problems)
    {
        Subjects = subjects.AsReadOnly(); Relationships = edges.AsReadOnly(); Problems = problems.AsReadOnly();
        _out = edges.ToLookup(e => e.From.Id); _in = edges.Where(e => e.To != null).ToLookup(e => e.To!.Id);
    }
    public IEnumerable<AssetRelationship> DependsOn(string id) => _out[id];
    public IEnumerable<AssetRelationship> UsedBy(string id) => _in[id];
    public IReadOnlyList<DependencySubject> Impact(string id)
    {
        var seen = new HashSet<string> { id }; var queue = new Queue<string>(); queue.Enqueue(id);
        var result = new List<DependencySubject>();
        while (queue.TryDequeue(out var next))
            foreach (var edge in UsedBy(next).Where(e => e.Kind != RelationshipKind.HeuristicCandidate))
                if (seen.Add(edge.From.Id)) { result.Add(edge.From); queue.Enqueue(edge.From.Id); }
        return result;
    }

    public static Task<DependencyIndex> BuildAsync(AssetNode root, CancellationToken cancellation = default,
        IProgress<string>? progress = null) => Task.Run(() => Build(root, cancellation, progress), cancellation);

    private static DependencyIndex Build(AssetNode root, CancellationToken token, IProgress<string>? progress)
    {
        var lastReport = System.Diagnostics.Stopwatch.StartNew();
        void Report(string message)
        {
            if (lastReport.ElapsedMilliseconds < 100) return;
            lastReport.Restart(); progress?.Report(message);
        }
        var nodes = new List<AssetNode>();
        foreach (var n in root.EnumerateDeep()) { token.ThrowIfCancellationRequested(); Report("Discovering " + n.Path); if (!n.IsFolder) nodes.Add(n); }
        var subjects = nodes.Select(n => new DependencySubject("asset:" + n.Key, n.Path, n, n.Location)).ToList();
        var byNode = subjects.ToDictionary(s => s.Source); var edges = new List<AssetRelationship>(); var problems = new List<string>();
        var names = nodes.ToLookup(n => n.Name, StringComparer.OrdinalIgnoreCase);
        void Link(DependencySubject from, DependencySubject? to, string target, RelationshipKind kind, string evidence, string at, bool missing = false)
            => edges.Add(new(from, to, target, kind, evidence, at, missing));
        // Xbox builds name their texture packs .xsh; prefer whichever sibling actually exists.
        static string TextureExt(AssetNode from, string name) =>
            from.Parent?.Children.Any(c => c.Name.Equals(Path.ChangeExtension(name, ".xsh"), StringComparison.OrdinalIgnoreCase)) == true ? ".xsh" : ".ssh";
        static string FontExt(AssetNode from, string name) =>
            from.Parent?.Children.Any(c => c.Name.Equals(Path.ChangeExtension(name, ".xfn"), StringComparison.OrdinalIgnoreCase)) == true ? ".xfn" : ".sfn";
        void Reference(AssetNode from, string target, RelationshipKind kind, string evidence, string at)
        {
            target = target.Replace('\\', '/');
            // Resolve only inside the source directory. A global basename match is never proof.
            var parts = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var scope = from.Parent; IEnumerable<AssetNode> matches = [];
            if (scope != null && !parts.Contains(".."))
            {
                IEnumerable<AssetNode> current = [scope];
                foreach (var part in parts) current = current.SelectMany(n => n.Children).Where(n => n.Name.Equals(part, StringComparison.OrdinalIgnoreCase)).ToList();
                matches = current.Where(byNode.ContainsKey).ToList();
            }
            var resolved = matches.ToList();
            if (resolved.Count == 1) Link(byNode[from], byNode[resolved[0]], target, kind, evidence, at);
            else
            {
                if (kind != RelationshipKind.HeuristicCandidate)
                    Link(byNode[from], null, target, kind, evidence + (resolved.Count > 1 ? "; ambiguous local reference" : "; absent in source directory; runtime search scope unknown"), at, resolved.Count == 0 && !names[parts.LastOrDefault() ?? ""].Any());
                foreach (var candidate in names[parts.LastOrDefault() ?? ""].Where(n => n != from))
                    Link(byNode[from], byNode[candidate], target, RelationshipKind.HeuristicCandidate, evidence + "; filename candidate only; scope/runtime resolution unverified", at);
            }
        }
        var textureTags = new Dictionary<AssetNode, HashSet<string>>();
        var localizedIds = new Dictionary<AssetNode, HashSet<int>>();
        foreach (var node in nodes.Where(n => n.Extension == ".loc" && n.Format == FormatId.LocBinary))
        {
            token.ThrowIfCancellationRequested();
            try { localizedIds[node] = LocTable.Read(node.ReadAll()).Strings.Select(s => s.Id).ToHashSet(); }
            catch (Exception ex) { problems.Add(node.Path + ": " + ex.Message); }
        }
        foreach (var node in nodes.Where(n => n.Format == FormatId.SshTexture))
        {
            token.ThrowIfCancellationRequested();
            Report("Indexing texture tags: " + node.Path);
            try { textureTags[node] = SshFile.Read(node.ReadAll()).Images.Select(i => i.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase); }
            catch (Exception ex) { problems.Add(node.Path + ": " + ex.Message); }
        }
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested(); Report("Reading " + node.Path);
            try
            {
                if (node.LoadError != null) problems.Add(node.Path + ": " + node.LoadError);
                if (node.Extension == ".fel")
                {
                    var layout = FelFile.Read(FormatInspector.ReadText(node.Open()));
                    foreach (var element in layout.Elements)
                    {
                        var at = $"{node.Path}:line {element.Line} ({element.Kind})";
                        if (element.Kind == "SF" && element.Field(1).Length > 0)
                            Reference(node, element.Field(1), RelationshipKind.ExplicitReference, "FEL SF include field 1", at);
                        if (element.Kind == "LS" && element.Name.Length > 0)
                            Reference(node, Path.ChangeExtension(element.Name, TextureExt(node, element.Name)), RelationshipKind.ExplicitReference, "FEL LS texture pack declaration", at);
                        if (element.Kind == "TB")
                        {
                            if (element.Field(13).Length > 0)
                                Reference(node, Path.ChangeExtension(element.Field(13), FontExt(node, element.Field(13))), RelationshipKind.ExplicitReference, "FEL TB font field 13", at);
                            if (int.TryParse(element.Field(14), out var id) && id >= 0)
                                foreach (var (loc, ids) in localizedIds)
                                {
                                    token.ThrowIfCancellationRequested();
                                    // A decoded string ID does not prove the runtime language-table selection.
                                    if (ids.Contains(id))
                                        Link(byNode[node], byNode[loc], $"string ID {id}", RelationshipKind.HeuristicCandidate, "FEL TB field 14 matches localization ID; language/table selection unverified", at);
                                }
                        }
                    }
                }
                if (node.Extension == ".ord")
                {
                    var joined = node.ReadElfJoined();
                    if (joined.Partner != null && byNode.ContainsKey(joined.Partner))
                        Link(byNode[node], byNode[joined.Partner], joined.Partner.Path, RelationshipKind.DecodedAssignment, "Split ELF requires link partner; joined by the object reader", "ELF split header");
                    var obj = RelocatedObject.Load(joined.Data);
                    foreach (var (offset, reloc) in obj.Relocs.Where(r => r.Value.External?.Contains("SHAPENAME=", StringComparison.Ordinal) == true))
                    {
                        var tag = EaglReader.TextureName(reloc.External!);
                        foreach (var pack in textureTags.Where(p => p.Key.Parent == node.Parent && p.Value.Contains(tag)))
                            Link(byNode[node], byNode[pack.Key], pack.Key.Path, RelationshipKind.HeuristicCandidate,
                                $"External relocation '{reloc.External}' matches SSH tag '{tag}' in the same directory; runtime texture binding unverified", $"ELF .data relocation @0x{offset:X}");
                    }
                }
                if (node.Format == FormatId.AptMovie)
                {
                    var constants = node.Parent?.Children.SingleOrDefault(n => n.Name.Equals(Path.ChangeExtension(node.Name, ".const"), StringComparison.OrdinalIgnoreCase));
                    var movieOffset = constants == null ? 0 : AptConstants.Read(constants.ReadAll()).MovieOffset;
                    var movie = AptFile.Read(node.ReadAll(), movieOffset);
                    var importsAt = MVPWorkshop.Core.IO.Bin.U32LE(movie.Data, movie.Movie.Offset + 0x2C);
                    foreach (var import in movie.Imports)
                        Reference(node, Path.HasExtension(import.Movie) ? import.Movie : import.Movie + ".apt", RelationshipKind.ExplicitReference,
                            $"APT import movie '{import.Movie}', symbol '{import.Name}', character {import.Character}", $"APT import record {movie.Imports.IndexOf(import)} @0x{importsAt + movie.Imports.IndexOf(import) * 16:X}");
                    foreach (var ext in new[] { ".const", TextureExt(node, node.Name) })
                        Reference(node, Path.ChangeExtension(node.Name, ext), RelationshipKind.HeuristicCandidate, "APT companion resource convention", "APT resource loader");
                    foreach (var character in movie.Characters.OfType<AptCharacter>().Where(c => !string.IsNullOrEmpty(c.FontName)))
                        Reference(node, character.FontName!, RelationshipKind.HeuristicCandidate, "Decoded APT font name; external font selection unverified", $"character {character.Id} @0x{character.Offset:X}");
                }
                if (node.Extension == ".hdr" && SpeechPairs.StreamFor(node) is { } stream && byNode.TryGetValue(stream, out var streamSubject))
                    Link(byNode[node], streamSubject, stream.Path, RelationshipKind.DecodedAssignment, "Speech HDR/DAT paired pack and record identity", node.Location);
                if (node.Format == FormatId.OffsetPack && node.OffsetIndexNode() is { } off && byNode.ContainsKey(off))
                    Link(byNode[node], byNode[off], off.Path, RelationshipKind.DecodedAssignment, "OFF records define stream byte ranges", "OFF index");
                // Embedded paths are useful discovery evidence, never explicit references without a decoded field.
                if (!node.IsContainer && node.StoredSize <= 8 * 1024 * 1024 &&
                    (node.Format is FormatId.Text or FormatId.LocText or FormatId.Csv or FormatId.AptConst || node.Extension is ".fel" or ".ord" or ".orl"))
                {
                    var text = Encoding.Latin1.GetString(node.ReadAll());
                    foreach (Match match in Regex.Matches(text, @"[A-Za-z0-9_./\\-]+\.(?:ssh|apt|fel|sfn|ffn|loc|ord|orl|asf|wav|bnk|big)\b", RegexOptions.IgnoreCase))
                    {
                        token.ThrowIfCancellationRequested();
                        Reference(node, match.Value, RelationshipKind.HeuristicCandidate, "Embedded filename string; semantics not decoded", $"logical bytes @0x{match.Index:X}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add(node.Path + ": " + ex.Message); }
        }
        token.ThrowIfCancellationRequested(); progress?.Report("Joining team and player assignments…");
        try
        {
            foreach (var profile in TeamAssetCatalog.Build(nodes, cancellation: token))
            {
                token.ThrowIfCancellationRequested();
                var team = new DependencySubject($"team:{profile.Roster.Key}:{profile.Team.RosterIndex}", "Team · " + profile.Label, profile.Roster, $"record @0x{profile.Team.Offset:X}"); subjects.Add(team);
                Link(team, byNode[profile.Roster], profile.Roster.Path, RelationshipKind.DecodedAssignment, "Team assignment fields are stored in this roster record", team.Location);
                foreach (var asset in profile.Assets.Where(a => a.Asset != profile.Roster))
                    Link(team, byNode[asset.Asset], asset.Asset.Path,
                        asset.Status == "Confirmed" ? RelationshipKind.DecodedAssignment : RelationshipKind.HeuristicCandidate,
                        asset.Evidence, profile.Roster.Path + " · " + team.Location);
                if (!profile.Assets.Any(a => a.Category.StartsWith("Uniforms/", StringComparison.Ordinal)))
                    Link(team, null, $"uniform family {profile.Team.UniformId:D3}", RelationshipKind.DecodedAssignment,
                        "Decoded uniform slot has no native or menu family assets", $"{profile.Roster.Path} @0x{profile.Team.Offset + 164:X}, {profile.Team.UniformBits}", true);
                if (!profile.Assets.Any(a => a.Category == "Logos"))
                    Link(team, null, $"LOGOS.BIG/l{profile.Team.LogoId:D3}.ssh", RelationshipKind.DecodedAssignment,
                        "Decoded logo slot has no logo asset", $"{profile.Roster.Path} @0x{profile.Team.Offset + 164:X}, {profile.Team.LogoBits}", true);
                if (profile.StadiumCode != null && !profile.Assets.Any(a => a.Category.StartsWith("Stadium/Assigned ", StringComparison.Ordinal)))
                    Link(team, null, profile.StadiumCode, RelationshipKind.DecodedAssignment, profile.StadiumEvidence,
                        $"{profile.Roster.Path} @0x{profile.Team.Offset + 168:X}, bits 4–9", true);
                foreach (var player in profile.Players)
                {
                    var subject = new DependencySubject($"player:{profile.Roster.Key}:{profile.Team.RosterIndex}:{player.Slot}", $"Player · {player.Name} · {profile.Label} slot {player.Slot}", profile.Roster, $"roster @0x{player.Offset:X}"); subjects.Add(subject);
                    Link(team, subject, player.Key, RelationshipKind.DecodedAssignment, player.Evidence, subject.Location);
                    Link(subject, byNode[profile.Roster], profile.Roster.Path, RelationshipKind.DecodedAssignment, "Player slot key is stored in this roster record", subject.Location);
                    foreach (var source in player.Sources)
                        Link(subject, byNode[source.Asset], player.Key, RelationshipKind.DecodedAssignment, $"LE32 player key {player.Key} joins hexadecimal DAT key", source.Asset.Path + ":line " + source.Record.Line);
                    if (player.Sources.Count == 0) Link(subject, null, player.Key, RelationshipKind.DecodedAssignment, "No DAT row matches roster player key", subject.Location, true);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add("Team assignments: " + ex.Message); }
        token.ThrowIfCancellationRequested();
        return new(subjects, edges.Distinct().ToList(), problems);
    }
}
