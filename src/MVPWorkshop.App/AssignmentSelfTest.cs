using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App;

/// <summary>Opt-in UI regression: --assignment-selftest report.txt. Leaves game files untouched.</summary>
public static class AssignmentSelfTest
{
    public static async void Run(MainWindow window, string reportPath)
    {
        var report = new StringBuilder();
        int failures = 0;
        void Check(string name, bool ok) { report.AppendLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) failures++; }
        IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T match) yield return match;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
        }
        try
        {
            TreeItem? teams = null;
            for (int i = 0; i < 120 && teams is null; i++)
            {
                await Task.Delay(250);
                teams = window.Tree.Items.OfType<TreeItem>().FirstOrDefault(t => t.Name == "Teams" && t.Children.Any(c => c.Team is not null));
            }
            if (teams is null) throw new InvalidOperationException("Team browser did not load.");
            teams.IsSelected = true;
            await Task.Delay(300);
            var browserTab = window.Docs.Items.OfType<DocumentTab>().Single(d => d.Title == "Teams");
            var browserGrid = Descendants<DataGrid>(browserTab.Content).First();
            var profile = teams.Children.First(c => c.Team is not null).Team!;
            var before = profile.Team;
            window.OpenTeam(profile);
            await Task.Delay(300);
            var tab = window.Docs.Items.OfType<DocumentTab>().Last(d => d.Content is TeamView);
            var oldContent = tab.Content;
            Descendants<TabControl>(tab.Content).First(t => t.Tag?.ToString() == "TeamWorkspaceTabs").SelectedItem = Descendants<TabControl>(tab.Content).First(t => t.Tag?.ToString() == "TeamWorkspaceTabs").Items.OfType<TabItem>().Single(t => t.Header.ToString() == "Edit assignments");
            await Task.Delay(100);
            var editor = Descendants<TeamAssignmentView>(tab.Content).First();
            var picks = Descendants<ComboBox>(editor).Take(3).ToList();
            Check("three assignment selectors visible", picks.Count == 3);
            foreach (var pick in picks) pick.SelectedIndex = (pick.SelectedIndex + 1) % pick.Items.Count;
            int Id(ComboBox pick) => (int)pick.SelectedItem.GetType().GetProperty("Id")!.GetValue(pick.SelectedItem)!;
            var uniform = Id(picks[0]); var logo = Id(picks[1]); var stadium = Id(picks[2]);
            Check("draft selections leave roster unchanged", TeamRoster.Read(profile.Roster.ReadAll(), TeamRoster.IsXbox(profile.Roster)).Single(t => t.RosterIndex == before.RosterIndex) == before);
            var apply = Descendants<Button>(editor).Single(b => b.Tag?.ToString() == "ApplyTeamAssignments");
            Check("reviewed draft can be applied", apply.IsEnabled);
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(300);
            Check("live team profile reflects applied IDs", profile.Team.UniformId == uniform && profile.Team.LogoId == logo && profile.Team.StadiumId == stadium);
            Check("open team view refreshes", !ReferenceEquals(tab.Content, oldContent));
            Check("browser grid reflects applied assignments", browserGrid.Items.Cast<MVPWorkshop.Core.Vfs.TeamProfile>().Any(p => p.Team.RosterIndex == before.RosterIndex && p.Team.UniformId == uniform && p.Team.LogoId == logo && p.Team.StadiumId == stadium));
            Check("tree resolves edited logo and uniforms", teams.Children.First(c => c.Team == profile).Children.Any(c => c.Name == "Logos") && profile.Assets.Any(a => a.Category == "Uniforms/Player textures"));
            window.UpdateLayout();
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(reportPath + ".png")) png.Save(stream);
            Editor.Session!.Undo();
            Check("undo refreshes browser to original IDs", profile.Team == before);
            Editor.Session.Redo();
            Check("redo refreshes browser to edited IDs", profile.Team.UniformId == uniform && profile.Team.LogoId == logo && profile.Team.StadiumId == stadium);
            Editor.Session.Undo();
        }
        catch (Exception ex) { failures++; report.AppendLine(ex.ToString()); }
        report.AppendLine($"Failures: {failures}");
        File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }
}

