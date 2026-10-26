using System.IO;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

/// <summary>Embeds the existing native texture editor with explicit APT/import ownership.</summary>
public sealed class AptTextureEditorView : Grid
{
    private sealed record Pack(AssetNode Movie, AssetNode Texture)
    {
        public override string ToString() => $"{Movie.Name} → {Texture.Path}";
    }
    private readonly AssetNode _movie;
    private readonly Action<IReadOnlyList<AssetNode>> _watch;
    private readonly ComboBox _packs = new() { Name = "AptTextureSources", MinWidth = 320, MaxWidth = 950 };
    private readonly ContentControl _editor = new();
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 10, 6) };
    private readonly Dictionary<string, int> _selection = [];
    private string? _packKey;
    private bool _loading;
    private TextureView? _texture;
    private string _discoverySignature = "";
    private List<AssetNode> _dependencies = [];
    internal TextureView? TextureEditor => _texture;
    internal AssetNode? TextureSource => (_packs.SelectedItem as Pack)?.Texture;
    internal int PackCount => _packs.Items.Count;

    public AptTextureEditorView(AssetNode movie, Action<IReadOnlyList<AssetNode>> watch)
    {
        _movie = movie; _watch = watch;
        _packs.SelectionChanged += (_, _) => ShowPack();
        Children.Add(B.Rows((B.Toolbar(B.Text("Texture source"), _packs,
            B.Button("Open source", null, (_, _) => { if (TextureSource is { } node) MainWindow.Instance?.OpenAsset(node); }, "Outline")), B.Auto),
            (_notes, B.Auto), (_editor, B.Star())));
        Loaded += async (_, _) => await Discover();
    }

    private string Signature() => string.Join("|", _dependencies.Where(n => n.Extension is ".apt" or ".const")
        .Select(n => n.Key + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(n.ReadAll()))));

    private async Task Discover()
    {
        if (_loading || (_dependencies.Count > 0 && Signature() == _discoverySignature)) return;
        _loading = true;
        try
        {
            var root = _movie; while (root.Parent is not null) root = root.Parent;
            var nodes = MainWindow.Instance is { } window ? await window.GetAssetIndex() : await Task.Run(() => root.EnumerateDeep().ToList());
            var packs = new List<Pack>(); var visited = new HashSet<AssetNode>(); var dependencies = new HashSet<AssetNode>(); var notes = new List<string>();
            void Visit(AssetNode movie, int depth)
            {
                if (!visited.Add(movie)) return;
                if (depth > 32) { notes.Add("Import depth limit: " + movie.Path); return; }
                foreach (var asset in new[] { movie, AptView.Sibling(movie, ".const"), AptView.Sibling(movie, ".o"), AptView.Sibling(movie, ".ssh") })
                    if (asset is not null) dependencies.Add(asset);
                if (AptView.Sibling(movie, ".ssh") is { } texture && !packs.Any(p => p.Texture == texture)) packs.Add(new(movie, texture));
                try
                {
                    var constants = AptView.Sibling(movie, ".const") is { } c ? AptConstants.Read(c.ReadAll()) : null;
                    var apt = AptFile.Read(movie.ReadAll(), constants?.MovieOffset ?? 0);
                    foreach (var import in apt.Imports.DistinctBy(i => i.Movie))
                    {
                        var name = Path.ChangeExtension(import.Movie.Replace('\\', '/').Split('/').Last(), ".apt");
                        var candidates = nodes.Where(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n.Parent == movie.Parent).ToList();
                        if (candidates.Count == 0) notes.Add($"Missing import: {movie.Name} → {import.Movie}");
                        else if (candidates.Count > 1 && candidates[0].Parent != movie.Parent) notes.Add($"Ambiguous import: {movie.Name} → {import.Movie}; no pack selected");
                        else Visit(candidates[0], depth + 1);
                    }
                }
                catch (Exception ex) { notes.Add(movie.Path + ": " + ex.Message); }
            }
            Visit(_movie, 0);
            _dependencies = dependencies.ToList(); _discoverySignature = Signature(); _watch(_dependencies);
            _packs.ItemsSource = packs;
            _packs.SelectedItem = packs.FirstOrDefault(p => p.Texture.Key == _packKey) ?? packs.FirstOrDefault();
            if (packs.Count == 0) { _editor.Content = null; _texture = null; }
            _notes.Text = (packs.Count == 0 ? "No paired SSH texture pack was found for this movie or its resolved imports."
                : "Replace image… uses the existing native texture importer. Edits use project history, undo/redo and Export Mod. Imported packs are shared: replacing an image changes every use of that source. Unsupported replacements remain local to the texture preview.")
                + (notes.Count == 0 ? "" : "\n" + string.Join("\n", notes));
        }
        catch (Exception ex) { _notes.Text = "Texture discovery failed: " + ex.Message; }
        finally { _loading = false; }
    }
    private void ShowPack()
    {
        if (_packs.SelectedItem is not Pack pack) return;
        _packKey = pack.Texture.Key;
        _packs.ToolTip = $"Movie: {pack.Movie.Path}\nTexture pack: {pack.Texture.Path}";
        try
        {
            _texture = new TextureView(SshFile.Read(pack.Texture.ReadAll()), pack.Texture, _ => { });
            _texture.SelectedImageIndex = _selection.GetValueOrDefault(_packKey);
            var key = _packKey;
            _texture.ImageSelected = index => _selection[key] = index;
            _editor.Content = _texture;
        }
        catch (Exception ex) { _texture = null; _editor.Content = B.Text("Cannot edit " + pack.Texture.Path + ": " + ex.Message, "Muted"); }
    }
}
