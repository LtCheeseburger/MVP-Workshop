using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Formats;

public sealed record StructureRegion(long Offset, long Length, string Name)
{
    public string Label => $"0x{Offset:X} +0x{Length:X} · {Name}";
}

/// <summary>Bounded source-byte regions. Unknown bytes retain raw labels, never guessed semantics.</summary>
public static class AssetStructure
{
    public static IReadOnlyList<StructureRegion> Read(FormatId format, IByteSource source)
    {
        var rows = new List<StructureRegion>();
        void Add(long offset, long length, string label) { if (offset >= 0 && length > 0 && offset < source.Length && length <= source.Length - offset) rows.Add(new(offset, length, label)); }
        Add(0, Math.Min(16, source.Length), "File header / signature");
        try
        {
            switch (format)
            {
                case FormatId.QklCache:
                    var qkl = QklFile.Read(source); Add(0, qkl.DataOffset, "QKL header and source/cache tables");
                    foreach (var record in qkl.Records) Add(record.PointerOffset - 8, 12, $"{record.FileName} · type {record.Kind} · entry {record.EntryIndex} · cache @0x{record.Offset:X}");
                    break;
                case FormatId.TerfArchive:
                    foreach (var block in TerfArchive.Read(source)) Add(block.Offset, block.Size, block.Tag + " block");
                    foreach (var entry in TerfArchive.ReadDirectory(source).Entries) { Add(entry.DirectoryOffset, 8, $"Entry {entry.Index} directory"); Add(entry.Offset, entry.Size, $"Entry {entry.Index} (compression {entry.Compression})"); }
                    break;
                case FormatId.MmapTexture:
                    var mmap = MmapTexture.Read(source.ReadAll());
                    foreach (var image in mmap.Images) Add(image.Offset, image.Length, image.Label);
                    foreach (var palette in mmap.Palettes) Add(palette.Offset, palette.Length, "BGRA palette");
                    break;
                case FormatId.TiburonModel:
                    foreach (var section in TiburonModel.Read(source)) Add(section.Offset, section.Length, section.Tag + $" section ({section.Count} records)");
                    break;
                case FormatId.TiburonDatabase:
                    var db = TiburonDatabase.Read(source.ReadAll());
                    foreach (var table in db.Tables) { Add(table.Offset, 40, table.Name + " header"); Add(table.Offset + 40, table.Fields.Count * 16, table.Name + " field definitions"); Add(table.DataOffset, table.End - table.DataOffset, table.Name + " records and string data"); }
                    break;
                case FormatId.TeamRoster:
                    Add(0, 4, "Team count");
                    foreach (var team in TeamRoster.Read(source.ReadAll())) { Add(team.Offset, 196, team.Name + " team prefix"); Add(team.Offset + 164, 8, team.Name + " packed assignment words"); Add(team.Offset + 196, team.PlayerCount * 8, team.Name + " player slots"); Add(team.Offset + 196 + team.PlayerCount * 8, 291, team.Name + " remaining record fields (undecoded)"); }
                    break;
                case FormatId.SpeechHeader:
                    Add(0, 2, "Record word"); Add(2, 2, "Kind / segment fields"); Add(4, 4, "Link / selector"); Add(8, 4, "u16 fields (units unknown)"); Add(12, 6, "Unknown fixed header fields"); Add(18, source.Length - 18, "Payload (variant-dependent; partially undecoded)"); break;
                case FormatId.SpeechEvents:
                    var events = SpeechEvents.Read(source.ReadAll()); Add(0, 20, "EVT fixed header"); Add(20, events.TableOffset - 20, "Speech script bytecode (undecoded)");
                    foreach (var entry in events.Events) { Add(entry.RecordOffset, 32, "Event directory record " + entry.Index); Add(entry.NameOffset, entry.Name.Length + 1, entry.Name + " (directory name)"); } break;
                case FormatId.BigArchive:
                    var big = BigArchive.Read(source); Add(16, big.HeaderSize - 16L, "BIG directory"); foreach (var entry in big.Entries.Take(10000)) Add(entry.Offset, entry.Size, entry.Name); break;
                case FormatId.SshTexture:
                    var ssh = SshFile.Read(source.ReadAll());
                    foreach (var image in ssh.Images.Take(10000)) { Add(image.DirOffset, 8, image.Tag + " directory entry"); Add(image.Header.Offset, 16, image.Tag + " image header"); Add(image.DataOffset, image.DataLength, image.Tag + " pixels"); foreach (var attachment in image.Attachments) Add(attachment.Header.Offset, attachment.Header.BlockSize, attachment.Kind); } break;
                case FormatId.Elf or FormatId.ElfFragment:
                    var elf = ElfFile.Read(source.ReadAll()); Add(0, 52, "ELF32 header"); foreach (var section in elf.Sections.Take(10000)) Add(section.Offset, section.Size, section.Name + " section"); break;
                case FormatId.DdsImage: Add(0, Math.Min(128, source.Length), "DDS header"); Add(128, source.Length - 128, "Texture payload (DX10 header, if present, precedes pixels)"); break;
                case FormatId.XboxExecutable:
                    var xbe = XbeFile.Read(source.ReadAll()); foreach (var section in xbe.Sections.Take(10000)) Add(section.RawAddress, section.RawSize, section.Name + " section"); break;
                case FormatId.SchlStream or FormatId.SchlContainer:
                    foreach (var stream in SchlStream.ScanAll(source, 10000)) foreach (var block in stream.Blocks.Take(10000)) Add(stream.Offset + block.Offset, block.Size, block.Id); break;
                case FormatId.EaBank:
                    var bank = EaBank.Read(source.ReadAll()); foreach (var sound in bank.Sounds.Take(10000)) Add(sound.HeaderOffset, Math.Max(1, sound.Header.EndOffset - sound.HeaderOffset), "Sound " + sound.Index + " PT patch header"); break;
                case FormatId.SonyHd:
                    foreach (var chunk in FormatInspector.WalkChunks(source, reversed: true, sizeAt: 8)) Add(chunk.Offset, chunk.Size, chunk.Id); break;
                default:
                    foreach (var chunk in FormatInspector.WalkChunks(source).Take(10000)) Add(chunk.Offset, chunk.Size, chunk.Id + " chunk"); break;
            }
        }
        catch { /* Metadata view reports parse errors; retain valid, bounded regions and raw bytes. */ }
        return rows.OrderBy(r => r.Offset).ThenBy(r => r.Length).ToArray();
    }
}
