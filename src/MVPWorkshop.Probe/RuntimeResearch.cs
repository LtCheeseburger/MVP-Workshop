using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

// Read-only research artifacts. No codec or runtime binding is enabled by these experiments.
internal static class RuntimeResearch
{
    private const string AuditedXboxHash = "264E0ECC4E6C33A32670B1F1F4CF1884D7D7FB3DE4A5BCFB6C37F00B12FEF96B";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Speech(string Asset, long StreamOffset, int Codec, int Version, int Rate, int Channels, long Samples,
        int Blocks, int PayloadBytes, long BlockSamples, bool Loop, string[] Headers);
    private sealed record Sample(string Asset, string File, int Codec, int Version, int Rate, int Samples, int PayloadBytes);

    public static void Run(AssetNode root, string output)
    {
        if (ModExporter.OutputProblem(root, output) is { } why) throw new InvalidOperationException(why);
        Directory.CreateDirectory(output);
        // Avoid rebuilding thousands of unrelated loose texture packs just to inspect audio/database data.
        var top = root.Children.Where(n => n.Name.Equals("data", StringComparison.OrdinalIgnoreCase)).SelectMany(n => n.Children).Concat(root.Children).Distinct().ToList();
        var branches = top.Where(n => n.Name.Equals("audio", StringComparison.OrdinalIgnoreCase) || n.Name.Equals("database", StringComparison.OrdinalIgnoreCase) || n.Name.Equals("database.big", StringComparison.OrdinalIgnoreCase) || n.Name.Equals("dbmisc.big", StringComparison.OrdinalIgnoreCase));
        var nodes = branches.SelectMany(n => n.EnumerateDeep()).Concat(root.Children.Where(n => !n.IsFolder && n.Extension is ".xbe" or ".82" or ".73")).Where(n => !n.IsFolder).Distinct().ToList();
        Console.WriteLine($"Discovered {nodes.Count} audio/database/executable nodes.");
        var speech = new List<Speech>(); var samples = new List<Sample>(); var errors = new List<string>();
        var pairs = nodes.Where(n => n.Extension == ".hdr").Select(n => (Header: n, Stream: SpeechPairs.StreamFor(n))).Where(p => p.Stream is not null).ToList();
        var taken = new HashSet<string>();
        foreach (var node in nodes.Where(n => n.Format == FormatId.SchlStream).OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = node.ReadAll(); var source = new MemoryByteSource(bytes);
                foreach (var stream in SchlStream.ScanAll(source))
                {
                    if (stream.Header is not { Codec2: 4 or 22 } h) continue;
                    var blocks = stream.Blocks.Where(b => b.Id == "SCDl").ToList();
                    int payload = blocks.Sum(b => checked((int)b.Size) - 12 - h.Channels * 4 - h.Channels);
                    long blockSamples = blocks.Sum(b => (long)(stream.LittleEndianBlocks ? Bin.U32LE(bytes, (int)(stream.Offset + b.Offset + 8)) : Bin.U32BE(bytes, (int)(stream.Offset + b.Offset + 8))));
                    var ancestors = new List<AssetNode>(); for (var parent = node; parent is not null; parent = parent.Parent) ancestors.Add(parent);
                    var headers = pairs.Where(p => ancestors.Any(a => a.Key == p.Stream!.Key)).Select(p => p.Header.Key).ToArray();
                    speech.Add(new(node.Key, stream.Offset, h.Codec2, h.Version, h.SampleRate, h.Channels, h.Samples, blocks.Count, payload, blockSamples,
                        h.Get(0x86) is > 0 || h.Get(0x87) is > 0 || h.Get(0x1A) is > 0, headers));
                    // One example per speech archive/codec/version, bounded to twelve real utterances.
                    string family = (ancestors.FirstOrDefault(a => a.Name.EndsWith(".big", StringComparison.OrdinalIgnoreCase))?.Key ?? node.Parent?.Key) + ":" + h.Codec2 + ":" + h.Version;
                    if (samples.Count < 12 && h.Channels == 1 && blockSamples is > 432 and < 220500 && taken.Add(family))
                    {
                        var decoded = stream.Decode(source); string file = $"speech-{samples.Count:D2}";
                        File.WriteAllBytes(Path.Combine(output, file + ".wav"), AudioCodecs.ToWav(decoded));
                        File.WriteAllBytes(Path.Combine(output, file + ".schl"), bytes.AsSpan((int)stream.Offset, (int)stream.Length).ToArray());
                        samples.Add(new(node.Key, file, h.Codec2, h.Version, h.SampleRate, decoded.Pcm.Length, payload));
                    }
                }
                node.DropCache();
            }
            catch (Exception ex) { errors.Add(node.Key + ": " + ex.Message); }
        }
        File.WriteAllText(Path.Combine(output, "speech.json"), JsonSerializer.Serialize(speech, Json));
        File.WriteAllText(Path.Combine(output, "samples.json"), JsonSerializer.Serialize(samples, Json));
        var exe = GameExecutable.Find(nodes);
        object? stadium = null;
        if (exe is not null)
        {
            var bytes = exe.ReadAll();
            File.WriteAllText(Path.Combine(output, "executable-sha256.txt"), exe.Key + " " + Convert.ToHexString(SHA256.HashData(bytes)));
            try
            {
                var map = StadiumAssignments.Read(bytes);
                var roster = nodes.First(n => n.Name.Equals("roster.bin", StringComparison.OrdinalIgnoreCase) && n.Key.Contains("database.big/", StringComparison.OrdinalIgnoreCase));
                var teams = TeamRoster.Read(roster.ReadAll(), TeamRoster.IsXbox(roster));
                if (TeamRoster.IsXbox(roster) && Convert.ToHexString(SHA256.HashData(bytes)) == AuditedXboxHash)
                {
                    var data = roster.ReadAll();
                    File.WriteAllText(Path.Combine(output, "team-audio-candidates.json"), JsonSerializer.Serialize(teams.Select(t => new { t.Name, t.Nickname, t.Offset,
                        SchoolAudioId = (Bin.U32LE(data, t.Offset + 160) >> 11) & 511,
                        NicknameAudioId = (Bin.U32LE(data, t.Offset + 160) >> 20) & 511 }), Json));
                }
                stadium = new { map.Count, map.FilenameTableOffset, map.MapTableOffset, map.Codes, map.Definitions,
                    Assignments = teams.Select(t => new { t.Name, t.LogoId, t.StadiumId, RecordOffset = t.Offset, Resolved = Enumerable.Range(0, 3).Select(time => map.Resolve(t.StadiumId, time)) }),
                    UnassignedEnums = Enumerable.Range(0, map.Count).Except(teams.Select(t => t.StadiumId)).ToArray() };
            }
            catch (Exception ex) { errors.Add("Stadium: " + ex.Message); }
            if (bytes.AsSpan().StartsWith("XBEH"u8)) Xrefs(bytes, output);
        }
        File.WriteAllText(Path.Combine(output, "stadium.json"), JsonSerializer.Serialize(stadium, Json));
        var report = new StringBuilder($"# Runtime binding research\n\nSource: `{root.DiskPath}`\n\n");
        report.AppendLine($"MicroTalk leaf streams: {speech.Count}; paired header records: {pairs.Count}; unpaired HDR: {nodes.Count(n => n.Extension == ".hdr") - pairs.Count}.");
        foreach (var group in speech.GroupBy(s => new { s.Codec, s.Version, s.Rate, s.Channels }))
            report.AppendLine($"- {group.Key}: {group.Count()} streams, {group.Sum(s => s.PayloadBytes)} payload bytes, {group.Sum(s => s.BlockSamples)} samples/channel; header/sample-total mismatches {group.Count(s => s.Samples != s.BlockSamples)}; loop-tagged {group.Count(s => s.Loop)}.");
        report.AppendLine("\n## Source lookup rows (evidence, not runtime ownership)\n");
        foreach (var n in nodes.Where(n => n.Name.Equals("schoolnameaudio.csv", StringComparison.OrdinalIgnoreCase) || n.Name.Equals("nicknameaudio.csv", StringComparison.OrdinalIgnoreCase)))
        {
            report.AppendLine($"### {n.Key}\n\n```text\n{Encoding.Latin1.GetString(n.ReadAll())}\n```\n");
        }
        report.AppendLine("## Events\n");
        foreach (var n in nodes.Where(n => n.Extension == ".evt"))
            report.AppendLine($"- `{n.Key}`: {n.StoredSize} bytes; first 64 bytes `{Convert.ToHexString(n.Open().ReadBytes(0, (int)Math.Min(64, n.StoredSize)))}`. Fields remain uninterpreted.");
        report.AppendLine("\n## Errors\n" + string.Join("\n", errors));
        File.WriteAllText(Path.Combine(output, "report.md"), report.ToString());
        Console.WriteLine($"{speech.Count} MicroTalk streams; {samples.Count} sampled utterances; {errors.Count} errors. Reports: {Path.GetFullPath(output)}");
    }

    private static void Xrefs(byte[] bytes, string output)
    {
        var xbe = XbeFile.Read(bytes); var result = new List<object>();
        File.WriteAllText(Path.Combine(output, "sections.json"), JsonSerializer.Serialize(xbe.Sections, Json));
        foreach (var str in FormatInspector.ExtractStrings(bytes, 4).Where(s => new[] { "body", "schoolnameaudio", "nicknameaudio", "stadium", "nname", "sname", "Events.evt", "u%03d", "f%03d" }.Any(term => s.Text.Contains(term, StringComparison.OrdinalIgnoreCase))))
        {
            var section = xbe.Sections.FirstOrDefault(s => str.Offset >= s.RawAddress && str.Offset - s.RawAddress < s.RawSize);
            if (section is null) continue;
            uint address = section.VirtualAddress + (uint)str.Offset - section.RawAddress;
            var refs = new List<object>();
            foreach (var code in xbe.Sections)
                for (int at = (int)code.RawAddress; at + 4 <= Math.Min(bytes.Length, code.RawAddress + code.RawSize); at++)
                    if (Bin.U32LE(bytes, at) == address) refs.Add(new { Offset = at, Address = code.VirtualAddress + at - code.RawAddress, Section = code.Name });
            result.Add(new { str.Text, str.Offset, Address = address, References = refs });
        }
        File.WriteAllText(Path.Combine(output, "string-xrefs.json"), JsonSerializer.Serialize(result, Json));
        var calls = new List<object>();
        foreach (uint target in Convert.ToHexString(SHA256.HashData(bytes)) == AuditedXboxHash ? new uint[] { 0x4D19A0, 0x4D19D0, 0x4D19B0, 0x4D19E0 } : [])
            foreach (var code in xbe.Sections.Where(s => s.Name is ".text" or "db_sec" or "fe_sec"))
                for (int at = (int)code.RawAddress; at + 5 <= Math.Min(bytes.Length, code.RawAddress + code.RawSize); at++)
                    if (bytes[at] == 0xE8 && unchecked(code.VirtualAddress + (uint)at - code.RawAddress + 5 + Bin.U32LE(bytes, at + 1)) == target)
                        calls.Add(new { Target = target, Address = code.VirtualAddress + (uint)at - code.RawAddress, Offset = at });
        File.WriteAllText(Path.Combine(output, "native-calls.json"), JsonSerializer.Serialize(calls, Json));
    }

    public static void Slice(AssetNode root, uint address, int size, string output)
    {
        if (ModExporter.OutputProblem(root, output) is { } why) throw new InvalidOperationException(why);
        var bytes = root.Children.Single(n => n.Name.Equals("default.xbe", StringComparison.OrdinalIgnoreCase)).ReadOriginal();
        var section = XbeFile.Read(bytes).Sections.Single(s => address >= s.VirtualAddress && address - s.VirtualAddress < s.RawSize);
        long offset = section.RawAddress + address - section.VirtualAddress;
        if (size < 1 || size > 1024 * 1024 || size > bytes.Length - offset || size > section.RawSize - (address - section.VirtualAddress)) throw new ArgumentOutOfRangeException(nameof(size));
        File.WriteAllBytes(output, bytes.AsSpan((int)offset, size).ToArray());
    }

    public static void References(AssetNode root, string output)
    {
        if (ModExporter.OutputProblem(root, output) is { } why) throw new InvalidOperationException(why);
        Directory.CreateDirectory(output);
        Xrefs(root.Children.Single(n => n.Name.Equals("default.xbe", StringComparison.OrdinalIgnoreCase)).ReadOriginal(), output);
    }

    public static void Study(AssetNode root, string directory, string encoder, string decoder, bool rev3)
    {
        if (ModExporter.OutputProblem(root, directory) is { } why) throw new InvalidOperationException(why);
        var samples = JsonSerializer.Deserialize<List<Sample>>(File.ReadAllText(Path.Combine(directory, "samples.json")))!;
        var results = new List<object>();
        var nativeResults = new List<object>();
        foreach (var sample in samples)
        {
            string native = Path.Combine(directory, sample.File + ".schl"), pcm = native + ".pcm";
            Exec(decoder, native, pcm, sample.Version == 3 ? "--rev3" : "--standard");
            var expected = AudioReplacement.ReadPcmWav(File.ReadAllBytes(Path.Combine(directory, sample.File + ".wav"))).Pcm;
            var raw = File.ReadAllBytes(pcm);
            int delta = 0;
            if (raw.Length == expected.Length * 2) for (int i = 0; i < expected.Length; i++) delta = Math.Max(delta, Math.Abs(expected[i] - Bin.S16LE(raw, i * 2)));
            nativeResults.Add(new { sample.Asset, sample.Version, SameSampleCount = raw.Length == expected.Length * 2, MaxDelta = delta });
        }
        File.WriteAllText(Path.Combine(directory, "native-reference.json"), JsonSerializer.Serialize(nativeResults, Json));
        foreach (var sample in samples.Take(6))
        foreach (int bitrate in new[] { 16000, 24000, 32000, 48000, 64000 })
        {
            string input = Path.Combine(directory, sample.File + ".wav"), encoded = Path.Combine(directory, sample.File + $"-{bitrate}{(rev3 ? "-rev3" : "")}.utk"), pcm = encoded + ".pcm";
            var frameLog = Exec(encoder, "-q", "-f", sample.Codec == 4 ? "-H" : "-F", "-b", bitrate.ToString(), input, encoded);
            Exec(decoder, encoded, pcm, rev3 ? "--rev3" : "--standard");
            var original = AudioReplacement.ReadPcmWav(File.ReadAllBytes(input)).Pcm;
            var raw = File.ReadAllBytes(pcm); if (raw.Length != original.Length * 2) throw new InvalidDataException("Independent sample count mismatch.");
            var compressed = File.ReadAllBytes(encoded); var own = new MicroTalkDecoder(rev3); own.SetBuffer(compressed, 32, compressed.Length);
            int maxDelta = 0, clipped = 0; double signal = 0, error = 0, dot = 0, norm = 0;
            for (int offset = 0; offset < original.Length; offset += 432)
            {
                own.DecodeFrame();
                for (int i = 0; i < Math.Min(432, original.Length - offset); i++)
                {
                    int x = original[offset + i], y = Bin.S16LE(raw, (offset + i) * 2);
                    maxDelta = Math.Max(maxDelta, Math.Abs(y - MicroTalkDecoder.ToPcm(own.Output[i])));
                    signal += (double)x * x; error += (double)(x - y) * (x - y); dot += (double)x * y; norm += (double)y * y;
                    if (y is short.MinValue or short.MaxValue) clipped++;
                }
            }
            int? overCapacity = null; bool? repackVerified = null, framingPreserved = null;
            if (rev3)
            {
                var ends = frameLog.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s.Trim())).ToArray();
                var native = File.ReadAllBytes(Path.Combine(directory, sample.File + ".schl"));
                var stream = SchlStream.ScanAll(new MemoryByteSource(native)).Single();
                var blocks = stream.Blocks.Where(b => b.Id == "SCDl").ToList();
                int expectedFrames = blocks.Sum(b => (checked((int)Bin.U32LE(native, (int)b.Offset + 8)) + 431) / 432);
                // No interior partial frame may be silently discarded or shifted into another block.
                if (expectedFrames == ends.Length && blocks.SkipLast(1).All(b => Bin.U32LE(native, (int)b.Offset + 8) % 432 == 0))
                {
                    overCapacity = 0; int frameAt = 0, encodedAt = 32; var patched = native.ToArray();
                    foreach (var block in blocks)
                    {
                        int at = (int)block.Offset, count = (checked((int)Bin.U32LE(native, at + 8)) + 431) / 432;
                        int end = ends[frameAt + count - 1], length = end - encodedAt, capacity = (int)block.Size - 17;
                        if (length > capacity) overCapacity++;
                        else { patched.AsSpan(at + 17, capacity).Clear(); compressed.AsSpan(encodedAt, length).CopyTo(patched.AsSpan(at + 17)); }
                        frameAt += count; encodedAt = end;
                    }
                    if (overCapacity == 0)
                    {
                        framingPreserved = patched.Length == native.Length && stream.Blocks.All(b => native.AsSpan((int)b.Offset, b.Id == "SCDl" ? 17 : (int)b.Size).SequenceEqual(patched.AsSpan((int)b.Offset, b.Id == "SCDl" ? 17 : (int)b.Size)));
                        string repacked = encoded + ".schl";
                        File.WriteAllBytes(repacked, patched); Exec(decoder, repacked, repacked + ".pcm", "--rev3");
                        repackVerified = File.ReadAllBytes(repacked + ".pcm").AsSpan().SequenceEqual(raw);
                    }
                }
            }
            results.Add(new { sample.Asset, Bitrate = bitrate, Bytes = compressed.Length - 32, NativePayloadBytes = sample.PayloadBytes,
                FitsTotalAllocationOnly = compressed.Length - 32 <= sample.PayloadBytes, IndependentDecoderMaxDelta = maxDelta,
                NativeBlocksOverCapacity = overCapacity, NativeRepackMatchesIndependentPcm = repackVerified,
                NativeFramingPreserved = framingPreserved,
                SnrDb = 10 * Math.Log10(Math.Max(1, signal) / Math.Max(1, error)), Correlation = dot / Math.Sqrt(Math.Max(1, signal * norm)), Clipped = clipped });
        }
        File.WriteAllText(Path.Combine(directory, rev3 ? "encoding-study-rev3.json" : "encoding-study.json"), JsonSerializer.Serialize(results, Json));
        Console.WriteLine($"{results.Count} independent encoding experiments. Total-size fit does not establish native per-block or loop compatibility.");
    }

    private static string Exec(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(tool)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { p.Kill(true); throw new TimeoutException(tool); }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{tool} exited {p.ExitCode}: {stderr.GetAwaiter().GetResult()}");
        return stdout.GetAwaiter().GetResult();
    }
}
