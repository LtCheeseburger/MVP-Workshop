using System.Diagnostics;
using MVPWorkshop.Core.Install;

// Install disc <image>              opens an Xbox image and lists its size
// Install install <image|folder> <game dir>   installs in the loose layout (resumes a cancelled run)
// Install verify <game dir>         checks every file against manifest.json
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Install disc <image> | install <image|folder> <game dir> | verify <game dir>");
    return 2;
}
var sw = Stopwatch.StartNew();
switch (args[0])
{
    case "disc":
    {
        using var disc = XboxDisc.Open(args[1], out var error);
        if (disc == null) { Console.WriteLine($"not opened: {error}"); return 1; }
        var files = disc.Files().ToList();
        Console.WriteLine($"{files.Count} files, {files.Sum(f => (long)f.File.Size) / 1048576} MB; default.xbe {(disc.Find("default.xbe") != null ? "found" : "missing")}");
        foreach (var (path, f) in files.Take(8)) Console.WriteLine($"  {path} {f.Size}");
        break;
    }
    case "install":
    {
        IGameSource src;
        XboxDisc? disc = null;
        if (Directory.Exists(args[1])) src = new FolderSource(args[1]);
        else
        {
            disc = XboxDisc.Open(args[1], out var error);
            if (disc == null) { Console.WriteLine($"not opened: {error}"); return 1; }
            src = new DiscSource(disc);
        }
        var last = 0L;
        var progress = new Progress<LooseInstaller.Progress>(p =>
        {
            var pct = p.Done * 100 / Math.Max(1, p.Total);
            if (pct >= last + 10) { last = pct; Console.WriteLine($"  {pct}% {p.Current}"); }
        });
        new LooseInstaller(src, args[2]).Install(new Dictionary<string, string> { ["from"] = src.Name }, progress, CancellationToken.None);
        disc?.Dispose();
        var n = LooseInstaller.ReadManifest(args[2], out _).Count;
        Console.WriteLine($"installed {n} files in {sw.Elapsed.TotalSeconds:F1} s");
        break;
    }
    case "verify":
    {
        var bad = LooseInstaller.Verify(args[1], null, CancellationToken.None);
        Console.WriteLine($"{bad.Count} missing or changed in {sw.Elapsed.TotalSeconds:F1} s");
        foreach (var r in bad.Take(10)) Console.WriteLine($"  {r.Path}");
        return bad.Count == 0 ? 0 : 1;
    }
}
return 0;
