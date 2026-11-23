using System.Buffers.Binary;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.Editing;

int failures = 0;
if (args.Contains("--ui-audit"))
{
    var root = GameFileSystem.OpenFolder(args[0]);
    int movies = 0, shapes = 0, images = 0; var kinds = new Dictionary<int, int>();
    foreach (var movie in root.EnumerateDeep().Where(n => n.Format == FormatId.AptMovie))
    {
        var resources = NascarAptResources.Read(movie); movies++;
        var constants = movie.Parent!.Children.FirstOrDefault(n => n.Format == FormatId.AptConst);
        var apt = AptFile.Read(movie.ReadAll(), constants is null ? 0 : AptConstants.Read(constants.ReadAll()).MovieOffset);
        foreach (var resource in resources)
        {
            if (resource.Kind == 0)
            {
                try { foreach (var draw in NascarAptShape.Read(resource.Node.ReadAll())) kinds[draw.Kind] = kinds.GetValueOrDefault(draw.Kind) + 1; shapes++; }
                catch (Exception ex) { Console.WriteLine("FAIL " + resource.Node.Key + " " + ex.Message); failures++; }
            }
            else if (resource.Node.Format == FormatId.MmapTexture) images++;
            else Console.WriteLine("OTHER " + resource.Kind + " " + resource.Node.Key + " " + resource.Node.Format);
        }
        if (resources.Count == 0 && movie.Parent.Children.Count > 3) { Console.WriteLine("FAIL resource mapping " + movie.Key); failures++; }
    }
    Console.WriteLine($"UI {movies} movies; {shapes} shapes; {images} image packs; fills " + string.Join(",", kinds.Select(p => p.Key + ":" + p.Value)));
    return failures == 0 ? 0 : 1;
}
Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".tmp", "nascar"));
void Check(string label, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label}"); if (!ok) failures++; }
void U32(byte[] b, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), value);
void U16(byte[] b, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(offset), value);
byte[] Pack(params byte[][] entries)
{
    var b = new byte[512 + entries.Sum(e => (e.Length + 127) / 128 * 128)];
    "TERF"u8.CopyTo(b); U32(b, 4, 128); U16(b, 12, 128); U16(b, 14, (ushort)entries.Length);
    "DIR1"u8.CopyTo(b.AsSpan(128)); U32(b, 132, 128);
    "COMP"u8.CopyTo(b.AsSpan(256)); U32(b, 260, 128);
    "DATA"u8.CopyTo(b.AsSpan(384)); U32(b, 388, (uint)(b.Length - 384));
    int offset = 512;
    for (int i = 0; i < entries.Length; i++) { U32(b, 136 + i * 8, (uint)(offset - 384)); U32(b, 140 + i * 8, (uint)entries[i].Length); U32(b, 268 + i * 8, (uint)entries[i].Length); entries[i].CopyTo(b, offset); offset += (entries[i].Length + 127) / 128 * 128; }
    return b;
}
// One named PAL8 texture, a 4x4 base and a 2x2 mip sharing a 256-colour palette.
var mmap = new byte[1600]; "MMAP"u8.CopyTo(mmap); U32(mmap, 4, 2); U16(mmap, 12, 1); U16(mmap, 14, 2); U32(mmap, 16, 1);
U32(mmap, 20, 1504); U32(mmap, 24, 40); U32(mmap, 28, 80); U32(mmap, 32, 1488);
U16(mmap, 40, 4); U16(mmap, 42, 4); U32(mmap, 44, 11); U32(mmap, 48, 16); U32(mmap, 52, 128);
U16(mmap, 56, 2); U16(mmap, 58, 2); U32(mmap, 60, 11); U32(mmap, 64, 4); U32(mmap, 68, 384);
U32(mmap, 80, 1); U32(mmap, 84, 1024); U32(mmap, 88, 320);
"PAINT"u8.CopyTo(mmap.AsSpan(1488)); U16(mmap, 1504, 1); U16(mmap, 1506, 2);
mmap[448 + 2] = 255; mmap[448 + 3] = 255;
var texture = MmapTexture.Read(mmap);
Check("MMAP names, bitmap ownership and shared mip palette", texture.Images.Count == 2 && texture.Images.All(i => i.Name == "PAINT" && i.PaletteIndex == 0));
Check("MMAP PAL8 decode", texture.Decode(texture.Images[0]).Bgra.Chunk(4).All(p => p.SequenceEqual(new byte[] { 0, 0, 255, 255 })));
var aliasFixture = mmap.ToArray(); U16(aliasFixture, 12, 2); U32(aliasFixture, 20, 1540);
mmap.AsSpan(1504, 12).CopyTo(aliasFixture.AsSpan(1540)); mmap.AsSpan(1504, 12).CopyTo(aliasFixture.AsSpan(1552));
aliasFixture.AsSpan(1504, 16).Clear(); "ALIAS"u8.CopyTo(aliasFixture.AsSpan(1504));
var aliasTexture = MmapTexture.Read(aliasFixture);
Check("MMAP track aliases retain both names and shared pixels", aliasTexture.Images.Count == 4 && aliasTexture.Images.Any(i => i.Name == "ALIAS") && aliasTexture.Images[0].Offset == aliasTexture.Images[2].Offset);
Check("shared texture replacement reports every affected name", aliasTexture.SharedPixelFiles(aliasTexture.Images[0]).SequenceEqual(new[] { "ALIAS" }) && aliasTexture.SharedPixelFiles(aliasTexture.Images[2]).SequenceEqual(new[] { "PAINT" }));
var aliasPixels = aliasTexture.Decode(aliasTexture.Images[0]);
var aliasReplacement = MmapTexture.Read(aliasTexture.ReplaceFile(0, aliasPixels.Bgra, aliasPixels.Width, aliasPixels.Height));
Check("shared bitmap replacement keeps aliases readable", aliasReplacement.Decode(aliasReplacement.Images[0]).Bgra.SequenceEqual(aliasReplacement.Decode(aliasReplacement.Images[2]).Bgra));
var green = Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 0, 255, 0, 255 }).ToArray();
var editedTexture = texture.ReplaceFile(0, green, 4, 4);
var afterTexture = MmapTexture.Read(editedTexture);
Check("MMAP replacement regenerates every mip", afterTexture.Images.All(i => afterTexture.Decode(i).Bgra.Chunk(4).All(p => p.SequenceEqual(new byte[] { 0, 255, 0, 255 }))));
Check("MMAP texture edit retains tables and unknown bytes", mmap.AsSpan(0, 256).SequenceEqual(editedTexture.AsSpan(0, 256)) && mmap.AsSpan(1472).SequenceEqual(editedTexture.AsSpan(1472)));
foreach (int length in new[] { 0, 39, 70, 100, 1500 })
{ try { MmapTexture.Read(mmap[..length]); Check($"MMAP rejects truncation {length}", false); } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { Check($"MMAP rejects truncation {length}", true); } }
var original = Pack("hello"u8.ToArray(), "unchanged"u8.ToArray());
var databaseFixture = new byte[116]; "DB\0\x08"u8.CopyTo(databaseFixture); U32(databaseFixture, 8, 116); U32(databaseFixture, 16, 1);
"TEST"u8.CopyTo(databaseFixture.AsSpan(24)); U32(databaseFixture, 36, 38); U32(databaseFixture, 40, 4); U32(databaseFixture, 44, 32); U16(databaseFixture, 52, 1); U16(databaseFixture, 54, 1); databaseFixture[60] = 1; databaseFixture[61] = 1;
U32(databaseFixture, 72, 13); "NAME"u8.CopyTo(databaseFixture.AsSpan(80)); U32(databaseFixture, 84, 128); databaseFixture[92] = 3; "abc"u8.CopyTo(databaseFixture.AsSpan(93)); "PKEY"u8.CopyTo(databaseFixture.AsSpan(96));
var fixtureDb = TiburonDatabase.Read(databaseFixture); var fixtureTable = fixtureDb.Tables[0];
var editedDb = TiburonDatabase.Read(fixtureDb.Set(fixtureTable, 0, fixtureTable.Fields[0], "longer"));
Check("DB variable text growth preserves trailing indexes", editedDb.Get(editedDb.Tables[0], 0, fixtureTable.Fields[0]) == "longer" && editedDb.Data.AsSpan(editedDb.Tables[0].End - 16, 16).SequenceEqual(databaseFixture.AsSpan(96, 16)) && editedDb.CheckChecksums().Count == 0);
var model = new byte[256]; "TMdl"u8.CopyTo(model); U32(model, 4, 256); U16(model, 8, 1);
"Geom"u8.CopyTo(model.AsSpan(16)); U32(model, 20, 32); U32(model, 24, 224);
U32(model, 32, 48); U32(model, 36, 1); U32(model, 40, 68); U32(model, 44, 1);
U32(model, 48, 84); model[64] = 1; U32(model, 68, 128); U16(model, 72, 3); model[74] = 24; U32(model, 76, 18);
U32(model, 84, 1); U32(model, 88, 92); U16(model, 92, 3); U16(model, 108, 0); U16(model, 110, 1); U16(model, 112, 2);
U32(model, 164, BitConverter.SingleToUInt32Bits(1)); U32(model, 192, BitConverter.SingleToUInt32Bits(1));
var modelGeometry = TiburonModel.ReadGeometry(model);
Check("TMdl vertex resources and triangle strip indices", modelGeometry.Meshes.Count == 1 && modelGeometry.Meshes[0].Triangles.SequenceEqual(new[] { 0, 1, 2 }) && modelGeometry.Meshes[0].Positions[1].X == 1);
Check("TMdl OBJ export retains decoded faces", TiburonModel.ToObj(modelGeometry).Contains("f 1/1/1 2/2/2 3/3/3"));
var interleaved = new byte[300]; model.CopyTo(interleaved, 0); interleaved[74] = 40; U32(interleaved, 76, 0x152);
U32(interleaved, 168, BitConverter.SingleToUInt32Bits(0.25f)); U32(interleaved, 172, BitConverter.SingleToUInt32Bits(0.75f));
var interleavedGeometry = TiburonModel.ReadGeometry(interleaved);
Check("TMdl interleaved position/normal/colour/UV stream", interleavedGeometry.Meshes.Count == 1 && interleavedGeometry.Meshes[0].Uv[0] == new System.Numerics.Vector2(0.25f, 0.75f));
Check("MAD movies detected by signature without filename extension", FormatDetector.Detect("00000.bin", new MemoryByteSource("MADk"u8.ToArray())) == FormatId.MadMovie);
var alphaFont = new byte[41]; "SHPS"u8.CopyTo(alphaFont); U32(alphaFont, 4, 41); U32(alphaFont, 8, 1); "font"u8.CopyTo(alphaFont.AsSpan(16)); U32(alphaFont, 20, 24);
alphaFont[24] = 0x7A; U16(alphaFont, 28, 2); U16(alphaFont, 30, 1); alphaFont[40] = 0xF3;
var alphaAtlas = SshFile.Read(alphaFont); var alphaPixels = alphaAtlas.Decode(alphaAtlas.Images[0]).Bgra;
Check("A4 font alpha nibble order and full opacity scale", alphaPixels.SequenceEqual(new byte[] {255, 255, 255, 51, 255, 255, 255, 255}));
if (args.Contains("--track-test"))
{
    int tracks = 0, instances = 0, texturePacks = 0, sceneImages = 0;
    foreach (var path in Directory.EnumerateFiles(args[0], "*.bin"))
    {
        var bytes = File.ReadAllBytes(path);
        var scene = TiburonTrackScene.Read(bytes);
        if (scene is null) continue;
        tracks++; instances += scene.Values.Sum(v => v.Count);
        var embedded = TiburonModel.Read(new MemoryByteSource(bytes)).FirstOrDefault(s => s.Tag == "Text");
        if (embedded is { Length: > 0 })
        {
            var pack = MmapTexture.Read(bytes.AsSpan((int)embedded.Offset, (int)embedded.Length).ToArray());
            foreach (var image in pack.Images.Where(i => i.Mip == 0)) { pack.Decode(image); sceneImages++; }
            texturePacks++;
        }
        Check(Path.GetFileName(path) + " track instances are finite", scene.Values.SelectMany(v => v).All(v => float.IsFinite(v.Transform.M41) && float.IsFinite(v.Transform.M42) && float.IsFinite(v.Transform.M43)));
    }
    Check("all 321 authored instance scenes decode (including 65 track models)", tracks == 321);
    Console.WriteLine($"TRACKS {tracks}; instances {instances}");
    Console.WriteLine($"SCENE TEXTURES {texturePacks} packs; {sceneImages} named base images including aliases");
    Environment.Exit(failures == 0 ? 0 : 1);
}
if (args.Contains("--model-audit"))
{
    var limits = new Dictionary<string, int>(); int meshCount = 0, files = 0;
    foreach (var path in Directory.EnumerateFiles(args[0], "model-*.bin"))
    {
        var decoded = TiburonModel.ReadGeometry(File.ReadAllBytes(path)); files++; meshCount += decoded.Meshes.Count;
        foreach (var issue in decoded.Limitations) { var message = issue[(issue.IndexOf(':') + 2)..]; limits[message] = limits.GetValueOrDefault(message) + 1; }
    }
    Console.WriteLine($"MODEL AUDIT: {files} files; {meshCount} meshes; {limits.Sum(kv => kv.Value)} limitations");
    foreach (var item in limits.OrderByDescending(kv => kv.Value)) Console.WriteLine($"{item.Value}: {item.Key}");
    return failures == 0 ? 0 : 1;
}
if (args.Contains("--model-textures"))
{
    var root = GameFileSystem.OpenFolder(args[0]);
    var car = root.Resolve("CARDATA.DAT/00000.bin")!;
    var models = NascarModelResources.ModelsFor(car);
    Check("paint texture chooses stock car body", models.Count > 0 && models[0].Name == "rcar2850.ea3");
    Check("database paint link identifies Chevrolet", NascarModelResources.VehicleFor(car) is { ModelId: 0, Body: "Chevy" });
    var dodgeTexture = root.Resolve("CARDATA.DAT/00203.bin")!;
    Check("Outtake Intake #110 selects Dodge manufacturer", NascarModelResources.VehicleFor(dodgeTexture) is { ModelId: 1, Body: "Dodge" });
    var modifiedTexture = root.Resolve("CARDATA.DAT/00301.bin")!;
    var modifiedModel = NascarModelResources.ModelsFor(modifiedTexture).First();
    Check("Which-Way #187 selects modified body rather than stock car", modifiedModel.Name == "ModFLite_LOD1.ea3");
    Check("modified chassis uses its own shared atlas", NascarModelResources.Bind(modifiedModel, modifiedTexture)["frame_map"].Source?.Key == "CARDATA.DAT/00529.bin");
    var paceTexture = root.Resolve("CARDATA.DAT/00307.bin")!;
    var paceModel = NascarModelResources.ModelsFor(paceTexture).First();
    Check("yellow checkered paint chooses pace car body", paceModel.Name == "pcar3000.ea3");
    Check("pace car paint is bound to its authored material", NascarModelResources.Bind(paceModel, paceTexture)["pacecar_map"].Source == paceTexture);
    Check("pace car wheels use their authored stock-atlas UV layout", NascarModelResources.Bind(paceModel, paceTexture)["frame_map"].Source?.Key == "CARDATA.DAT/00513.bin");
    var paceAssembly = TiburonCarPreview.Assemble(TiburonModel.ReadGeometry(paceModel.Node.ReadAll()), TiburonModel.ReadParts(paceModel.Node.ReadAll()));
    Check("pace car wheel placement has supporting body geometry", paceAssembly is not null && paceAssembly.Wheels.Values.All(v => v.Length == 4));
    var unmapped = root.Resolve("CARDATA.DAT/00392.bin")!;
    Check("unlinked paint has no guessed default body", NascarModelResources.PreferredModel(unmapped) is null);
    var mustang = NascarModelResources.Get(car).Models.First(m => m.Name == "prd_05FordMustangGT_LOD1.ea3");
    NascarModelResources.RememberModel(unmapped, mustang);
    Check("manual vehicle choice is remembered for this asset", NascarModelResources.PreferredModel(unmapped) == mustang.Name);
    var carBindings = NascarModelResources.Bind(models[0], car);
    Check("paint shader slots use selected DECAL atlas", carBindings["hd_map"].Source == car && carBindings["lf_map"].Source == car && carBindings["rf_map"].Source == car);
    Check("roof and glass use paint atlas rather than the chassis tire atlas", new[] { "wrw_map", "ft_and_lt_bk_ma", "bk_window_map" }.All(slot => carBindings[slot].Source == car));
    Check("shared car trim is separate from paint", carBindings["frame_map"].Image.Name == "UNIVERSAL");
    var carParts = TiburonModel.ReadParts(models[0].Node.ReadAll());
    Check("car body parts remain visible instead of choosing the empty None variant", carParts.Where(p => p.Name.StartsWith("ShapeTub") && p.Variants.Any(v => v.Meshes.Count > 0)).All(p => p.Variants[TiburonModel.DefaultVariant(p)].Meshes.Count > 0));
    var carGeometry = TiburonModel.ReadGeometry(models[0].Node.ReadAll());
    var dodgeGeometry = TiburonModel.ReadGeometry(models[0].Node.ReadAll(), "Dodge");
    var chevyGrill = carGeometry.Meshes.Single(m => m.Index == 63); var dodgeGrill = dodgeGeometry.Meshes.Single(m => m.Index == 63);
    Check("Dodge manufacturer morph uses authored fixed-point panel deltas", Math.Abs(dodgeGrill.Positions[0].X - chevyGrill.Positions[0].X - 555 / 4096f) < .00001f);
    Check("Dodge manufacturer selects its authored UV stream", !dodgeGrill.Uv.SequenceEqual(chevyGrill.Uv));
    Check("manufacturer shape changes do not select damaged IDP fragments", carParts.Where(p => p.Name.StartsWith("Shape") && p.Variants.Any(v => v.Name == "Normal" && v.Meshes.Count > 0)).All(p => p.Variants[TiburonModel.DefaultVariant(p, "Dodge")].Name == "Normal" || p.Variants[TiburonModel.DefaultVariant(p, "Dodge")].Name == "Dodge"));
    var modifiedAssembly = TiburonCarPreview.Assemble(TiburonModel.ReadGeometry(modifiedModel.Node.ReadAll()), TiburonModel.ReadParts(modifiedModel.Node.ReadAll()), openWheel: true);
    Check("modified wheel instances follow suspension endpoints", modifiedAssembly is not null && modifiedAssembly.Front.Z is > 4.5f and < 6 && modifiedAssembly.Rear.Z is > -5 and < -3 && modifiedAssembly.Wheels.Values.All(v => v.Length == 4));
    Console.WriteLine("MODIFIED ASSEMBLY " + (modifiedAssembly is null ? "No supported arch fit" : $"{modifiedAssembly.Front} / {modifiedAssembly.Rear}"));
    var assembly = TiburonCarPreview.Assemble(carGeometry, carParts);
    Check("stock car preview fits both authored fender arches", assembly is not null && assembly.Front.Evidence >= 8 && assembly.Rear.Evidence >= 8 && assembly.Front.Z is > 5 and < 5.6f && assembly.Rear.Z is > -4.2f and < -3.5f);
    Check("wheel mesh has four mirrored instances away from model origin", assembly is not null && assembly.Wheels.Count > 0 && assembly.Wheels.Values.All(matrices => matrices.Length == 4 && matrices.All(m => Math.Abs(m.M43) > 3 && Math.Abs(m.M41) > 1) && matrices.Count(m => m.M11 < 0) == 2));
    Check("unplaced damage and flap meshes are excluded from static assembly", carParts.Where(p => p.Name == "HIDP" || p.Name.StartsWith("RoofFlap")).All(TiburonCarPreview.IsUnplacedPart));
    Check("wheel placement refuses geometry without supporting wheel arches", TiburonCarPreview.Assemble(new TiburonModel.Geometry([], []), carParts) is null);
    var body = root.Resolve("CHARDAT.DAT/00680.bin")!;
    var drivers = NascarModelResources.ModelsFor(body);
    Check("driver texture chooses its adjacent authored model", drivers[0].Node.Key == "CHARDAT.DAT/00678.bin");
    var driverBindings = NascarModelResources.Bind(drivers[0], body);
    Check("driver torso and arms use selected body", driverBindings["torso_file"].Source == body && driverBindings["arm_file"].Source == body);
    Check("driver face comes from its matching resource group", driverBindings["face_file"].Source?.Key == "CHARDAT.DAT/00679.bin");
    var parts = TiburonModel.ReadParts(drivers[0].Node.ReadAll());
    Check("model part alternatives retain authored mesh indices", parts.Single(p => p.Name == "BodySize").Variants.Single(v => v.Name == "Medium").Meshes.SequenceEqual(new[] { 27, 26, 28 }));
    var posed = TiburonModel.ReadGeometry(drivers[0].Node.ReadAll());
    Check("driver bone-palette preview produces finite transformed geometry", posed.Meshes.Count >= 28 && posed.Meshes.SelectMany(m => m.Positions).All(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)) && posed.Meshes.Single(m => m.Index == 3).Positions[0].Y > 1);
    var driverVertices = posed.Meshes.SelectMany(m => m.Triangles.Select(i => m.Positions[i])).ToArray();
    float driverHeight = driverVertices.Max(p => p.Y) - driverVertices.Min(p => p.Y);
    Check("driver centimetre skeleton matches foot-space body proportions", driverHeight > 5 && driverHeight < 7);
    Check("driver draw-group palettes retain torso and leg geometry", posed.Meshes.Count >= 29 && !posed.Limitations.Any(l => l.Contains("palette", StringComparison.OrdinalIgnoreCase)));

    Check("optional model parts default to None rather than stacking variants", TiburonModel.DefaultVariant(new TiburonModel.Part("Shades", [new("None", []), new("Shades1", [1])])) == 0);
    var originalOverlay = AssetNode.Overlay;
    var bytes = body.ReadAll(); var file = MmapTexture.Read(bytes); var image = file.Images.First(i => i.Name == "TORSO" && i.Mip == 0); var decoded = file.Decode(image);
    var previewGreen = Enumerable.Range(0, decoded.Width * decoded.Height).SelectMany(_ => new byte[] { 0, 255, 0, 255 }).ToArray();
    var changed = file.ReplaceFile(image.Index, previewGreen, decoded.Width, decoded.Height);
    try
    {
        AssetNode.Overlay = n => ReferenceEquals(n, body) ? changed : originalOverlay?.Invoke(n);
        var live = NascarModelResources.Bind(drivers[0], body)["torso_file"];
        Check("3D texture binding reads current project edits", live.File.Decode(live.Image).Bgra.AsSpan().SequenceEqual(previewGreen));
    }
    finally { AssetNode.Overlay = originalOverlay; }
    Check("2D/3D preview does not write source files", body.ReadOriginal().AsSpan().SequenceEqual(bytes));
    Console.WriteLine($"MODEL TEXTURES car={carBindings.Count}; driver={driverBindings.Count}; compatible car models={models.Count}; driver models={drivers.Count}");
    return failures == 0 ? 0 : 1;
}
if (args.Contains("--media-test"))
{
    var root = GameFileSystem.OpenFolder(args[0]);
    var output = Path.GetFullPath(".tmp/nascar/media"); Directory.CreateDirectory(output);
    int movies = 0, fonts = 0, apt = 0;
    foreach (var node in root.EnumerateDeep())
    {
        if (node.Format == FormatId.MadMovie)
        {
            var path = Path.Combine(output, "input.mad"); File.WriteAllBytes(path, node.ReadAll());
            var start = new System.Diagnostics.ProcessStartInfo("ffmpeg.exe", ["-v", "error", "-i", path, "-frames:v", "1", "-f", "null", "-"]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(true); process.WaitForExit(); Check("movie timeout " + node.Key, false); }
            else Check("MAD first-frame decode " + node.Key, process.ExitCode == 0);
            if (process.ExitCode != 0) Console.WriteLine(error.GetAwaiter().GetResult());
            File.Delete(path); movies++;
        }
        if (node.Format == FormatId.EaFontSfn) { var font = SfnFont.Read(node.ReadAll()); var atlas = font.Atlas.Decode(font.Atlas.Images[0]); Check("font atlas " + node.Key, atlas.Bgra.Any(v => v != 0)); fonts++; }
        if (node.Format == FormatId.AptMovie && node.Parent?.Format == FormatId.TerfArchive)
        {
            var constants = node.Parent.Children.Where(n => n.Format == FormatId.AptConst).ToArray();
            if (constants.Length == 1) { var pool = AptConstants.Read(constants[0].ReadAll()); AptFile.Read(node.ReadAll(), pool.MovieOffset); apt++; }
        }
        if (!node.IsContainer) node.DropCache();
    }
    Console.WriteLine($"MEDIA AUDIT movies={movies}; fonts={fonts}; paired APT={apt}; failures={failures}");
    return failures == 0 ? 0 : 1;
}
var directory = TerfArchive.ReadDirectory(new MemoryByteSource(original));
Check("TERF directory entry offsets", directory.Entries.Count == 2 && directory.Entries[1].Offset == 640);
var grown = TerfWriter.Write(original, new Dictionary<int, byte[]> { [0] = new byte[300] });
var grownDirectory = TerfArchive.ReadDirectory(new MemoryByteSource(grown));
Check("TERF growth keeps untouched offsets and payloads", grownDirectory.Entries[1] == directory.Entries[1] && grown.AsSpan(640, 9).SequenceEqual("unchanged"u8));
Check("TERF growth is aligned and stores replacements uncompressed", grownDirectory.Entries[0].Offset % 128 == 0 && grownDirectory.Entries[0].Size == 300 && grownDirectory.Entries[0].Compression == 0);
var corrupt = original.ToArray(); U32(corrupt, 136, uint.MaxValue);
try { TerfArchive.ReadDirectory(new MemoryByteSource(corrupt)); Check("TERF rejects escaping offsets", false); } catch (InvalidDataException) { Check("TERF rejects escaping offsets", true); }
var scratch = Path.Combine(Path.Combine(Environment.CurrentDirectory, ".tmp"), "nascar-workshop-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    var source = Path.Combine(scratch, "game"); Directory.CreateDirectory(source);
    File.WriteAllBytes(Path.Combine(source, "nested.dat"), Pack(Pack("text"u8.ToArray()), "preserve"u8.ToArray()));
    var root = GameFileSystem.OpenFolder(source); var inner = root.Resolve("nested.dat/00000.bin/00000.bin")!;
    Check("nested TERF entries resolve and allow saved edits", inner is not null && inner.WriteBlocker is null);
    var session = new EditSession(root, Path.Combine(scratch, "mod")); session.Commit(inner!, "longer replacement text"u8.ToArray(), "nested test");
    var report = ModExporter.Export(session, session.OutputFolder);
    foreach (var problem in report.Problems) Console.WriteLine("EXPORT ERROR " + problem);
    Check("nested TERF rebuild and archive verification", report.Ok && report.Files.Single().ComparedAssets >= 4);
    var exported = GameFileSystem.OpenFolder(session.OutputFolder);
    Check("nested exported edit matches", exported.Resolve("nested.dat/00000.bin/00000.bin")?.ReadAll().SequenceEqual("longer replacement text"u8.ToArray()) == true);
    Check("game source stays byte-identical", root.Resolve("nested.dat/00000.bin/00000.bin")!.ReadOriginal().AsSpan().SequenceEqual("text"u8));
}
finally { Directory.Delete(scratch, true); }

