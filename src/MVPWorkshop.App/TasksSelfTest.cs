using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunTasksCheck(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        try
        {
            var start = _docs.Single(d => !d.CanClose);
            var view = (TaskStartView)start.Content;
            for (int i = 0; i < 200 && view.Profiles.Count < 2; i++) await Task.Delay(30);
            Check("loose installation shows task-first Start", view.Profiles.Count >= 2);
            var schoolTree = Tree.Items.OfType<TreeItem>().FirstOrDefault(t => t.Name == "Teams");
            Check("explorer restores expanded school-by-school browser", schoolTree is { IsExpanded: true } && schoolTree.Children.Count(c => c.Team is not null) == view.Profiles.Count);
            Check("Start and explorer share indexed school profiles", schoolTree is not null && view.Profiles.All(p => schoolTree.Children.Any(c => ReferenceEquals(c.Team, p))));
            Check("school tree retains uniform source groups", schoolTree?.Children.FirstOrDefault(c => c.Team?.Team.UniformId == 0)?.Children.Any(c => c.Name == "Uniforms") == true);
            var picker = ((StackPanel)view.Content).Children.OfType<ComboBox>().Single();
            UpdateLayout();
            IEnumerable<TextBlock> Labels(DependencyObject parent)
            {
                if (parent is TextBlock text) yield return text;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var childLabel in Labels(VisualTreeHelper.GetChild(parent, i))) yield return childLabel;
            }
            Check("picker displays the selected school", view.SelectedSchool is { } selected && Labels(picker).Any(t => t.Text == selected.Label));
            var first = view.Profiles[0]; var second = view.Profiles[1];
            var uniformAsset = first.Assets.First(a => a.Category == "Uniforms/Player textures" && a.Asset.Name.StartsWith('u')).Asset;
            OpenAsset(uniformAsset);
            Check("texture editor exposes dressed player shortcut", Docs.SelectedItem is DocumentTab textureTab && Labels((DependencyObject)textureTab.Content).Any(t => t.Text == "Dressed player"));
            OpenSchoolAsset(first, uniformAsset);
            Check("opening school uniform selects player workspace and variant", Docs.SelectedItem is DocumentTab { Content: TeamView uniformTeam } && uniformTeam.SelectedSectionName.StartsWith("Uniforms") && uniformTeam.UniformState.Variant == uniformAsset.Name[4]);
            view.SelectedSchool = second;
            foreach (var task in new[] { "Uniforms", "Logos", "Stadium", "Roster", "Audio" })
            {
                OpenSchoolTask(second, task);
                Check(task + " opens selected school's editor section", Docs.SelectedItem is DocumentTab { Content: TeamView team } && team.SelectedSectionName.StartsWith(task) && ((DocumentTab)Docs.SelectedItem).Title == second.Label);
            }
            var schoolTabs = _docs.Count(d => d.Content is TeamView);
            OpenSchoolTask(new MVPWorkshop.Core.Vfs.TeamProfile { Team = second.Team, Roster = second.Roster }, "Roster");
            Check("same school reuses its workspace", _docs.Count(d => d.Content is TeamView) == schoolTabs);
            foreach (var task in new[] { "Menus/UI", "Textures" })
            {
                OpenSchoolTask(second, task);
                Check(task + " opens school-scoped sources", Docs.SelectedItem is DocumentTab tab && tab.Title == second.Label + " · " + task);
            }
            Docs.SelectedItem = start;
            await Task.Delay(300);
            view.SelectedSchool = view.Profiles.First(p => p.Team.RosterIndex == second.Team.RosterIndex);
            var session = Editor.Session!;
            var bytes = first.Roster.ReadAll().ToArray(); bytes[first.Team.Offset + 112] ^= 1;
            session.Commit(first.Roster, bytes, "Edit first school's nickname");
            Check("recent edits match the modified school's roster record", SchoolRecentEdits.ForSchool(first, session).Count == 1);
            Check("other school's shared roster file is excluded from recent edits", SchoolRecentEdits.ForSchool(second, session).Count == 0);
            await Task.Delay(500);
            Check("refresh preserves selected school", view.SelectedSchool?.Team.RosterIndex == second.Team.RosterIndex);
            session.Undo();
            Check("undo removes recent school edit", SchoolRecentEdits.ForSchool(first, session).Count == 0);
            view.SelectedSchool = view.Profiles.First();
            await Task.Delay(300); UpdateLayout();
            var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(reportPath + ".png"); png.Save(stream);
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        finally { Editor.Session?.MarkSaved(); File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown(); }
    }
}
