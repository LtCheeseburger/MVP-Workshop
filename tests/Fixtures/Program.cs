using System.Buffers.Binary;
using System.Text;
using MVPWorkshop.Core.Install;

if (args.Length != 1) throw new ArgumentException("Fixtures <new empty output directory>");
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidOperationException("Fixture output must be empty.");
Directory.CreateDirectory(output);
void Text(string path, string text) { var full = Path.Combine(output, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text); }
void Bytes(string path, byte[] bytes) { var full = Path.Combine(output, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, bytes); }
var texture = new byte[48]; "SHPX"u8.CopyTo(texture);
BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(4), texture.Length); BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(8), 1);
"logo"u8.CopyTo(texture.AsSpan(16)); BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(20), 24);
BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(24), (24 << 8) | 0x60);
BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(28), 4); BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(30), 4);
Bytes("explorer/default.xbe", [0, 1, 2, 3]); Text("explorer/sample_big/_big.json", "{}");
Text("explorer/sample_big/a.txt", "original a\n"); Text("explorer/sample_big/b.txt", "original b\n");
if (!LooseInstall.WriteTexturePack(texture, Path.Combine(output, "explorer/sample_big/logo0.xsh"), out var error)) throw new InvalidDataException(error);
var roster = new byte[4 + 495 * 2]; BinaryPrimitives.WriteInt32LittleEndian(roster, 2);
for (int i = 0; i < 2; i++)
{
    int at = 4 + i * 495;
    Encoding.ASCII.GetBytes("School " + i).CopyTo(roster, at + 8); Encoding.ASCII.GetBytes("T" + i).CopyTo(roster, at + 56);
    Encoding.ASCII.GetBytes("Nickname").CopyTo(roster, at + 112);
    BinaryPrimitives.WriteInt32LittleEndian(roster.AsSpan(at + 192), 1); BinaryPrimitives.WriteInt32LittleEndian(roster.AsSpan(at + 196), i + 1);
}
const string attributes = "0 first_name,1 last_name,2 playerattrib_jerseynum,5 playerattrib_primaryposition,13 playerattrib_skintone,18 playerattrib_bodytype,35 playerattrib_audioid\n00000001,0 Alpha,1 Player,2 1,5 1,13 0,18 0,35 1\n00000002,0 Beta,1 Player,2 2,5 1,13 0,18 0,35 2\n";
foreach (var (game, database) in new[] { ("tasks", "database_big"), ("players", "DATABASE.BIG") })
{
    Bytes(game + "/default.xbe", [0, 1, 2, 3]); Bytes(game + "/" + database + "/roster.bin", roster); Text(game + "/" + database + "/attrib.dat", attributes);
}
Text("tasks/database_big/_big.json", "{}");
Bytes("install-source/default.xbe", [0, 1, 2, 3]); Bytes("install-source/logo.xsh", texture); Text("install-source/readme.txt", "Synthetic installation fixture.");
Console.WriteLine("Synthetic fixtures: " + output);