if (args.Length > 0 && args.Contains("--catalog-test"))
{
    var root = GameFileSystem.OpenFolder(args[0]); var nodes = root.EnumerateDeep().ToArray();
    var expected = nodes.Where(n => !n.IsFolder && (!n.IsContainer || n.Children.Count == 0)).ToHashSet();
    var keys = expected.ToDictionary(n => n, n => n.Key);
    var catalog = NascarAssetCatalog.Build(nodes);
    Check("NASCAR categories cover every leaf exactly once", catalog.Count == expected.Count && catalog.Select(e => e.Asset).Distinct().Count() == expected.Count && catalog.All(e => expected.Contains(e.Asset)));
    Check("category aliases retain original asset keys and source nodes", catalog.All(e => e.Asset.Key == keys[e.Asset] && e.SourcePack == NascarAssetCatalog.SourceArchive(e.Asset).Name));
    Check("car paint schemes are classified by their source pack", catalog.Any(e => e.SourcePack == "CARDATA.DAT") && catalog.Where(e => e.SourcePack == "CARDATA.DAT").All(e => e.Category.StartsWith("Cars/")));
    Check("driver numbers and track physics have dedicated categories", catalog.Any(e => e.Category == "Drivers/Numbers/Large numbers") && catalog.Any(e => e.Category == "Tracks/Physics and collision"));
    Check("database and quick-load caches stay discoverable", catalog.Any(e => e.Category == "Databases/Main racing database") && catalog.Any(e => e.Category == "System/Quick-load caches"));
    var namedTexture = catalog.First(e => e.Asset.Format == FormatId.MmapTexture && e.Label.Contains("×"));
    Check("friendly texture labels include dimensions and stable source entry IDs", namedTexture.Label.Contains(" · ") && namedTexture.Asset.Key == keys[namedTexture.Asset]);
    Check("search includes category, asset kind and original path", NascarAssetCatalog.SearchText(namedTexture.Asset).Contains(namedTexture.Category) && NascarAssetCatalog.SearchText(namedTexture.Asset).Contains(namedTexture.Asset.Path));
    var report = catalog.GroupBy(e => e.Category).Select(g => $"{g.Key}: {g.Count():N0}");
    File.WriteAllLines(".tmp/nascar/catalog.txt", report); Console.WriteLine($"CATALOG {catalog.Count:N0} assets; {catalog.Select(e => e.Category).Distinct().Count()} categories; {catalog.Count(e => e.Category.StartsWith("Other game data/"))} unclassified");
}

