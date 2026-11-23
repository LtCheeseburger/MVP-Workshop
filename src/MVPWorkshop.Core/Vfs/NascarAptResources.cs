using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Vfs;

/// <summary>Resolves numbered Xbox menu resources through the authored character/resource table.</summary>
public static class NascarAptResources
{
    public sealed record Resource(int Character, int Kind, AssetNode Node);
    public static IReadOnlyList<Resource> Read(AssetNode movie)
    {
        if (movie.Parent is not { Format: FormatId.TerfArchive } parent) return [];
        var children = parent.Children;
        if (children.Count < 3 || children[0] != movie || children[1].Format != FormatId.AptConst) return [];
        var table = children[2].ReadAll();
        if (table.Length < 4) return [];
        uint count = Bin.U32LE(table, 0);
        if (count > 65536 || table.Length != 4L + count * 8L || children.Count != 3L + count) return [];
        var result = new List<Resource>();
        for (int i = 0; i < count; i++)
        {
            int p = 4 + i * 8, character = checked((int)Bin.U32LE(table, p)), kind = checked((int)Bin.U32LE(table, p + 4));
            result.Add(new(character, kind, children[i + 3]));
        }
        return result;
    }
}
