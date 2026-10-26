namespace MVPWorkshop.Core.Vfs;

/// <summary>Locates the main executable: SLUS_/SLES_ ELF on PS2, default.xbe on Xbox.</summary>
public static class GameExecutable
{
    public static AssetNode? Find(IEnumerable<AssetNode> nodes) =>
        nodes.FirstOrDefault(n => !n.IsFolder && (n.Name.StartsWith("SLUS_", StringComparison.OrdinalIgnoreCase) || n.Name.StartsWith("SLES_", StringComparison.OrdinalIgnoreCase) || n.Name.Equals("default.xbe", StringComparison.OrdinalIgnoreCase)));
}
