using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.Core.Vfs;

public static class SpeechPairs
{
    /// <summary>The DAT stream a header record describes: <c>NNAMEHDR.BIG/201.hdr ↔ NNAMEDAT.BIG/201.dat</c>, or record N of a PROJx OFF pack.</summary>
    public static AssetNode? StreamFor(AssetNode hdr)
    {
        var pack = hdr.Parent;
        if (pack?.Parent is null) return null;
        var datName = pack.Name.Replace("HDR", "DAT", StringComparison.OrdinalIgnoreCase);
        var datPack = pack.Parent.Children.FirstOrDefault(c => c != pack && c.Name.Equals(datName, StringComparison.OrdinalIgnoreCase));
        if (datPack is null) return null;
        if (pack.Format == FormatId.OffsetPack)
        {
            var index = -1;
            for (var i = 0; i < pack.Children.Count; i++) if (pack.Children[i] == hdr) { index = i; break; }
            return index >= 0 && index < datPack.Children.Count ? datPack.Children[index] : null;
        }
        var stem = Path.GetFileNameWithoutExtension(hdr.Name);
        return datPack.Children.FirstOrDefault(c => Path.GetFileNameWithoutExtension(c.Name).Equals(stem, StringComparison.OrdinalIgnoreCase));
    }
}
