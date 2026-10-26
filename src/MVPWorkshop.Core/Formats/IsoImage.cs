using System.Buffers.Binary;
using System.Text;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record IsoEntry(string Path, uint Sector, uint Length, bool Directory, long RecordOffset);

/// <summary>Read-only cooked 2048-byte ISO9660 view. Original sectors are retained by the exporter.</summary>
public sealed class IsoImage
{
    public const int SectorSize = 2048;
    public required string SourcePath { get; init; }
    public List<IsoEntry> Entries { get; } = [];
    public List<long> VolumeDescriptors { get; } = [];
    public bool HasUdf { get; private set; }
    private readonly List<(uint Sector, uint Length)> _directories = [];
    public static IsoImage Read(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var source = new FileRangeByteSource(path, 0, new FileInfo(path).Length);
        if (source.Length % SectorSize != 0) throw new InvalidDataException("Expected a cooked 2048-byte sector ISO image.");
        var iso = new IsoImage { SourcePath = path };
        byte[]? primary = null;
        for (int sector = 16; sector < 64 && (long)(sector + 1) * SectorSize <= source.Length; sector++)
        {
            var descriptor = source.ReadBytes((long)sector * SectorSize, SectorSize);
            var signature = Encoding.ASCII.GetString(descriptor, 1, 5);
            if (signature is "BEA01" or "NSR02" or "NSR03" or "TEA01") iso.HasUdf = true;
            if (signature != "CD001") continue;
            if (descriptor[6] != 1) throw new InvalidDataException("Unsupported ISO9660 descriptor version.");
            if (descriptor[0] is 1 or 2)
            {
                if (Bin.U16LE(descriptor, 128) != SectorSize || BinaryPrimitives.ReadUInt16BigEndian(descriptor.AsSpan(130)) != SectorSize) throw new InvalidDataException("ISO logical block size must be 2048 bytes.");
                if (descriptor[0] == 2) throw new NotSupportedException("Supplementary/Joliet directory trees are not supported for editing.");
                iso.VolumeDescriptors.Add((long)sector * SectorSize); primary ??= descriptor;
            }
        }
        if (primary is null) throw new InvalidDataException("ISO9660 primary volume descriptor not found (raw BIN/CUE is unsupported).");
        var seen = new HashSet<uint>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Directory(uint sector, uint length, string parent, int depth)
        {
            if (depth > 32 || length > 32 * 1024 * 1024 || !seen.Add(sector)) throw new InvalidDataException("Invalid/cyclic ISO directory.");
            if ((long)sector * SectorSize + length > source.Length) throw new InvalidDataException("ISO directory exceeds source image.");
            iso._directories.Add((sector, length));
            var bytes = source.ReadBytes((long)sector * SectorSize, checked((int)length));
            for (int offset = 0; offset < bytes.Length;)
            {
                int count = bytes[offset];
                if (count == 0) { offset = (offset / SectorSize + 1) * SectorSize; continue; }
                if (count < 34 || offset + count > bytes.Length || offset % SectorSize + count > SectorSize) throw new InvalidDataException("Malformed ISO directory record.");
                var record = bytes.AsSpan(offset, count); int nameLength = record[32];
                if (33 + nameLength > count || record[1] != 0 || record[26] != 0 || record[27] != 0 || (record[25] & 0x80) != 0)
                    throw new NotSupportedException("Extended, interleaved or multi-extent ISO files are unsupported.");
                uint extent = Bin.U32LE(record, 2), size = Bin.U32LE(record, 10);
                if (extent != Bin.U32BE(record, 6) || size != Bin.U32BE(record, 14)) throw new InvalidDataException("ISO endian copies disagree.");
                if ((long)extent * SectorSize + size > source.Length) throw new InvalidDataException("ISO file extent exceeds source image.");
                if (nameLength == 1 && record[33] is 0 or 1) { offset += count; continue; }
                var name = Encoding.ASCII.GetString(record.Slice(33, nameLength));
                var version = name.LastIndexOf(';'); if (version >= 0) name = name[..version];
                if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0) throw new InvalidDataException("Unsafe ISO entry name.");
                var relative = parent.Length == 0 ? name : parent + "/" + name;
                if (!paths.Add(relative)) throw new InvalidDataException("Duplicate ISO entry path: " + relative);
                var entry = new IsoEntry(relative, extent, size, (record[25] & 2) != 0, (long)sector * SectorSize + offset);
                iso.Entries.Add(entry); if (iso.Entries.Count > 100000) throw new InvalidDataException("ISO entry limit exceeded.");
                if (entry.Directory) Directory(extent, size, relative, depth + 1);
                offset += count;
            }
        }
        Directory(Bin.U32LE(primary, 158), Bin.U32LE(primary, 166), "", 0);
        return iso;
    }
    public IByteSource Open(IsoEntry entry) => new FileRangeByteSource(SourcePath, (long)entry.Sector * SectorSize, entry.Length);

    /// <summary>Patch a new image, append growing extents, and update ISO9660 plus the game's decoded PS2DIR index.</summary>
    public void Export(string destination, IReadOnlyDictionary<string, byte[]> replacements, Action<string>? progress = null)
    {
        destination = System.IO.Path.GetFullPath(destination);
        if (destination.Equals(SourcePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a new output ISO; the source image is read-only.");
        if (File.Exists(destination)) throw new IOException("The output ISO already exists. Choose a new filename.");
        if (HasUdf) throw new NotSupportedException("UDF bridge images require synchronized UDF metadata; ISO9660-only export is supported.");
        var files = Entries.Where(e => !e.Directory).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var patches = new Dictionary<string, byte[]>(replacements, StringComparer.OrdinalIgnoreCase);
        if (patches.Keys.Any(k => !files.ContainsKey(k))) throw new InvalidDataException("A replacement does not exist in the ISO.");
        var end = new FileInfo(SourcePath).Length;
        var layout = files.ToDictionary(k => k.Key, v => (Sector: v.Value.Sector, Length: v.Value.Length), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, bytes) in patches)
        {
            var entry = files[key];
            if (bytes.LongLength > ((entry.Length + 2047L) / SectorSize) * SectorSize)
            { layout[key] = (checked((uint)(end / SectorSize)), checked((uint)bytes.Length)); end = checked(end + (bytes.LongLength + 2047) / SectorSize * SectorSize); }
            else layout[key] = (entry.Sector, checked((uint)bytes.Length));
        }
        if (end > 2295104L * SectorSize) throw new NotSupportedException("Output exceeds single-layer DVD capacity; dual-layer layout rebuilding is unsupported.");
        var index = files.Values.FirstOrDefault(e => e.Path.Equals("PS2DIR.BIN", StringComparison.OrdinalIgnoreCase));
        if (index is not null)
        {
            if (patches.ContainsKey(index.Path)) throw new InvalidOperationException("PS2DIR.BIN is regenerated from the ISO layout; remove manual edits to it first.");
            var data = Open(index).ReadAll();
            if (data.Length < 16) throw new InvalidDataException("Truncated PS2DIR index.");
            uint count = Bin.U32LE(data, 0), table = Bin.U32LE(data, 8);
            if (count > 10000 || (long)table + count * 16L > data.Length) throw new InvalidDataException("Invalid PS2DIR index table.");
            string Text(uint at)
            {
                if (at < 16 || at >= data.Length) throw new InvalidDataException("Invalid PS2DIR string pointer.");
                return Bin.CString(data, (int)at);
            }
            var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint i = 0; i < count; i++)
            {
                int at = checked((int)(table + i * 16)); var name = Text(Bin.U32LE(data, at)); var directory = Text(Bin.U32LE(data, at + 12));
                int version = name.LastIndexOf(';'); if (version >= 0) name = name[..version];
                int colon = directory.IndexOf(':'); if (colon >= 0) directory = directory[(colon + 1)..];
                string key = (directory.Trim('/', '\\') + "/" + name).TrimStart('/').Replace('\\', '/');
                if (layout.TryGetValue(key, out var target))
                { if (patches.ContainsKey(key)) { BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 4), target.Sector); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 8), target.Length); } mapped.Add(key); }
            }
            if (patches.Keys.Any(k => !mapped.Contains(k))) throw new InvalidDataException("A changed file is absent from PS2DIR.BIN; runtime lookup cannot be updated.");
            patches[index.Path] = data;
        }
        // Detect shared extents before patching their bytes in place.
        foreach (var key in patches.Keys)
        {
            var entry = files[key];
            if (entry.Length > 0 && (entry.Sector < 18 || _directories.Any(d => (long)d.Sector * SectorSize < (long)entry.Sector * SectorSize + entry.Length && (long)entry.Sector * SectorSize < (long)d.Sector * SectorSize + d.Length)))
                throw new InvalidDataException("File overlaps ISO filesystem metadata: " + key);
            if (files.Values.Any(other => other.Path != entry.Path && other.Length > 0 && entry.Length > 0 && (long)other.Sector * SectorSize < (long)entry.Sector * SectorSize + entry.Length && (long)entry.Sector * SectorSize < (long)other.Sector * SectorSize + other.Length))
                throw new NotSupportedException("Overlapping/shared ISO extents need alias-aware rebuilding: " + key);
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            progress?.Invoke("Copying source ISO"); File.Copy(SourcePath, temporary, overwrite: false);
            using (var output = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                output.SetLength(end);
                foreach (var (key, bytes) in patches)
                {
                    progress?.Invoke("Writing " + key); var entry = files[key]; var target = layout[key];
                    output.Position = (long)target.Sector * SectorSize; output.Write(bytes);
                    output.Position = entry.RecordOffset + 2; WriteBoth(output, target.Sector);
                    output.Position = entry.RecordOffset + 10; WriteBoth(output, target.Length);
                }
                foreach (var descriptor in VolumeDescriptors) { output.Position = descriptor + 80; WriteBoth(output, checked((uint)(end / SectorSize))); }
                output.Flush(true);
            }
            progress?.Invoke("Verifying ISO entries"); var reopened = Read(temporary);
            foreach (var (key, bytes) in patches)
            {
                var entry = reopened.Entries.Single(e => e.Path.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (!reopened.Open(entry).ReadAll().AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("ISO verification failed: " + key);
            }
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void WriteBoth(Stream output, uint value)
    {
        Span<byte> both = stackalloc byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(both, value); BinaryPrimitives.WriteUInt32BigEndian(both[4..], value); output.Write(both);
    }
}
