using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App.Viewers;

public sealed class TiburonDatabaseView : DockPanel
{
    private readonly AssetNode node;
    private TiburonDatabase database;
    private readonly ComboBox tables = new() { MinWidth = 300 };
    private readonly DataGrid grid = B.Grid();
    private readonly TextBlock selected = B.Text("Select a cell to edit", "Muted");
    private readonly TextBox input = new() { MinWidth = 220, MaxWidth = 500 };
    private TiburonDatabase.Table? table;
    private int row = -1, field = -1;

    public TiburonDatabaseView(AssetNode node)
    {
        this.node = node; database = TiburonDatabase.Read(node.ReadAll());
        var toolbar = B.Toolbar(tables, B.Button("Export table CSV…", "", (_, _) => Export()));
        SetDock(toolbar, Dock.Top); Children.Add(toolbar);
        var edit = B.Toolbar(selected, input, B.Button("Apply field", "", (_, _) => Apply()));
        SetDock(edit, Dock.Bottom); Children.Add(edit);
        grid.SelectionUnit = DataGridSelectionUnit.Cell; grid.AutoGenerateColumns = false;
        grid.SelectedCellsChanged += (_, _) => SelectCell(); Children.Add(grid);
        tables.SelectionChanged += (_, _) => LoadTable();
        tables.ItemsSource = database.Tables.Select(t => t.Label).ToArray();
        int drivers = database.Tables.FindIndex(t => t.Name == "DRVR");
        tables.SelectedIndex = drivers >= 0 ? drivers : Math.Max(0, database.Tables.FindIndex(t => t.Rows > 0));
        Loaded += (_, _) => Editor.Changed += Refresh;
        Unloaded += (_, _) => Editor.Changed -= Refresh;
    }
    private void Refresh()
    {
        int index = tables.SelectedIndex;
        database = TiburonDatabase.Read(node.ReadAll()); tables.ItemsSource = database.Tables.Select(t => t.Label).ToArray();
        tables.SelectedIndex = Math.Clamp(index, 0, Math.Max(0, database.Tables.Count - 1));
    }
    private void LoadTable()
    {
        table = database.Tables.ElementAtOrDefault(tables.SelectedIndex);
        grid.Columns.Clear(); row = field = -1; input.Text = "";
        if (table is null) return;
        var data = new DataTable(); data.Columns.Add("Row", typeof(int));
        grid.Columns.Add(new DataGridTextColumn { Header = "Row", Binding = new System.Windows.Data.Binding("[Row]") });
        for (int f = 0; f < table.Fields.Count; f++)
        {
            string key = "F" + f; data.Columns.Add(key);
            grid.Columns.Add(new DataGridTextColumn { Header = table.Fields[f].Name, Binding = new System.Windows.Data.Binding("[" + key + "]"), MinWidth = 80 });
        }
        for (int r = 0; r < table.Rows; r++)
        {
            var record = data.NewRow(); record[0] = r;
            for (int f = 0; f < table.Fields.Count; f++) record[f + 1] = database.Get(table, r, table.Fields[f]);
            data.Rows.Add(record);
        }
        grid.ItemsSource = data.DefaultView;
        selected.Text = $"{table.Rows:N0} records · original field IDs · select a cell";
    }
    private void SelectCell()
    {
        if (table is null || grid.SelectedCells.Count == 0 || grid.SelectedCells[0].Item is not DataRowView record) return;
        var cell = grid.SelectedCells[0]; field = grid.Columns.IndexOf(cell.Column) - 1; row = (int)record["Row"];
        if (field < 0 || field >= table.Fields.Count) return;
        var definition = table.Fields[field]; input.Text = database.Get(table, row, definition);
        selected.Text = $"{table.Name}[{row}].{definition.Name} · type {definition.Type} · {definition.Bits} bits";
    }
    private void Apply()
    {
        if (table is null || row < 0 || field < 0) return;
        try
        {
            var definition = table.Fields[field];
            if (input.Text == database.Get(table, row, definition)) return;
            var result = database.Set(table, row, definition, input.Text);
            if (TiburonDatabase.Read(result).CheckChecksums().Count != 0) throw new InvalidDataException("DB checksum verification failed.");
            Editor.Apply(node, result, $"Edit {table.Name}[{row}].{definition.Name}");
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Edit database field", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Export()
    {
        if (table is null) return;
        var picker = new SaveFileDialog { Filter = "CSV table|*.csv", FileName = table.Name + ".csv" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        using var writer = new StreamWriter(picker.FileName, false, new System.Text.UTF8Encoding(true));
        writer.WriteLine(string.Join(",", table.Fields.Select(f => Quote(f.Name))));
        for (int r = 0; r < table.Rows; r++) writer.WriteLine(string.Join(",", table.Fields.Select(f => Quote(database.Get(table, r, f)))));
    }
}
