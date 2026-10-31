using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunRosterCsvCheck(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T item) yield return item;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
        }
        try
        {
            var scope = await RosterCsvScope(null);
            var hashes = Directory.GetFiles(GameRoot!.DiskPath!, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
            Check("all-school roster scope resolves", scope.Count == 2 && scope.All(s => s.Players.Count == 1));
            var tools = MainMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "_Tools");
            Check("all-school CSV commands are reachable", tools.Items.OfType<MenuItem>().Any(i => i.Header.ToString() == "Export all rosters CSV…") && tools.Items.OfType<MenuItem>().Any(i => i.Header.ToString() == "Import roster CSV (all schools)…"));
            OpenTeam(scope[0]); UpdateLayout();
            Check("school workspace exposes CSV commands", Find<TextBlock>(((DocumentTab)Docs.SelectedItem).Content).Any(t => t.Text == "Export roster CSV") && Find<TextBlock>(((DocumentTab)Docs.SelectedItem).Content).Any(t => t.Text == "Import roster CSV"));
            var csv = RosterCsv.Export([scope[0]]);
            var path = Path.Combine(Path.GetDirectoryName(reportPath)!, "roster.csv"); File.WriteAllText(path, csv, new UTF8Encoding(true));
            var edited = File.ReadAllText(path).Replace(",Alpha,Player,1,", ",CSV Name,Player,42,");
            var plan = RosterCsv.Plan(edited, [scope[0]]);
            Check("single-school CSV preview validates changed fields", plan.Problems.Count == 0 && plan.ChangedRows == 1 && plan.Changes.Count == 2);
            var review = new RosterCsvReview(plan) { Owner = this };
            review.Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    Check("review displays before/after rows", Find<DataGrid>(review).Single().Items.Count == 2);
                    review.UpdateLayout();
                    var image = new RenderTargetBitmap((int)review.ActualWidth, (int)review.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(review);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                    using (var stream = File.Create(reportPath + ".png")) png.Save(stream);
                    Find<Button>(review).Single(b => b.Tag as string == "ApplyRosterCsv").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { report.AppendLine("FAIL " + ex); review.Close(); }
            });
            Check("review Apply accepts the preview", review.ShowDialog() == true);
            Check("CSV commits one edited source file", plan.Apply(Editor.Session!) == 1);
            var source = scope[0].Players[0].Sources.Single(s => s.Asset.Name == "attrib.dat").Asset;
            var record = PlayerAttributes.Read(source.ReadAll()).Records[scope[0].Players[0].Id];
            Check("committed values reopen in the roster editor data", record.Field(0) == "CSV Name" && record.Field(2) == "42");
            var invalid = RosterCsv.Plan(edited.Replace(",42,", ",100,"), [scope[0]]);
            var rejected = new RosterCsvReview(invalid) { Owner = this }; rejected.Show(); rejected.UpdateLayout();
            Check("review disables Apply when a range is invalid", invalid.Problems.Count > 0 && !Find<Button>(rejected).Single(b => b.Tag as string == "ApplyRosterCsv").IsEnabled);
            rejected.Close();
            bool guarded = false; try { Exporter.CheckOutput(Path.Combine(GameRoot.DiskPath!, "roster.csv"), GameRoot); } catch (InvalidOperationException) { guarded = true; }
            Check("CSV export protects the game folder", guarded);
            Editor.Session!.Undo();
            Check("undo restores original roster source bytes", PlayerAttributes.Read(source.ReadAll()).Records[scope[0].Players[0].Id].Field(0) == "Alpha");
            Check("source game files remain unchanged", hashes.All(p => SHA256.HashData(File.ReadAllBytes(p.Key)).SequenceEqual(p.Value)) && Directory.GetFiles(GameRoot.DiskPath!, "*", SearchOption.AllDirectories).Length == hashes.Count);
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        finally { Editor.Session?.MarkSaved(); File.WriteAllText(reportPath, report.ToString()); Application.Current.Shutdown(); }
    }
}
