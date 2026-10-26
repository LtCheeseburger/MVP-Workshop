using System.Windows.Controls;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App.Viewers;

/// <summary>Sections / symbols / segments of a PS2 ELF (executables, IRX modules, .ord/.o model objects).</summary>
public sealed class ElfView : Grid
{
    public ElfView(ElfFile elf)
    {
        var tabs = new TabControl { Tag = Ui.Res("PanelAlt") };

        var secs = B.Grid();
        secs.Columns.Add(B.Col("#", nameof(ElfSection.Index), 40));
        secs.Columns.Add(B.Col("Name", nameof(ElfSection.Name), 180, true));
        secs.Columns.Add(B.Col("Type", nameof(ElfSection.Type), 120));
        secs.Columns.Add(B.Col("Flags", nameof(ElfSection.FlagsText), 60, true));
        secs.Columns.Add(B.Col("Addr", nameof(ElfSection.Addr), 100, true, "0x{0:X8}"));
        secs.Columns.Add(B.Col("Offset", nameof(ElfSection.Offset), 100, true, "0x{0:X}"));
        secs.Columns.Add(B.Col("Size", nameof(ElfSection.Size), 100, true, "0x{0:X}"));
        secs.Columns.Add(B.Col("Link", nameof(ElfSection.Link), 50));
        secs.Columns.Add(B.Col("Info", nameof(ElfSection.Info), 50));
        secs.Columns.Add(B.Col("Align", nameof(ElfSection.Align), 60));
        secs.ItemsSource = elf.Sections;
        tabs.Items.Add(new TabItem { Header = $"Sections ({elf.Sections.Count})", Content = secs });

        var syms = B.Grid();
        syms.Columns.Add(B.Col("#", nameof(ElfSymbol.Index), 60));
        syms.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding(nameof(ElfSymbol.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        syms.Columns.Add(B.Col("Value", nameof(ElfSymbol.Value), 100, true, "0x{0:X8}"));
        syms.Columns.Add(B.Col("Size", nameof(ElfSymbol.Size), 80, true, "0x{0:X}"));
        syms.Columns.Add(B.Col("Type", nameof(ElfSymbol.Type), 80));
        syms.Columns.Add(B.Col("Bind", nameof(ElfSymbol.Bind), 70));
        syms.Columns.Add(B.Col("Section", nameof(ElfSymbol.Section), 140, true));
        syms.ItemsSource = elf.Symbols;
        var filter = B.SearchBox("Filter symbols", q => syms.ItemsSource = q.Length == 0 ? elf.Symbols : elf.Symbols.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Section.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList());
        var symHost = B.Rows((B.Toolbar(B.Glyph("", "Muted", 12), filter), B.Auto), (syms, B.Star()));
        tabs.Items.Add(new TabItem { Header = $"Symbols ({elf.Symbols.Count:N0})", Content = symHost });

        if (elf.Segments.Count > 0)
        {
            var segs = B.Grid();
            segs.Columns.Add(B.Col("#", nameof(ElfSegment.Index), 40));
            segs.Columns.Add(B.Col("Type", nameof(ElfSegment.Type), 100));
            segs.Columns.Add(B.Col("Offset", nameof(ElfSegment.Offset), 100, true, "0x{0:X}"));
            segs.Columns.Add(B.Col("VAddr", nameof(ElfSegment.VAddr), 100, true, "0x{0:X8}"));
            segs.Columns.Add(B.Col("File size", nameof(ElfSegment.FileSize), 100, true, "0x{0:X}"));
            segs.Columns.Add(B.Col("Mem size", nameof(ElfSegment.MemSize), 100, true, "0x{0:X}"));
            segs.Columns.Add(B.Col("Flags", nameof(ElfSegment.Flags), 60));
            segs.ItemsSource = elf.Segments;
            tabs.Items.Add(new TabItem { Header = $"Segments ({elf.Segments.Count})", Content = segs });
        }
        tabs.SelectedIndex = elf.Symbols.Count > 0 && elf.Kind == "Relocatable" ? 1 : 0;
        Children.Add(tabs);
    }
}
