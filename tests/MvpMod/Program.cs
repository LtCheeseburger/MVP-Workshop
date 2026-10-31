using System.IO.Compression;
using System.Security.Cryptography;
using System.Buffers.Binary;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Install;
using MVPWorkshop.Core.Vfs;

var folder = Path.Combine(Path.GetTempPath(), "mvpmod-roundtrip-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
int failures = 0;
void Check(string label, bool value) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); if (!value) failures++; }
try
{
    var game = Path.Combine(folder, "game");
    Directory.CreateDirectory(Path.Combine(game, "sample_big"));
    File.WriteAllBytes(Path.Combine(game, "default.xbe"), [0, 1, 2, 3]);
    File.WriteAllText(Path.Combine(game, "sample_big", "_big.json"), "{}");
    File.WriteAllText(Path.Combine(game, "sample_big", "a.txt"), "original");
    var texture = new byte[48];
    "SHPX"u8.CopyTo(texture); BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(4), texture.Length);
    BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(8), 1); "logo"u8.CopyTo(texture.AsSpan(16));
    BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(20), 24);
    BinaryPrimitives.WriteInt32LittleEndian(texture.AsSpan(24), (24 << 8) | 0x60);
    BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(28), 4); BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(30), 4);
    Check("synthetic loose texture fixture", LooseInstall.WriteTexturePack(texture, Path.Combine(game, "logo.xsh"), out _));
    var original = Directory.GetFiles(game, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
    var root = GameFileSystem.OpenGame(game);
    var session = new EditSession(root, Path.Combine(folder, "output"));
    var asset = root.Resolve("sample.big/a.txt")!;
    session.Commit(asset, [5, 4, 3, 2], "test edit");
    session.AddFile("sample_big/new.txt", [7, 8, 9]);
    var editedTexture = (byte[])texture.Clone(); editedTexture[^1] = 42;
    session.Commit(root.Resolve("logo.xsh")!, editedTexture, "texture edit");
    session.AddFile("new-logo.xsh", editedTexture);
    var info = new ModInfo { Id = "roundtrip", Name = "Round trip", Version = "1.2.3", Author = "Author", Description = "Description", AffectsSaves = true };
    var archive = Path.Combine(folder, "first.mvpmod");
    Check("export synthetic mod", MvpModExporter.Export(session, info, archive, false).Ok);
    var package = MvpModImporter.Read(archive);
    Check("metadata preserved", package.Info.Id == info.Id && package.Info.Name == info.Name && package.Info.Version == info.Version && package.Info.Author == info.Author && package.Info.Description == info.Description && package.Info.AffectsSaves);
    Check("XBE hash matches", package.MatchesGame(root));
    var reopened = MvpModImporter.Open(package, root, session.OutputFolder);
    Check("overrides and partial DDS-folder mods round-trip", reopened.Overrides.Count == session.Overrides.Count && session.Overrides.All(f => reopened.Overrides[f.Key].SequenceEqual(f.Value)));
    Check("Added files and complete texture folders round-trip", reopened.Added.Count == session.Added.Count && session.Added.All(f => reopened.Added[f.Key].SequenceEqual(f.Value)));
    var second = Path.Combine(folder, "second.mvpmod");
    Check("re-export imported mod", MvpModExporter.Export(reopened, package.Info, second, false).Ok);
    var again = MvpModImporter.Read(second);
    Check("export/open/export loose files identical", package.Files.Count == again.Files.Count && package.Files.All(f => again.Files[f.Key].SequenceEqual(f.Value)));
    Check("next version increments patch", MvpModImporter.NextVersion("1.2.3") == "1.2.4");
    var incompleteLayout = new MvpModPackage { Info = info };
    incompleteLayout.Files["logo.xsh/xsh.meta"] = File.ReadAllBytes(Path.Combine(game, "logo.xsh", XshFolder.MetaName));
    var overlayBefore = AssetNode.Overlay;
    bool layoutRejected = false;
    try { MvpModImporter.Open(incompleteLayout, root, session.OutputFolder); } catch (InvalidDataException) { layoutRejected = true; }
    Check("replacement texture metadata requires its own complete image set", layoutRejected && AssetNode.Overlay == overlayBefore);
    var invalid = Path.Combine(folder, "unsafe.mvpmod");
    using (var zip = ZipFile.Open(invalid, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("files/../../outside.txt").Open()); writer.Write("unsafe"); }
    bool rejected = false;
    try { MvpModImporter.Read(invalid); } catch (InvalidDataException) { rejected = true; }
    Check("unsafe paths rejected before extraction", rejected && !File.Exists(Path.Combine(folder, "outside.txt")));
    Check("game installation unchanged", original.All(f => SHA256.HashData(File.ReadAllBytes(f.Key)).SequenceEqual(f.Value)) && Directory.GetFiles(game, "*", SearchOption.AllDirectories).Length == original.Count);
}
catch (Exception ex) { Console.WriteLine("FAIL " + ex); failures++; }
finally { Directory.Delete(folder, true); }
return failures == 0 ? 0 : 1;
