using System.Buffers.Binary;
using System.Text;

namespace MVPWorkshop.Core.Install;

/// <summary>
/// Xbox DVD file system image (XDVDFS), read only: a whole redump disc (game partition behind the video one), or the
/// game partition alone (xiso). A port of the runtime's sdk/runtime/kernel/disc.cpp; keep the two in step.
/// </summary>
public sealed class XboxDisc : IDisposable
{
    public sealed class Entry
    {
        public required string Name { get; init; }
        public long Offset { get; init; }   // bytes into the image file; a folder's is its entry table
        public uint Size { get; init; }
        public bool Directory { get; init; }
        public List<Entry> Children { get; } = [];
    }

    const int Sector = 2048;
    // the game partition: at the start of an xiso, past the video partition of a whole original Xbox disc dump
    static readonly long[] Partitions = [0, 0x18300000];
    const int VolumeAt = 32 * Sector;
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");
    const int EntryHead = 0xE;
    const int MaxTable = 16 << 20;

    readonly FileStream file;
    readonly object gate = new();

    public string Path { get; }
    public Entry Root { get; private set; } = null!;
    public long Time { get; private set; }  // the volume's FILETIME

    XboxDisc(string path)
    {
        Path = path;
        file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
    }

    /// <summary>Opens an image; null with the reason when it isn't an Xbox disc.</summary>
    public static XboxDisc? Open(string path, out string? error)
    {
        error = null;
        XboxDisc disc;
        try { disc = new XboxDisc(path); }
        catch (Exception e) { error = $"cannot open the file ({e.Message})"; return null; }
        var volume = new byte[Sector];
        foreach (var basePos in Partitions)
        {
            if (disc.ReadAt(basePos + VolumeAt, volume) != Sector || !volume.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                !volume.AsSpan(0x7EC, Magic.Length).SequenceEqual(Magic))
                continue;
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(volume.AsSpan(0x14));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(volume.AsSpan(0x18));
            disc.Time = BinaryPrimitives.ReadInt64LittleEndian(volume.AsSpan(0x1C));
            disc.Root = new Entry { Name = "", Offset = basePos + (long)sector * Sector, Size = size, Directory = true };
            if (!disc.ReadDir(disc.Root, basePos, 0)) { error = "its folders cannot be read"; disc.Dispose(); return null; }
            return disc;
        }
        error = "it is not an Xbox disc image";
        disc.Dispose();
        return null;
    }

    int ReadAt(long at, Span<byte> buffer)
    {
        lock (gate)
        {
            file.Position = at;
            var total = 0;
            while (total < buffer.Length)
            {
                var n = file.Read(buffer[total..]);
                if (n == 0) break;
                total += n;
            }
            return total;
        }
    }

    // a folder is a table of entries in a binary tree: +0 left and +2 right (4-byte units, 0 none), +4 sector,
    // +8 size, +0xC attributes (0x10 folder), +0xD name length, +0xE name; a table of 0xFF is empty
    bool ReadDir(Entry dir, long basePos, int depth)
    {
        if (dir.Size == 0) return true;
        if (depth > 32 || dir.Size > MaxTable) return false;
        var table = new byte[dir.Size];
        if (ReadAt(dir.Offset, table) != table.Length) return false;
        var seen = new bool[table.Length / 4 + 1];
        var todo = new Stack<int>();
        todo.Push(0);
        while (todo.Count > 0)
        {
            var at = todo.Pop();
            if (at + EntryHead > table.Length || seen[at / 4]) continue;
            seen[at / 4] = true;
            var left = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(at));
            var right = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(at + 2));
            if (left == 0xFFFF) continue;
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at + 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at + 8));
            int attributes = table[at + 0xC], length = table[at + 0xD];
            if (at + EntryHead + length > table.Length) continue;
            if (left != 0) todo.Push(left * 4);
            if (right != 0) todo.Push(right * 4);
            dir.Children.Add(new Entry
            {
                Name = Encoding.Latin1.GetString(table, at + EntryHead, length),
                Offset = basePos + (long)sector * Sector, Size = size, Directory = (attributes & 0x10) != 0,
            });
        }
        dir.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var child in dir.Children)
            if (child.Directory && !ReadDir(child, basePos, depth + 1)) return false;
        return true;
    }

    /// <summary>A file or folder by its backslash or slash separated path from the root, any case; null when missing.</summary>
    public Entry? Find(string path)
    {
        var cur = Root;
        foreach (var part in path.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = cur.Children.FirstOrDefault(e => string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next == null) return null;
            cur = next;
        }
        return cur;
    }

    public byte[] Load(Entry file)
    {
        if (file.Directory) throw new InvalidOperationException($"{file.Name} is a folder");
        var data = new byte[file.Size];
        if (ReadAt(file.Offset, data) != data.Length) throw new IOException($"{Path}: {file.Name} is cut short");
        return data;
    }

    /// <summary>Every file with its path from the root (backslashes), folders first walked in name order.</summary>
    public IEnumerable<(string Path, Entry File)> Files()
    {
        var stack = new Stack<(string, Entry)>();
        stack.Push(("", Root));
        while (stack.Count > 0)
        {
            var (prefix, dir) = stack.Pop();
            foreach (var e in dir.Children)
            {
                var p = prefix.Length == 0 ? e.Name : prefix + "\\" + e.Name;
                if (e.Directory) stack.Push((p, e));
                else yield return (p, e);
            }
        }
    }

    public void Dispose() => file.Dispose();
}
