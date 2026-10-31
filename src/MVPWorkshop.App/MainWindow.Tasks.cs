using System.Windows;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    public void OpenSchoolTask(TeamProfile profile, string task)
    {
        if (new[] { "Uniforms", "Logos", "Stadium", "Roster", "Audio" }.Contains(task))
        {
            OpenTeam(profile);
            if (Docs.SelectedItem is DocumentTab { Content: TeamView view }) view.SelectSection(task);
            return;
        }
        var sources = task == "Menus/UI"
            ? profile.Assets.Where(a => a.Category == "Uniforms/Menu artwork" || a.Category.StartsWith("Logos") || a.Asset.FormatInfo.Category == FormatCategory.Ui)
            : profile.Assets.Where(a => a.Asset.IsTexturePack || a.Asset.IsLooseTexturePack || a.Asset.Extension == ".dds");
        OpenWorkspaceDocument(new DocumentTab { Title = profile.Label + " · " + task, Content = (FrameworkElement)TeamWorkspaceView.Sources(sources.DistinctBy(a => a.Asset), $"{profile.Label} · {task}: confirmed school links. Shared assets affect every school that uses them.", profile) });
    }
}
