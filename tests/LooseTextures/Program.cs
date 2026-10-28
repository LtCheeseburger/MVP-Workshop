using System.Diagnostics;
using MVPWorkshop.Core.Formats;

// LooseTextures check <game dir>   every .xsh splits into a folder and builds back byte-identical (through the DDS files)
// LooseTextures split <game dir>   replaces each .xsh that splits with its folder, in place (a test install only)
if (args.Length != 2 || args[0] is not ("check" or "split"))
{
    Console.Error.WriteLine("usage: LooseTextures check|split <game dir>");
    return 2;
}
var split = args[0] == "split";
int files = 0, folders = 0, kept = 0, bad = 0, dds = 0, raw = 0;
long bytes = 0;
var sw = Stopwatch.StartNew();
var buildTime = TimeSpan.Zero;
foreach (var path in Directory.EnumerateFiles(args[1], "*.xsh", SearchOption.AllDirectories))
{
    var d = File.ReadAllBytes(path);
    files++;
    bytes += d.Length;
    var parts = XshFolder.Split(d);
    if (parts == null)
    {
        kept++;
        continue;
    }
    // build from the serialized files, as the runtime does
    var t = Stopwatch.StartNew();
    var back = XshFolder.Build(parts[XshFolder.MetaName], n => parts.GetValueOrDefault(n), out var error);
    buildTime += t.Elapsed;
    if (back == null || !back.AsSpan().SequenceEqual(d))
    {
        if (bad++ < 10) Console.WriteLine($"MISMATCH {path}: {error ?? "bytes differ"}");
        continue;
    }
    folders++;
    dds += parts.Keys.Count(k => k.EndsWith(".dds"));
    raw += parts.Keys.Count(k => k.EndsWith(".bin"));
    if (!split) continue;
    var tmp = path + ".tmp";
    Directory.CreateDirectory(tmp);
    foreach (var (name, data) in parts) File.WriteAllBytes(Path.Combine(tmp, name), data);
    File.Delete(path);
    Directory.Move(tmp, path);
}
Console.WriteLine($"{files} .xsh ({bytes / 1048576} MB): {folders} split ({dds} DDS, {raw} raw), {kept} kept as files, {bad} mismatched");
Console.WriteLine($"{sw.Elapsed.TotalSeconds:F1} s total, {buildTime.TotalSeconds:F1} s building");
return bad == 0 ? 0 : 1;
