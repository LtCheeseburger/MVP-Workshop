using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App.Viewers;

internal sealed class RosterCsvReview : Window
{
    internal RosterCsvReview(RosterCsvPlan plan)
    {
        Title = "Review roster CSV"; Width = 1120; Height = 660; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MaxWidth = Math.Max(600, SystemParameters.WorkArea.Width - 40); MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40);
        Background = Ui.Res("WindowBg"); Foreground = Ui.Res("Text");
        var panel = new DockPanel { Margin = new Thickness(18) }; Content = panel;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        heading.Children.Add(B.Text($"{plan.ChangedRows} changed player/source rows · {plan.Changes.Count} field changes · {plan.Problems.Count} problems", "Text", 17));
        heading.Children.Add(new TextBlock { Text = "Only existing DAT value spans change. School/slot/player/source identities are read-only. Jersey numbers must be 0–99; other raw fields use the roster editor's numeric validation because their enum ranges are unresolved.", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 8, 0, 12) });
        if (plan.Problems.Count > 0) heading.Children.Add(new ScrollViewer { MaxHeight = 140, Content = new TextBlock { Text = string.Join("\n", plan.Problems), Foreground = Ui.Res("Bad"), TextWrapping = TextWrapping.Wrap } });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var apply = B.Button("Apply roster CSV", null, (_, _) => DialogResult = true, "Primary"); apply.Tag = "ApplyRosterCsv";
        apply.IsEnabled = plan.Problems.Count == 0 && plan.ChangedRows > 0; apply.IsDefault = true;
        var cancel = B.Button("Cancel", null, (_, _) => DialogResult = false); cancel.IsCancel = true;
        buttons.Children.Add(apply); buttons.Children.Add(cancel);
        var grid = B.Grid(); grid.IsReadOnly = true;
        grid.Columns.Add(B.Col("CSV row", nameof(RosterCsvChange.CsvRow), 65)); grid.Columns.Add(B.Col("School", nameof(RosterCsvChange.School), 140));
        grid.Columns.Add(B.Col("Slot", nameof(RosterCsvChange.Slot), 45)); grid.Columns.Add(B.Col("Player key", nameof(RosterCsvChange.Player), 90));
        grid.Columns.Add(B.Col("Source DAT", nameof(RosterCsvChange.Source), 170)); grid.Columns.Add(B.Col("Field", nameof(RosterCsvChange.Field), 240));
        grid.Columns.Add(B.Col("Before", nameof(RosterCsvChange.Before), 110)); grid.Columns.Add(B.Col("After", nameof(RosterCsvChange.After), 110));
        grid.Columns.Add(new DataGridTextColumn { Header = "Validation problem", Binding = new Binding(nameof(RosterCsvChange.Problem)), MinWidth = 180, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.ItemsSource = plan.Changes; panel.Children.Add(grid);
    }
}
