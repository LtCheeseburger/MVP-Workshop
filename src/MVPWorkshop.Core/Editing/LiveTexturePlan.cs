using System.Buffers.Binary;
using System.Text.Json;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.Core.Editing;

public sealed record LiveTextureMapping(string AssetKey, int Slot, string PackPath, string File, List<string> Keys);
public sealed class LiveTexturePlan
{
    public int Format { get; set; } = 1;
    public List<LiveTextureMapping> Mappings { get; set; } = [];
    public List<string> Problems { get; set; } = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Matches gpu::Hash and texturedumper::Key in the port, including unsigned 128-bit multiplication.
    public static ulong Hash(ReadOnlySpan<byte> data, ulong previous = 1469598103934665603UL)
    {
        const ulong k0 = 0x9E3779B97F4A7C15, k1 = 0xE7037ED1A0B428DB;
        static ulong Mix(ulong a, ulong b) { var product = (UInt128)a * b; return (ulong)product ^ (ulong)(product >> 64); }
        var seed = previous ^ k0; int i = 0;
        for (; i + 16 <= data.Length; i += 16) seed = Mix(BinaryPrimitives.ReadUInt64LittleEndian(data[i..]) ^ k1, BinaryPrimitives.ReadUInt64LittleEndian(data[(i + 8)..]) ^ seed);
        var remaining = data.Length - i;
        if (remaining > 0)
        {
            ulong x = 0, y = 0;
            if (remaining >= 8) { x = BinaryPrimitives.ReadUInt64LittleEndian(data[i..]); y = BinaryPrimitives.ReadUInt64LittleEndian(data[^8..]); }
            else for (int j = 0; j < remaining; j++) x |= (ulong)data[i + j] << (j * 8);
            seed = Mix(x ^ k1, y ^ seed ^ (ulong)remaining);
        }
        return Mix(seed ^ k0, (ulong)data.Length ^ k1);
    }

    public static string? TextureKey(byte[] bytes, SshImage image)
    {
        var header = image.Header;
        uint format = header.Type switch { 0x60 => 0x0C, 0x61 => 0x0E, 0x7D => 0x06, _ => uint.MaxValue };
        if (format == uint.MaxValue || header.Width < 1 || header.Height < 1 || header.FlagSwizzled != (header.Type == 0x7D)) return null;
        int length = header.Type == 0x7D ? checked(header.Width * header.Height * 4) : checked(Math.Max(1, (header.Width + 3) / 4) * Math.Max(1, (header.Height + 3) / 4) * (header.Type == 0x60 ? 8 : 16));
        if (image.DataOffset < 0 || image.DataOffset > bytes.Length - length) return null;
        Span<byte> shape = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(shape, format); BinaryPrimitives.WriteUInt32LittleEndian(shape[4..], (uint)header.Width); BinaryPrimitives.WriteUInt32LittleEndian(shape[8..], (uint)header.Height);
        var key = Hash(bytes.AsSpan(image.DataOffset, length), Hash(shape)); return (key == 0 ? 1UL : key).ToString("x16");
    }

    public static void Write(EditSession session, string generation, string? previousGeneration)
    {
        var previous = new LiveTexturePlan();
        if (previousGeneration is not null && File.Exists(Path.Combine(previousGeneration, "live-textures.json")))
            previous = JsonSerializer.Deserialize<LiveTexturePlan>(File.ReadAllText(Path.Combine(previousGeneration, "live-textures.json")), Json) ?? previous;
        var result = new LiveTexturePlan();
        var targets = session.Overrides.Keys.Select(session.Resolve).OfType<AssetNode>().Select(ModExporter.DiskFileOf)
            .Where(n => n.IsLooseTexturePack).Select(n => n.Key).Concat(previous.Mappings.Select(m => m.AssetKey)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<(int Slot, string Key)> Keys(byte[] bytes) => SshFile.Read(bytes).Images.Select(i => (i.Index, Key: TextureKey(bytes, i))).Where(p => p.Key is not null).Select(p => (p.Index, p.Key!)).ToList();
        foreach (var assetKey in targets)
        {
            try
            {
                var node = session.Resolve(assetKey) ?? throw new InvalidDataException("Texture no longer resolves.");
                var current = session.GetBytes(node); var parts = XshFolder.Split(current) ?? throw new InvalidDataException("Unsupported native texture layout.");
                var originalImages = SshFile.Read(node.ReadOriginal()).Images; var currentImages = SshFile.Read(current).Images;
                if (originalImages.Count != currentImages.Count || originalImages.Where((image, index) => image.Tag != currentImages[index].Tag).Any())
                    throw new InvalidDataException("Changing texture slot names/count requires restarting.");
                if (!XshFolder.ReadMeta(parts[XshFolder.MetaName], out _, out var slots)) throw new InvalidDataException("Invalid texture metadata.");
                var currentKeys = Keys(current); var originalKeys = Keys(node.ReadOriginal()); var baseKeys = Keys(session.BaseBytes(node));
                var baseParts = XshFolder.Split(session.BaseBytes(node));
                var packPath = ModExporter.RelativePath(session.GameRoot, node).Replace('\\', '/');
                foreach (var slot in slots)
                {
                    if (!slot.Dds || currentKeys.All(k => k.Slot != slot.DirIndex)) continue;
                    var old = previous.Mappings.FirstOrDefault(m => m.AssetKey.Equals(assetKey, StringComparison.OrdinalIgnoreCase) && m.Slot == slot.DirIndex);
                    if (old is null && baseParts?.GetValueOrDefault(slot.File) is { } baseline && baseline.AsSpan().SequenceEqual(parts[slot.File])) continue;
                    var keys = originalKeys.Concat(baseKeys).Concat(currentKeys).Where(k => k.Slot == slot.DirIndex).Select(k => k.Key).Concat(old?.Keys ?? []).Distinct().ToList();
                    var file = "live-textures/" + result.Mappings.Count.ToString("D6") + ".dds";
                    Directory.CreateDirectory(Path.Combine(generation, "live-textures")); File.WriteAllBytes(Path.Combine(generation, file), parts[slot.File]);
                    result.Mappings.Add(new(assetKey, slot.DirIndex, packPath, file, keys));
                }
            }
            catch (Exception ex) { result.Problems.Add(assetKey + ": " + ex.Message); }
        }
        // Content-key replacement is safe only when every matching source slot belongs to the same mapping.
        var wanted = result.Mappings.SelectMany(m => m.Keys).ToHashSet();
        foreach (var node in session.GameRoot.EnumerateDeep().Where(n => n.IsLooseTexturePack))
        {
            try
            {
                var keys = Keys(node.ReadOriginal()).Concat(Keys(session.BaseBytes(node))).Distinct().ToList();
                foreach (var mapping in result.Mappings)
                    if (keys.Any(k => mapping.Keys.Contains(k.Key) && (node.Key != mapping.AssetKey || k.Slot != mapping.Slot)))
                        result.Problems.Add(mapping.AssetKey + ": texture bytes are shared with " + node.Key + "; restart to preserve asset scope.");
            }
            catch { if (wanted.Count > 0) result.Problems.Add(node.Key + ": cannot verify live texture ownership; restart required."); }
        }
        File.WriteAllText(Path.Combine(generation, "live-textures.json"), JsonSerializer.Serialize(result, Json));
    }
}