if (args.Length > 0 && args.Contains("--db-test"))
{
    var path = Path.Combine(args[0], "NASCAR.DB"); var bytes = File.ReadAllBytes(path); var db = TiburonDatabase.Read(bytes);
    var drivers = db.Tables.Single(t => t.Name == "DRVR"); var name = drivers.Fields.Single(f => f.Name == "FNAM");
    var changed = TiburonDatabase.Read(db.Set(drivers, 0, name, "Workshop"));
    Check("driver name edit and complete DB checksum chain", changed.Get(changed.Tables[drivers.Index], 0, name) == "Workshop" && changed.CheckChecksums().Count == 0);
    var huffmanTable = db.Tables.First(t => (t.Allocation & 64) != 0 && t.Rows > 0 && t.Fields.Any(f => f.Type == 13));
    var textField = huffmanTable.Fields.First(f => f.Type == 13); var text = db.Get(huffmanTable, 0, textField);
    var newText = new string(text.Reverse().ToArray());
    var rewritten = TiburonDatabase.Read(db.Set(huffmanTable, 0, textField, newText));
    Check("Huffman text edit relocates table data and updates checksums", rewritten.Get(rewritten.Tables[huffmanTable.Index], 0, textField) == newText && rewritten.CheckChecksums().Count == 0);
    Check("variable text edit preserves every other record/field", db.Tables.All(t => Enumerable.Range(0, t.Rows).All(r => t.Fields.All(f => t.Index == huffmanTable.Index && r == 0 && f == textField || db.Get(t, r, f) == rewritten.Get(rewritten.Tables[t.Index], r, f)))));
    Check("DB edit never changes source file", bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
}

if (args.Length > 0 && args.Contains("--export-test"))
{
    var root = GameFileSystem.OpenFolder(args[0]);
    var asset = root.Resolve("CARRESFE.DAT/00000.bin")!;
    var textureFile = MmapTexture.Read(asset.ReadAll()); var first = textureFile.Images.First();
    var decoded = textureFile.Decode(first); var pixels = decoded.Bgra.ToArray();
    pixels[0] = 255; pixels[1] = 0; pixels[2] = 255; pixels[3] = 255;
    var replacement = textureFile.ReplaceFile(first.Index, pixels, decoded.Width, decoded.Height);
    var output = Path.Combine(Environment.CurrentDirectory, ".tmp/nascar/real-export");
    var session = new EditSession(root, output); session.Commit(asset, replacement, "MMAP export test");
    var report = ModExporter.Export(session, output, true, Console.WriteLine);
    foreach (var problem in report.Problems) Console.WriteLine("EXPORT ERROR " + problem);
    Check("real compressed TERF texture export with synchronized quick-load caches", report.Ok && report.Files.Any(f => f.RelativePath.EndsWith(".QKL", StringComparison.OrdinalIgnoreCase)));
    var exported = GameFileSystem.OpenFolder(output);
    Check("real exported texture equals project replacement", exported.Resolve(asset.Key)?.ReadAll().SequenceEqual(replacement) == true);
    Check("real source texture stays unchanged", !asset.ReadOriginal().AsSpan().SequenceEqual(replacement));
}

if (args.Length > 0 && !args.Contains("--export-test") && !args.Contains("--db-test") && !args.Contains("--catalog-test"))
{
    var root = GameFileSystem.OpenFolder(args[0]);
    int entries = 0, textures = 0, bitmaps = 0, archives = 0, models = 0, modelMeshes = 0, modelLimits = 0, databases = 0, skeletons = 0, bones = 0, movies = 0;
    var errors = new List<string>();
    foreach (var node in root.EnumerateDeep())
    {
        if (node.IsFolder) continue;
        entries++;
        try
        {
            if (node.Format == FormatId.TerfArchive) { var dir = TerfArchive.ReadDirectory(node.OpenOriginal()); archives++; if (node.Children.Count != dir.Entries.Count) throw new InvalidDataException(node.LoadError ?? "Entry count differs"); }
            if (node.Format == FormatId.MmapTexture) { var tex = MmapTexture.Read(node.ReadAll()); foreach (var image in tex.Images) { tex.Decode(image); bitmaps++; } textures++; }
            if (node.Format == FormatId.TiburonModel) { TiburonModel.Read(node.Open()); var geometry = TiburonModel.ReadGeometry(node.ReadAll()); models++; modelMeshes += geometry.Meshes.Count; modelLimits += geometry.Limitations.Count; }
            if (node.Format == FormatId.QklCache) QklFile.Read(node.Open());
            if (node.Format == FormatId.TiburonSkeleton) { bones += TiburonSkeleton.Read(node.ReadAll()).Count; skeletons++; }
            if (node.Format == FormatId.MadMovie) movies++;
            if (node.Format == FormatId.EaFontSfn) { var font = SfnFont.Read(node.ReadAll()); font.Atlas.Decode(font.Atlas.Images[0]); }
            if (node.Format == FormatId.TiburonDatabase)
            {
                var db = TiburonDatabase.Read(node.ReadAll()); databases++;
                if (db.CheckChecksums().Count != 0) throw new InvalidDataException("DB checksum mismatch");
                foreach (var table in db.Tables) for (int r = 0; r < table.Rows; r++) foreach (var field in table.Fields) db.Get(table, r, field);
                foreach (uint type in new uint[] { 0, 2, 3, 4, 13 })
                {
                    var table = db.Tables.FirstOrDefault(t => t.Rows > 0 && t.Fields.Any(f => f.Type == type));
                    if (table is null) continue;
                    var field = table.Fields.First(f => f.Type == type);
                    string value = db.Get(table, 0, field);
                    var rewritten = TiburonDatabase.Read(db.Set(table, 0, field, value));
                    if (rewritten.CheckChecksums().Count != 0 || rewritten.Get(rewritten.Tables[table.Index], 0, field) != value) throw new InvalidDataException($"DB field type {type} roundtrip differs");
                }
            }
            if (node.LoadError is { } error) throw new InvalidDataException(error);
        }
        catch (Exception ex) { errors.Add(node.Key + ": " + ex.Message); }
        if (entries % 2000 == 0) Console.WriteLine($"Scanned {entries}; archives {archives}; textures {textures}; errors {errors.Count}");
    }
    Console.WriteLine($"REAL files/entries={entries}; archives={archives}; MMAP={textures}; bitmaps/mips={bitmaps}; TMdl={models}; meshes={modelMeshes}; model limitations={modelLimits}; DB={databases}; SKL1={skeletons}; bones={bones}; MAD={movies}; errors={errors.Count}");
    File.WriteAllLines(Path.Combine(Environment.CurrentDirectory, ".tmp/nascar/full-errors.txt"), errors);
    foreach (var error in errors.Take(30)) Console.WriteLine("ERROR " + error);
    Check("real archive, texture and model directory scan", errors.Count == 0 && textures > 8000 && archives > 116);
}
return failures == 0 ? 0 : 1;

