using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.IO;

int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
void U(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
void Both(byte[] data, int at, uint value) { U(data, at, value); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at + 4), value); }
byte[] Big(string name, byte[] contents)
{
    var result = new byte[64 + contents.Length]; "BIGF"u8.CopyTo(result);
    U(result, 4, (uint)result.Length); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), 1); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), 64);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), 64); BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20), (uint)contents.Length); Encoding.ASCII.GetBytes(name).CopyTo(result, 24); contents.CopyTo(result, 64); return result;
}
byte[] Texture()
{
    var data = new byte[24 + 16 + 1024 + 16 + 1024]; "SHPS"u8.CopyTo(data); U(data, 4, (uint)data.Length); U(data, 8, 1); "TEST"u8.CopyTo(data.AsSpan(16)); U(data, 20, 24);
    data[24] = 2; var block = 16 + 1024; data[25] = (byte)block; data[26] = (byte)(block >> 8);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 32); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(30), 32);
    data[24 + block] = 0x21; BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24 + block + 4), 256); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24 + block + 6), 1); return data;
}
byte[] Index(int fileLength)
{
    var data = new byte[256]; U(data, 0, 1); U(data, 8, 16); U(data, 16, 40); U(data, 20, 24); U(data, 24, (uint)fileLength); U(data, 28, 64);
    Encoding.ASCII.GetBytes("PACK.BIG;1\0").CopyTo(data, 40); Encoding.ASCII.GetBytes("cdrom0:\\DATA\0").CopyTo(data, 64); return data;
}
int Record(byte[] target, int at, string name, uint sector, uint size, bool directory)
{
    var text = name is "." ? new byte[] { 0 } : name is ".." ? new byte[] { 1 } : Encoding.ASCII.GetBytes(name);
    int length = (33 + text.Length + 1) & ~1; target[at] = (byte)length; Both(target, at + 2, sector); Both(target, at + 10, size); target[at + 25] = directory ? (byte)2 : (byte)0;
    target[at + 28] = 1; target[at + 31] = 1; target[at + 32] = (byte)text.Length; text.CopyTo(target, at + 33); return at + length;
}
var folder = Path.Combine(AppContext.BaseDirectory, "fixtures", "iso-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
var sourcePath = Path.Combine(folder, "source.iso"); var outputPath = Path.Combine(folder, "edited.iso");
var originalTexture = Texture(); var archive = Big("inner.big", Big("image.ssh", originalTexture));
byte[] disc = new byte[64 * 2048];
int pvd = 16 * 2048; disc[pvd] = 1; "CD001"u8.CopyTo(disc.AsSpan(pvd + 1)); disc[pvd + 6] = 1; Both(disc, pvd + 80, 64); disc[pvd + 129] = 8; disc[pvd + 130] = 8;
Record(disc, pvd + 156, ".", 20, 2048, true);
int terminator = 17 * 2048; disc[terminator] = 255; "CD001"u8.CopyTo(disc.AsSpan(terminator + 1)); disc[terminator + 6] = 1;
int row = 20 * 2048; row = Record(disc, row, ".", 20, 2048, true); row = Record(disc, row, "..", 20, 2048, true); row = Record(disc, row, "DATA", 21, 2048, true); Record(disc, row, "PS2DIR.BIN;1", 30, 256, false);
row = 21 * 2048; row = Record(disc, row, ".", 21, 2048, true); row = Record(disc, row, "..", 20, 2048, true); row = Record(disc, row, "PACK.BIG;1", 24, (uint)archive.Length, false); Record(disc, row, "KEEP.BIN;1", 28, 13, false);
archive.CopyTo(disc, 24 * 2048); "keep original"u8.CopyTo(disc.AsSpan(28 * 2048)); Index(archive.Length).CopyTo(disc, 30 * 2048); File.WriteAllBytes(sourcePath, disc);
var sourceHash = SHA256.HashData(disc);
var game = GameFileSystem.OpenIso(sourcePath); var session = new EditSession(game, Path.Combine(folder, "mod"));
var texture = game.EnumerateDeep().Single(n => n.Name == "image.ssh"); Check(session.SaveBlocker(texture) is null, "nested ISO archive editable");
var ssh = SshFile.Read(texture.ReadAll()); var pixels = new byte[64 * 64 * 4]; for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 80; pixels[i + 2] = 120; pixels[i + 3] = 255; }
var resized = SshWriter.ReplaceImage(ssh, ssh.Images[0], pixels, 64, 64, allowResize: true);
session.Commit(texture, resized.File, "retain native 2x upscale");
var project = Path.Combine(folder, "edit.mvpwproj"); ProjectFile.Save(session, project);
session = ProjectFile.Load(project, GameFileSystem.OpenIso(sourcePath), out var warnings); Check(warnings.Count == 0, "ISO project save/reload");
IsoModExporter.Export(session, outputPath);
var exported = GameFileSystem.OpenIso(outputPath); var exportedTexture = exported.EnumerateDeep().Single(n => n.Name == "image.ssh"); var result = SshFile.Read(exportedTexture.ReadAll());
Check(result.Images[0].Header.Width == 64 && result.Images[0].Header.Height == 64 && result.Decode(result.Images[0]).Bgra.SequenceEqual(pixels), "higher resolution survives nested BIG and ISO export");
Check(SHA256.HashData(File.ReadAllBytes(sourcePath)).SequenceEqual(sourceHash), "source ISO remains byte-identical");
var iso = exported.DiscImage!; var pack = iso.Entries.Single(e => e.Path == "DATA/PACK.BIG"); Check(pack.Sector >= 64, "growing archive relocated to appended sectors");
var index = exported.Children.Single(n => n.Name == "PS2DIR.BIN").ReadAll(); Check(Bin.U32LE(index, 20) == pack.Sector && Bin.U32LE(index, 24) == pack.Length, "PS2DIR LBA and length updated");
Check(iso.Open(iso.Entries.Single(e => e.Path == "DATA/KEEP.BIN")).ReadAll().SequenceEqual("keep original"u8.ToArray()), "untouched ISO file retained");
var mod = ModExporter.Export(session, Path.Combine(folder, "mod")); Check(mod.Ok && mod.Files.Count == 1 && File.Exists(Path.Combine(folder, "mod", "DATA", "PACK.BIG")), "ISO project also exports extracted mod files");
var extracted = Path.Combine(folder, "extracted"); Directory.CreateDirectory(Path.Combine(extracted, "DATA")); File.WriteAllBytes(Path.Combine(extracted, "DATA", "PACK.BIG"), archive);
var extractedGame = GameFileSystem.OpenFolder(extracted); var extractedSession = new EditSession(extractedGame, Path.Combine(folder, "folder-mod")); var extractedTexture = extractedGame.EnumerateDeep().Single(n => n.Name == "image.ssh"); extractedSession.Commit(extractedTexture, resized.File, "upscale");
var folderIso = Path.Combine(folder, "folder-edited.iso"); IsoModExporter.Export(extractedSession, folderIso, sourcePath); Check(GameFileSystem.OpenIso(folderIso).EnumerateDeep().Single(n => n.Name == "image.ssh").ReadAll().SequenceEqual(resized.File), "extracted-folder edits export through original ISO template");
bool rejected = false; try { iso.Export(outputPath, new Dictionary<string, byte[]>()); } catch (InvalidOperationException) { rejected = true; } Check(rejected, "source ISO overwrite rejected");
rejected = false; try { IsoImage.Read(sourcePath).Export(outputPath, new Dictionary<string, byte[]>()); } catch (IOException) { rejected = true; } Check(rejected, "existing output ISO overwrite rejected");
var bad = (byte[])disc.Clone(); bad[21 * 2048 + 1] = 1; File.WriteAllBytes(Path.Combine(folder, "bad.iso"), bad); rejected = false; try { IsoImage.Read(Path.Combine(folder, "bad.iso")); } catch (NotSupportedException) { rejected = true; } Check(rejected, "unsupported extended directory layout rejected");
var udf = (byte[])disc.Clone(); "BEA01"u8.CopyTo(udf.AsSpan(32 * 2048 + 1)); var udfPath = Path.Combine(folder, "udf.iso"); File.WriteAllBytes(udfPath, udf); rejected = false;
try { IsoImage.Read(udfPath).Export(Path.Combine(folder, "udf-output.iso"), new Dictionary<string, byte[]>()); } catch (NotSupportedException) { rejected = true; }
Check(rejected && !File.Exists(Path.Combine(folder, "udf-output.iso")), "UDF bridge export rejected before writing output");
var mutated = (byte[])archive.Clone(); mutated[^1] ^= 1; File.WriteAllBytes(Path.Combine(extracted, "DATA", "PACK.BIG"), mutated); rejected = false;
try { IsoModExporter.Export(extractedSession, Path.Combine(folder, "mismatched.iso"), sourcePath); } catch (InvalidDataException) { rejected = true; }
Check(rejected && !File.Exists(Path.Combine(folder, "mismatched.iso")), "mismatched extracted-folder ISO template rejected");
Check(File.ReadAllBytes(outputPath).AsSpan(0, 16 * 2048).SequenceEqual(disc.AsSpan(0, 16 * 2048)), "boot/system area retained byte-for-byte");
Console.WriteLine($"{checks} ISO checks passed; fixtures: {folder}");
