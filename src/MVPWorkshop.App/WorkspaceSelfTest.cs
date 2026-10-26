using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

/// <summary>Exercises school workspace navigation and project overlay refresh without modifying disc files.</summary>
public static class WorkspaceSelfTest
{
    public static async void Run(MainWindow window, string reportPath)
    {
        var report = new StringBuilder();
        int failures = 0;
        void Check(string name, bool ok) { report.AppendLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) failures++; }
        IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T match) yield return match;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                foreach (var child in Find<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
        }
        async Task Capture(string suffix)
        {
            await Task.Delay(400);
            window.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(window);
            var image = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            image.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(reportPath + suffix + ".png"); png.Save(stream);
        }
        try
        {
            window.LogRow.Height = new GridLength(0); window.LogSplitRow.Height = new GridLength(0);
            TreeItem? teams = null;
            for (int i = 0; i < 120 && teams is null; i++)
            {
                await Task.Delay(250);
                teams = window.Tree.Items.OfType<TreeItem>().FirstOrDefault(t => t.Name == "Teams" && t.Children.Any(c => c.Team is not null));
            }
            if (teams is null) throw new InvalidOperationException("Team browser not loaded.");
            var profiles = teams.Children.Where(t => t.Team is not null).Select(t => t.Team!).ToList();
            var lsu = profiles.Single(p => p.Team.Abbreviation == "LSU");
            var diverse = profiles.Where(p => p.StadiumEvidence.Contains("also links")).Take(2).Prepend(lsu).ToList();
            foreach (var profile in diverse)
            {
                window.OpenTeam(profile);
                await Task.Delay(150);
                var tab = (DocumentTab)window.Docs.SelectedItem;
                var view = (TeamView)tab.Content;
                Check(profile.Label + ": workspace and all relationship sections", Find<TabControl>(view).First().Items.Count == 9);
                await Capture("-" + profile.Team.Abbreviation.Replace("/", "_") + "-overview");
                view.SelectedSection = 1;
                await Task.Delay(150);
                var roster = Find<DataGrid>(view).First();
                Check(profile.Label + ": roster rows visible", roster.Items.Count == profile.Team.PlayerCount);
                roster.SelectedIndex = Math.Min(3, roster.Items.Count - 1);
                await Task.Delay(100);
                Check(profile.Label + ": structured player Apply action", Find<Button>(view).Any(b => b.Tag?.ToString() == "ApplyPlayerFields:attrib.dat"));
                var selectedSlot = view.SelectedPlayerSlot;
                window.OpenSchoolAsset(profile, profile.Players[0].Sources.First().Asset);
                Check(profile.Label + ": player source stays in school roster", ReferenceEquals(window.Docs.SelectedItem, tab) && view.SelectedPlayerSlot == selectedSlot);
                window.OpenSchoolAsset(profile, profile.Roster);
                Check(profile.Label + ": roster source opens only school players", ReferenceEquals(window.Docs.SelectedItem, tab) && roster.Items.OfType<TeamPlayer>().All(p => profile.Players.Any(own => own.Id == p.Id)));
                var info = TeamWorkspaceView.SchoolInfo(profile);
                window.OpenWorkspaceDocument(new DocumentTab { Title = "School scope check", Content = (FrameworkElement)info });
                await Task.Delay(100);
                window.UpdateLayout();
                var infoText = string.Join("\n", Find<TextBlock>(info).Select(t => t.Text));
                Check(profile.Label + ": school info names selected school", infoText.Contains(profile.Team.Name) && !infoText.Contains("A&M,60,Aggies,College Station,44"));
                window.Docs.SelectedItem = tab;
                await Capture("-" + profile.Team.Abbreviation.Replace("/", "_") + "-players");
            }
            window.OpenTeam(lsu);
            var lsuTab = (DocumentTab)window.Docs.SelectedItem;
            var oldView = (TeamView)lsuTab.Content;
            oldView.SelectedSection = 1;
            oldView.SelectedPlayerSlot = 4;
            var source = lsu.Players[0].Sources.Single(s => s.Asset.Name == "attrib.dat").Asset;
            var original = source.ReadAll();
            // Exercise the structured UI Apply action rather than only the text editor.
            oldView.SelectedPlayerSlot = 1;
            await Task.Delay(150);
            var attributeGrid = Find<DataGrid>(oldView).First(g => g.Items.OfType<TeamWorkspaceView.EditableField>().Any(f => f.Name == "first_name"));
            var nameField = attributeGrid.Items.OfType<TeamWorkspaceView.EditableField>().Single(f => f.Name == "first_name");
            nameField.Value = "ResetMe";
            Find<Button>(oldView).Single(b => b.Tag?.ToString() == "ResetPlayerDraft:attrib.dat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            attributeGrid = Find<DataGrid>(oldView).First(g => g.Items.OfType<TeamWorkspaceView.EditableField>().Any(f => f.Name == "first_name"));
            nameField = attributeGrid.Items.OfType<TeamWorkspaceView.EditableField>().Single(f => f.Name == "first_name");
            Check("reset discards only unapplied draft", nameField.Value == "LSU" && source.ReadAll().AsSpan().SequenceEqual(original));
            nameField.Value = "StructuredTest";
            oldView.SelectedPlayerSlot = 2;
            oldView.SelectedPlayerSlot = 1;
            await Task.Delay(100);
            attributeGrid = Find<DataGrid>(oldView).First(g => g.Items.OfType<TeamWorkspaceView.EditableField>().Any(f => f.Name == "first_name"));
            Check("player draft survives selection changes", attributeGrid.Items.OfType<TeamWorkspaceView.EditableField>().Single(f => f.Name == "first_name").Value == "StructuredTest");
            var fieldSearch = Find<TextBox>(oldView).Single(t => t.Tag?.ToString() == "PlayerFieldSearch");
            fieldSearch.Text = "first_name";
            Check("field search narrows selected player fields", attributeGrid.Items.Count == 1);
            fieldSearch.Clear();
            Find<Button>(oldView).Single(b => b.Tag?.ToString() == "ApplyPlayerFields:attrib.dat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("structured player Apply commits named field", lsu.Players[0].Name == "StructuredTest 1");
            Editor.Session!.Undo();
            Check("structured Apply undo restores exact source", source.ReadAll().AsSpan().SequenceEqual(original));
            Editor.Session.Redo();
            Check("structured Apply redo updates player", lsu.Players[0].Name == "StructuredTest 1");
            Editor.Session.Undo();
            var state = ((TeamView)lsuTab.Content).PlayerState;
            var firstSource = lsu.Players[0].Sources.Single(s => s.Asset == source);
            var secondSource = lsu.Players[1].Sources.Single(s => s.Asset == source);
            var firstDraft = state.Get(firstSource, lsu.Players[0].Id);
            var secondDraft = state.Get(secondSource, lsu.Players[1].Id);
            firstDraft.Fields.Single(f => f.Id == 0).Value = "BatchOne";
            secondDraft.Fields.Single(f => f.Id == 0).Value = "BatchTwo";
            secondDraft.Fields.Single(f => f.Id == 2).Value = "invalid";
            bool saveGuarded = false;
            try { window.SaveProjectTo(Path.Combine(Path.GetTempPath(), "unapplied-player-test.mvpwproj")); } catch (InvalidOperationException) { saveGuarded = true; }
            Check("save guards unapplied player drafts", saveGuarded);
            bool invalidBatch = false;
            try { state.ApplyAll(Editor.Session, lsu.Team.Name); } catch (System.IO.InvalidDataException) { invalidBatch = true; }
            Check("batch validates all drafts before commit", invalidBatch && source.ReadAll().AsSpan().SequenceEqual(original) && state.PendingFields == 3);
            secondDraft.Fields.Single(f => f.Id == 2).Value = secondDraft.Fields.Single(f => f.Id == 2).Original;
            state.ApplyAll(Editor.Session, lsu.Team.Name);
            Check("apply all keeps multiple player edits in same DAT", lsu.Players[0].Name == "BatchOne 1" && lsu.Players[1].Name.StartsWith("BatchTwo ") && state.PendingFields == 0);
            Editor.Session.Undo();
            Check("batch undo restores exact DAT", source.ReadAll().AsSpan().SequenceEqual(original));
            firstDraft = state.Get(firstSource, lsu.Players[0].Id);
            firstDraft.Fields.Single(f => f.Id == 0).Value = "ResetAllTest";
            state.ResetAll();
            Check("reset all keeps visible draft linked and clean", state.PendingFields == 0 && ReferenceEquals(firstDraft.Fields, state.Get(firstSource, lsu.Players[0].Id).Fields));
            firstDraft.Fields.Single(f => f.Id == 0).Value = "Retained";
            var unrelated = MVPWorkshop.Core.Editing.PlayerAttributeEditor.Patch(original, lsu.Players[0].Id, new Dictionary<int, string> { [2] = "99" });
            Editor.Apply(source, unrelated, "Draft preservation check");
            Check("unrelated project refresh retains player drafts", ReferenceEquals(((TeamView)lsuTab.Content).PlayerState, state) && state.PendingFields == 1);
            state.ApplyAll(Editor.Session, lsu.Team.Name);
            Check("apply rebases over unrelated project fields", lsu.Players[0].Name == "Retained 1" && lsu.Players[0].Jersey == "99");
            Editor.Session.Undo(); Editor.Session.Undo();
            Check("rebased draft undo restores original bytes", source.ReadAll().AsSpan().SequenceEqual(original));
            oldView = (TeamView)lsuTab.Content;
            oldView.SelectedPlayerSlot = 4;
            var text = Encoding.Latin1.GetString(original);
            var changed = Encoding.Latin1.GetBytes(text.Replace("00b8f1fd6,0 LSU,1 1,", "00b8f1fd6,0 WorkspaceTest,1 1,"));
            Editor.Apply(source, changed, "Workspace refresh check");
            Check("DAT edit immediately refreshes player data", lsu.Players[0].Name == "WorkspaceTest 1");
            Check("related source edit marks team workspace edited", lsuTab.IsEdited);
            Check("open workspace rebuilds and retains section", !ReferenceEquals(oldView, lsuTab.Content) && ((TeamView)lsuTab.Content).SelectedSection == 1);
            Check("workspace retains selected player", ((TeamView)lsuTab.Content).SelectedPlayerSlot == 4);
            Editor.Session!.Undo();
            Check("undo refreshes player data", lsu.Players[0].Name == "LSU 1");
            Editor.Session.Redo();
            Check("redo refreshes player data", lsu.Players[0].Name == "WorkspaceTest 1");
            Editor.Session.Undo();
            foreach (var section in new[] { 2, 3, 4 })
            {
                var view = (TeamView)lsuTab.Content;
                view.SelectedSection = section;
                await Task.Delay(120);
                Check($"section {section}: relevant player fields visible", Find<ComboBox>(view).Any(c => c.SelectedIndex == (section == 2 ? 1 : section == 3 ? 2 : 3)));
                await Capture("-LSU-fields-" + section);
                Find<TabControl>(view).First(t => t.Items.OfType<TabItem>().Any(i => i.Header.ToString() == "Source library")).SelectedIndex = 1;
                await Task.Delay(100);
                var grid = Find<DataGrid>(view).First();
                Check($"section {section}: direct source rows", grid.Items.Count > 0);
                grid.SelectedItem = grid.Items.Cast<TeamAssetLink>().FirstOrDefault(a => section == 2 ? a.Asset.IsTexturePack && a.Asset.StoredSize > 200 : section == 3 ? a.Asset.Name == "hihead.ord" : a.Asset.Extension == ".asf") ?? grid.Items[0];
                var link = (TeamAssetLink)grid.SelectedItem;
                Find<Button>(view).Single(b => b.Tag?.ToString() == "OpenWorkspaceSource").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check($"section {section}: opens exact existing asset document", ((DocumentTab)window.Docs.SelectedItem).Node == link.Asset);
                window.Docs.SelectedItem = lsuTab;
                await Capture("-LSU-section-" + section);
            }
        }
        catch (Exception ex) { failures++; report.AppendLine(ex.ToString()); }
        report.AppendLine($"Failures: {failures}");
        File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }
}
