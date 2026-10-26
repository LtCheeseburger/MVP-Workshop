using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

/// <summary>Re-opens a rebuilt file and compares it, entry by entry and level by level, with the original it was built from.</summary>
public static class ArchiveVerifier
{
    /// <param name="original">Node of the untouched game file.</param>
    /// <param name="rebuilt">Node opened from the exported file.</param>
    /// <param name="overrides">Expected replacement contents, keyed by original node key.</param>
    /// <returns>Human-readable differences; empty when the rebuilt file matches (edited assets must equal their replacements).</returns>
    public static List<string> Verify(AssetNode original, AssetNode rebuilt, IReadOnlyDictionary<string, byte[]> overrides, out int compared)
    {
        var problems = new List<string>();
        var count = 0;
        Compare(original, rebuilt, overrides, problems, ref count, 0);
        compared = count;
        return problems;
    }

    private static void Compare(AssetNode a, AssetNode b, IReadOnlyDictionary<string, byte[]> overrides, List<string> problems, ref int count, int depth)
    {
        if (problems.Count > 40) return;
        if (a.IsFolder != b.IsFolder || !a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) && depth > 0)
        { problems.Add($"{a.Path}: entry differs ({a.Name} vs {b.Name})"); return; }

        if (a.IsFolder)
        {
            CompareChildren(a, b, overrides, problems, ref count, depth);
            return;
        }

        var replaced = overrides.TryGetValue(a.Key, out var ov);
        count++;
        if (!replaced && a.Format == FormatId.BigArchive)
        {
            // archives are compared structurally: their bytes legitimately change when an entry inside is edited or re-encoded
            if (b.Format != FormatId.BigArchive) { problems.Add($"{a.Path}: no longer opens as a BIG archive"); return; }
            CompareLayout(a, b, problems);
            CompareChildren(a, b, overrides, problems, ref count, depth);
            return;
        }

        var expected = replaced ? ov! : a.ReadOriginal();
        var actual = b.ReadOriginal();
        if (!expected.AsSpan().SequenceEqual(actual))
            problems.Add($"{a.Path}: contents differ ({expected.Length:N0} expected, {actual.Length:N0} found){(replaced ? " [edited asset]" : "")}");
    }

    /// <summary>Directory names/order, header size, every header byte except offset/size fields and the declared size (so unknown header data survives), and entry alignment.</summary>
    private static void CompareLayout(AssetNode a, AssetNode b, List<string> problems)
    {
        var da = a.ReadOriginal(); var db = b.ReadOriginal();
        var ba = BigArchive.Read(new MemoryByteSource(da)); var bb = BigArchive.Read(new MemoryByteSource(db));
        if (ba.Entries.Count != bb.Entries.Count || ba.HeaderSize != bb.HeaderSize || ba.Magic != bb.Magic) { problems.Add($"{a.Path}: BIG header changed ({ba.Entries.Count}/{ba.HeaderSize} → {bb.Entries.Count}/{bb.HeaderSize})"); return; }
        var skip = new bool[ba.HeaderSize];
        for (var i = 4; i < 8; i++) skip[i] = true;
        foreach (var e in ba.Entries) for (var i = 0; i < 8; i++) skip[e.DirPos + i] = true;
        for (var i = 0; i < skip.Length; i++)
            if (!skip[i] && da[i] != db[i]) { problems.Add($"{a.Path}: header byte 0x{i:X} changed"); return; }
        var align = BigWriter.InferAlignment(ba.Entries);
        for (var i = 0; i < ba.Entries.Count; i++)
        {
            if (ba.Entries[i].Name != bb.Entries[i].Name) { problems.Add($"{a.Path}: entry {i} renamed"); return; }
            if (ba.Entries[i].Offset % align == 0 && bb.Entries[i].Offset % align != 0 && ba.Entries[i].Size > 0) { problems.Add($"{a.Path}: entry '{ba.Entries[i].Name}' lost its {align}-byte alignment"); return; }
        }
        if (bb.DeclaredSize != db.Length) problems.Add($"{a.Path}: declared size {bb.DeclaredSize} ≠ actual {db.Length}");
    }

    private static void CompareChildren(AssetNode a, AssetNode b, IReadOnlyDictionary<string, byte[]> overrides, List<string> problems, ref int count, int depth)
    {
        var ac = a.Children; var bc = b.Children;
        if (ac.Count != bc.Count) { problems.Add($"{a.Path}: {ac.Count} entries originally, {bc.Count} after rebuild"); return; }
        for (var i = 0; i < ac.Count; i++) Compare(ac[i], bc[i], overrides, problems, ref count, depth + 1);
    }
}
