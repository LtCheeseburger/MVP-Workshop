using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App.Viewers;

public sealed class DiagnosticsView : DockPanel, IDisposable
{
    private readonly MainWindow _window;
    private readonly DataGrid _rows = B.Grid();
    private readonly TextBlock _summary = B.Text("", "Text", 13);
    public DiagnosticsView(MainWindow window)
    {
        _window = window;
        var head = new StackPanel { Margin = new Thickness(16) };
        head.Children.Add(_summary);
        head.Children.Add(B.Text("Errors block Export and Test in Game. Warnings describe changes that may need review.", "Muted"));
        SetDock(head, Dock.Top); Children.Add(head);
        _rows.IsReadOnly = true;
        _rows.Columns.Add(B.Col("Severity", nameof(ModDiagnostic.Severity), 90));
        _rows.Columns.Add(B.Col("Asset", nameof(ModDiagnostic.Asset), 260));
        _rows.Columns.Add(new DataGridTextColumn { Header = "Message", Binding = new Binding(nameof(ModDiagnostic.Message)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } } });
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(ContentControl.ContentProperty, "Go to");
        button.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) => { if (((Button)sender).DataContext is ModDiagnostic row) window.GoToDiagnostic(row); }));
        _rows.Columns.Add(new DataGridTemplateColumn { Header = "", Width = 70, CellTemplate = new DataTemplate { VisualTree = button } });
        Children.Add(_rows);
        window.DiagnosticsChanged += Refresh;
        Refresh();
    }
    private void Refresh()
    {
        _rows.ItemsSource = _window.Diagnostics;
        _summary.Text = $"{_window.Diagnostics.Count(r => r.Severity == DiagnosticSeverity.Error)} errors · {_window.Diagnostics.Count(r => r.Severity == DiagnosticSeverity.Warning)} warnings";
    }
    public void Dispose() => _window.DiagnosticsChanged -= Refresh;
}
