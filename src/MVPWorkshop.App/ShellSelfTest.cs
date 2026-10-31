using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    // Runs against the real window, without a game install or writes to game files.
    internal async void RunShellCheck()
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(args, "--shell-selftest");
        if (at < 0 || at + 1 >= args.Length) return;
        var report = new StringBuilder();
        void Check(string label, bool ok) => report.AppendLine($"{(ok ? "PASS" : "FAIL")} {label}");
        try
        {
            await Task.Delay(600);
            Width = 1000; Height = 620;
            var a = new DocumentTab { Title = "First", Content = new TextBlock { Text = "First document" } };
            var b = new DocumentTab { Title = "Second", Content = new TextBlock { Text = "Second document" } };
            AddTab(a); AddTab(b); UpdateLayout();
            Check("five menu headings", MainMenu.Items.Count == 5);
            Check("close all shortcut", InputBindings.OfType<KeyBinding>().Any(k => k.Key == Key.W && k.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && k.Command == CloseAllTabsCmd));
            var target = (TabItem)Docs.ItemContainerGenerator.ContainerFromItem(a);
            var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new object[] { new DataObject(typeof(DocumentTab), b), DragDropKeyStates.None, DragDropEffects.Move, target, new Point() }, null)!;
            drop.RoutedEvent = DragDrop.DropEvent;
            target.RaiseEvent(drop);
            Check("drop reorders documents and retains selection", _docs.IndexOf(b) < _docs.IndexOf(a) && Docs.SelectedItem == b);
            MainMenu.Items.OfType<MenuItem>().First().IsSubmenuOpen = true;
            await Task.Delay(250);
            Check("file menu opens in live window", MainMenu.Items.OfType<MenuItem>().First().IsSubmenuOpen);
            MainMenu.Items.OfType<MenuItem>().First().IsSubmenuOpen = false;
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(args[at + 1] + ".png")) png.Save(output);
            CloseAllTabsCmd.Execute(null, this);
            Check("close all preserves Start", _docs.Count == 1 && !_docs[0].CanClose);
            Check("close all disabled without documents", !CloseAllTabsCmd.CanExecute(null, this));
            Check("undo and redo disabled without edits", !UndoCmd.CanExecute(null, this) && !RedoCmd.CanExecute(null, this));
        }
        catch (Exception ex) { report.AppendLine("FAIL " + ex); }
        File.WriteAllText(args[at + 1], report.ToString());
        Application.Current.Shutdown();
    }
}
