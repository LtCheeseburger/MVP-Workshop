using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    public async void OpenUniformPreview(AssetNode asset)
    {
        var session = Editor.Session;
        try
        {
            var profiles = await GetSchoolProfiles();
            if (Editor.Session != session) return;
            var profile = profiles.FirstOrDefault(p => p.Assets.Any(a => a.Asset == asset && a.Category == "Uniforms/Player textures"));
            if (profile is null) { SetStatus("No school assignment found for " + asset.Name); return; }
            OpenSchoolAsset(profile, asset);
        }
        catch (Exception ex) { Log.Error("Uniform preview: " + ex.Message); }
    }

    private Task<List<TeamProfile>>? _schoolIndexTask;
    private MVPWorkshop.Core.Editing.EditSession? _schoolIndexSession;
    private int _schoolIndexChange = -1;

    public Task<List<TeamProfile>> GetSchoolProfiles()
    {
        var session = Editor.Session;
        var change = session?.History.LastOrDefault()?.Id ?? 0;
        if (_schoolIndexTask is null || _schoolIndexSession != session || _schoolIndexChange != change)
        {
            _schoolIndexSession = session;
            _schoolIndexChange = change;
            _schoolIndexTask = Build();
        }
        return _schoolIndexTask;
        async Task<List<TeamProfile>> Build()
        {
            var nodes = await GetAssetIndex();
            if (Editor.Session == session) _searchIndex = nodes;
            return await Task.Run(() => TeamAssetCatalog.Build(nodes));
        }
    }

    private async void RestoreSchoolBrowser(AssetNode game)
    {
        try
        {
            var session = Editor.Session;
            var profiles = await GetSchoolProfiles();
            if (_gameRoot != game || Editor.Session != session || _categoryRoots is null || profiles.Count == 0) return;
            var teamRoot = _categoryRoots.FirstOrDefault(r => r.Name == "Teams");
            if (teamRoot is null) { teamRoot = new TreeItem("Teams", 0, []); _categoryRoots.Insert(0, teamRoot); }
            if (teamRoot.Children.Any(c => c.Team is not null)) return;
            var shared = new TreeItem("All team assets by type", 1, teamRoot.Children.ToList());
            teamRoot.Children.Clear();
            foreach (var profile in profiles)
            {
                var children = profile.Assets.GroupBy(a => a.Category.Split('/')[0]).Select(group =>
                    new TreeItem(group.Key, 2, group.GroupBy(a => a.Category.Contains('/') ? a.Category[(a.Category.IndexOf('/') + 1)..] : "Sources")
                        .Select(sub => new TreeItem(sub.Key, 3, sub.Select(a => new TreeItem(a.Asset, 4) { SchoolScope = profile })))));
                teamRoot.Children.Add(new TreeItem(profile.Label, 1, children) { Team = profile });
            }
            teamRoot.Children.Add(shared);
            teamRoot.IsExpanded = true;
            if (BrowseMode.SelectedIndex == 0) ShowExplorer(_categoryRoots);
        }
        catch (Exception ex) { Log.Warn("School browser: " + ex.Message); }
    }

    private List<TreeItem> BuildPreorganizedCategories(AssetNode game)
    {
        var groups = AssetCatalog.Sources(game);
        List<TreeItem> Build(IEnumerable<KeyValuePair<string, List<AssetNode>>> source, int depth)
        {
            return source.GroupBy(g => g.Key.Split('/')[depth]).Select(group =>
            {
                var children = group.Where(g => g.Key.Split('/').Length == depth + 1)
                    .SelectMany(g => g.Value).Select(n => FileItem(n, depth + 1)).ToList();
                children.AddRange(Build(group.Where(g => g.Key.Split('/').Length > depth + 1), depth + 1));
                return new TreeItem(group.Key, depth, children);
            }).ToList();
        }
        var order = new[] { "Models", "Teams", "Players", "Animations", "UI", "Audio", "Video", "Textures", "Data", "System" };
        return Build(groups, 0).OrderBy(r => Array.IndexOf(order, r.Name) is var rank && rank >= 0 ? rank : int.MaxValue).ToList();
    }
}
