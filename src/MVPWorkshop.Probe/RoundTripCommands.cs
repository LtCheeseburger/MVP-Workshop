using System.Diagnostics;
using System.Security.Cryptography;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Vfs;

/// <summary>Round-trip validation of the BIG writer, RefPack encoder and export pipeline against the real game files.</summary>
static class RoundTripCommands
{
    /// <summary>
    /// roundtrip &lt;game&gt; [--filter text]: for every BIG archive (nested ones included) rewrite it through <see cref="BigWriter"/>
    /// (a) with one entry replaced by itself — must be byte-identical — and (b) with one entry grown — must reopen with the same
    /// entries, padding and alignment, and identical contents everywhere else.
    /// </summary>
    public static int Layout(AssetNode root, string? filter)
    {
        int archives = 0, identical = 0, grownOk = 0, skipped = 0, failed = 0;
        foreach (var n in root.EnumerateDeep())
        {
            if (n.IsFolder || n.Format != FormatId.BigArchive) continue;
            if (filter is not null && !n.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            archives++;
            try
            {
                var data = n.ReadAll();
                var big = BigArchive.Read(new MemoryByteSource(data));
                var pick = big.Entries.FirstOrDefault(e => e.Size > 0);
                if (pick is null) { skipped++; continue; }

                var same = BigWriter.Write(data, big, new Dictionary<int, byte[]> { [pick.Index] = data.AsSpan((int)pick.Offset, (int)pick.Size).ToArray() });
                if (same.AsSpan().SequenceEqual(data)) identical++;
                else { failed++; Console.WriteLine($"IDENTITY MISMATCH {n.Path} ({same.Length} vs {data.Length})"); continue; }

                var grown = data.AsSpan((int)pick.Offset, (int)pick.Size).ToArray().Concat(Enumerable.Repeat((byte)0xA5, 37)).ToArray();
                byte[] edited;
                try { edited = BigWriter.Write(data, big, new Dictionary<int, byte[]> { [pick.Index] = grown }); }
                catch (NotSupportedException ex) { skipped++; Console.WriteLine($"skip {n.Path}: {ex.Message}"); continue; }
                var re = BigArchive.Read(new MemoryByteSource(edited));
                var bad = new List<string>();
                if (re.Entries.Count != big.Entries.Count) bad.Add("entry count");
                if (re.HeaderSize != big.HeaderSize) bad.Add("header size");
                if (!edited.AsSpan(0, (int)big.HeaderSize).SequenceEqual(data.AsSpan(0, (int)big.HeaderSize)) && !HeaderOnlyPatched(data, edited, big)) bad.Add("header bytes beyond offset/size fields");
                if (re.DeclaredSize != edited.Length) bad.Add("declared size");
                var align = BigWriter.InferAlignment(big.Entries);
                foreach (var (o, r) in big.Entries.Zip(re.Entries))
                {
                    if (o.Name != r.Name) { bad.Add($"name {o.Name}"); break; }
                    var want = o.Index == pick.Index ? grown : data.AsSpan((int)o.Offset, (int)o.Size).ToArray();
                    if (!edited.AsSpan((int)r.Offset, (int)r.Size).SequenceEqual(want)) { bad.Add($"content {o.Name}"); break; }
                    if (r.Offset % align != 0 && o.Offset % align == 0 && !BigOverlaps(big)) { bad.Add($"alignment {o.Name}"); break; }
                }
                if (bad.Count == 0) grownOk++;
                else { failed++; Console.WriteLine($"GROWN MISMATCH {n.Path}: {string.Join(", ", bad)}"); }
            }
            catch (Exception ex) { failed++; Console.WriteLine($"ERROR {n.Path}: {ex.Message}"); }
        }
        Console.WriteLine($"layout: archives={archives} identity-ok={identical} grown-ok={grownOk} skipped={skipped} failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static bool BigOverlaps(BigArchive big)
    {
        long end = 0;
        foreach (var e in big.Entries.OrderBy(e => e.Offset)) { if (e.Offset < end) return true; end = Math.Max(end, e.Offset + (long)e.Size); }
        return false;
    }

    private static bool HeaderOnlyPatched(byte[] a, byte[] b, BigArchive big)
    {
        var mask = new bool[big.HeaderSize];
        for (var i = 4; i < 8; i++) mask[i] = true;
        foreach (var e in big.Entries) for (var i = 0; i < 8; i++) mask[e.DirPos + i] = true;
        for (var i = 0; i < mask.Length; i++) if (!mask[i] && a[i] != b[i]) return false;
        return true;
    }

    /// <summary>
    /// reencode &lt;game&gt; [--filter text]: RefPack round trip of every compressed entry, then a full re-encode of each top-level archive
    /// (every compressed entry recompressed, every BIG relaid out) which must reopen with identical logical contents.
    /// </summary>
    public static int Reencode(AssetNode root, string? filter)
    {
        var failed = 0;
        long inBytes = 0, outBytes = 0, entries = 0;
        var sw = Stopwatch.StartNew();
        foreach (var top in root.Children.SelectMany(Flatten).Where(f => !f.IsFolder && f.Format == FormatId.BigArchive))
        {
            if (filter is not null && !top.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var stored = top.ReadStored();
                var rebuilt = ModExporter.RebuildStored(top, new Dictionary<string, byte[]>(), reencodeAll: true);
                var tmp = Path.Combine(Path.GetTempPath(), "mvpworkshop_rt_" + Guid.NewGuid().ToString("N") + Path.GetExtension(top.Name));
                File.WriteAllBytes(tmp, rebuilt);
                try
                {
                    var problems = ArchiveVerifier.Verify(top, GameFileSystem.OpenFile(tmp), new Dictionary<string, byte[]>(), out var compared);
                    entries += compared; inBytes += stored.Length; outBytes += rebuilt.Length;
                    if (problems.Count > 0) { failed++; Console.WriteLine($"FAIL {top.Path}: {string.Join(" | ", problems.Take(5))}"); }
                    else Console.WriteLine($"ok   {top.Path}  {Bin.Size(stored.Length)} → {Bin.Size(rebuilt.Length)}  ({compared:N0} entries)");
                }
                finally { File.Delete(tmp); }
            }
            catch (Exception ex) { failed++; Console.WriteLine($"ERROR {top.Path}: {ex.Message}"); }
        }
        Console.WriteLine($"reencode: entries compared={entries:N0} failures={failed} original={Bin.Size(inBytes)} rebuilt={Bin.Size(outBytes)} time={sw.Elapsed.TotalSeconds:0}s");
        return failed == 0 ? 0 : 1;
    }

    private static IEnumerable<AssetNode> Flatten(AssetNode n)
    {
        if (n.IsFolder) { foreach (var c in n.Children) foreach (var x in Flatten(c)) yield return x; }
        else yield return n;
    }

    /// <summary>
    /// edit-test &lt;game&gt; &lt;virtual/path&gt;... : edit real assets (appends a marker to their bytes), export through <see cref="ModExporter"/> into a temp
    /// folder, verify the rebuilt archives reopen with the edits, and confirm the original game files are unchanged.
    /// </summary>
    public static int EditTest(AssetNode root, string[] paths)
    {
        var game = root.DiskPath!;
        var out_ = Path.Combine(Path.GetTempPath(), "mvpworkshop_mod_" + Guid.NewGuid().ToString("N"));
        var session = new EditSession(root, out_);
        var before = Snapshot(game, paths, root);
        foreach (var p in paths)
        {
            var n = root.Resolve(p) ?? throw new FileNotFoundException(p);
            var bytes = n.ReadAll().Concat("\nEDITED-BY-MVPWORKSHOP"u8.ToArray()).ToArray();
            session.Commit(n, bytes, "test edit");
            Console.WriteLine($"edit {n.Path}: {n.ReadAll().Length} → {bytes.Length} bytes");
        }
        var report = ModExporter.Export(session, out_, verify: true, Console.WriteLine);
        foreach (var f in report.Files) Console.WriteLine($"  wrote {f.RelativePath}  {Bin.Size(f.Size)}  edited={f.EditedAssets} verified entries={f.ComparedAssets}");
        foreach (var p in report.Problems) Console.WriteLine("  PROBLEM " + p);
        var after = Snapshot(game, paths, root);
        var untouched = before == after;
        Console.WriteLine($"original game files unchanged: {untouched}");

        // independent re-open of the output, outside the exporter's own verification
        var okReopen = true;
        foreach (var f in report.Files)
        {
            var node = GameFileSystem.OpenFile(Path.Combine(out_, f.RelativePath));
            foreach (var p in paths)
            {
                var orig = root.Resolve(p)!;
                if (ModExporter.DiskFileOf(orig).Name != node.Name) continue;
                var rel = RelativeWithin(orig, ModExporter.DiskFileOf(orig));
                var again = node.Resolve(rel);
                var want = session.Overrides[orig.Key];
                if (again is null || !again.ReadAll().AsSpan().SequenceEqual(want)) { okReopen = false; Console.WriteLine($"  re-open mismatch {p}"); }
            }
        }
        Console.WriteLine($"re-opened outputs contain the edits: {okReopen}");
        Directory.Delete(out_, true);
        return report.Ok && untouched && okReopen ? 0 : 1;
    }

    private static string RelativeWithin(AssetNode n, AssetNode diskFile)
    {
        var parts = new List<string>();
        for (var p = n; p != diskFile; p = p.Parent!) parts.Insert(0, p.BigIndex is { } i ? $"{p.Name}#{i}" : p.Name);
        return string.Join("/", parts);
    }

    private static string Snapshot(string game, string[] paths, AssetNode root)
    {
        var files = paths.Select(p => ModExporter.DiskFileOf(root.Resolve(p)!).DiskPath!).Distinct();
        return string.Join(";", files.Select(f => f + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));
    }
}

static class SshRoundTrip
{
    /// <summary>
    /// ssh-roundtrip &lt;game&gt; [--filter text]: decode every writable image, re-encode it (existing palette and rebuilt palette), decode again and compare pixels;
    /// everything outside the image's own data must stay byte-identical. A noisy replacement forces the grow path for RefPack images.
    /// </summary>
    public static int Run(AssetNode root, string? filter)
    {
        int files = 0, images = 0, keepOk = 0, rebuildOk = 0, rawIdentical = 0, unsupported = 0, failed = 0, grown = 0, grownOk = 0, quirks = 0;
        var types = new SortedDictionary<string, int>();
        var reasons = new Dictionary<string, int>();
        var minPsnr = (Value: 99.0, Where: "");
        foreach (var n in root.EnumerateDeep())
        {
            if (n.IsFolder || !n.IsTexturePack) continue;
            if (filter is not null && !n.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            SshFile ssh;
            byte[] data;
            try { data = n.ReadAll(); ssh = SshFile.Read(data); } catch { continue; }
            files++;
            foreach (var img in ssh.Images)
            {
                images++;
                var key = $"0x{img.Header.Type:X2} swz={img.Header.FlagSwizzled}";
                types[key] = types.GetValueOrDefault(key) + 1;
                if (SshWriter.Unsupported(ssh, img) is { } why) { unsupported++; reasons[why] = reasons.GetValueOrDefault(why) + 1; continue; }
                try
                {
                    var d = ssh.Decode(img);
                    foreach (var mode in new[] { PaletteMode.KeepExisting, PaletteMode.Rebuild })
                    {
                        if (mode == PaletteMode.Rebuild && (img.Header.Type & 0x7F) is 5 or 0x7D or 0x60 or 0x61 or 0x62) continue;
                        var r = SshWriter.ReplaceImage(ssh, img, d.Bgra, d.Width, d.Height, mode);
                        var re = SshFile.Read(r.File);
                        var d2 = re.Decode(re.Images[img.Index]);
                        var dxt = (img.Header.Type & 0x7F) is 0x60 or 0x61 or 0x62;
                        var psnr = dxt ? Psnr(d.Bgra, d2.Bgra) : 99;
                        if (psnr < minPsnr.Value) minPsnr = (psnr, $"{n.Path} {img.Tag}");
                        if (dxt ? psnr < MinDxtPsnr : !SamePixels(d.Bgra, d2.Bgra, mode == PaletteMode.Rebuild))
                        {
                            if (OutsidePalette(data, img)) { quirks++; goto next; }   // source pixels index past the palette; they decode as transparent black and cannot be reproduced exactly
                            failed++; Console.WriteLine($"PIXEL MISMATCH {n.Path} {img.Tag} {mode} type=0x{img.Header.Type:X2} {d.Width}x{d.Height} psnr={Psnr(d.Bgra, d2.Bgra):0.0}"); goto next;
                        }
                        // everything except this image's own pixels and palette block must be unchanged
                        var diff = Enumerable.Range(0, Math.Min(data.Length, r.File.Length)).Count(i => data[i] != r.File[i] && !Covered(img, i));
                        if (r.File.Length != data.Length || diff > 0) { failed++; Console.WriteLine($"COLLATERAL {n.Path} {img.Tag} {mode}: {diff} stray byte changes, length {data.Length}→{r.File.Length}"); goto next; }
                        if (mode == PaletteMode.KeepExisting)
                        {
                            keepOk++;
                            if (r.File.AsSpan(img.DataOffset, img.DataLength).SequenceEqual(data.AsSpan(img.DataOffset, img.DataLength))) rawIdentical++;
                        }
                        else rebuildOk++;
                    }
                    if ((img.Header.Type & 0x80) != 0 || images % 400 == 0)
                    {
                        // noise cannot compress: forces a larger RefPack region (and exercises the shift/fix-up path)
                        var rng = new Random(img.Index + 7); var noise = new byte[d.Width * d.Height * 4]; rng.NextBytes(noise);
                        for (var i = 3; i < noise.Length; i += 4) noise[i] = 255;
                        var r = SshWriter.ReplaceImage(ssh, img, noise, d.Width, d.Height, PaletteMode.KeepExisting);
                        grown++;
                        var re = SshFile.Read(r.File);
                        var ok = re.Images.Count == ssh.Images.Count;
                        for (var i = 0; ok && i < ssh.Images.Count; i++)
                        {
                            if (i == img.Index) continue;
                            ok = re.Decode(re.Images[i]).Bgra.AsSpan().SequenceEqual(ssh.Decode(ssh.Images[i]).Bgra) && re.Images[i].Name == ssh.Images[i].Name;
                        }
                        if (ok) grownOk++; else { failed++; Console.WriteLine($"GROW FAIL {n.Path} {img.Tag}"); }
                    }
                }
                catch (Exception ex) { failed++; Console.WriteLine($"ERROR {n.Path} {img.Tag}: {ex.Message}"); }
                next:;
            }
        }
        Console.WriteLine($"ssh: files={files} images={images} unsupported(GST etc.)={unsupported} keepPalette-ok={keepOk} (raw bytes identical {rawIdentical}) rebuildPalette-ok={rebuildOk} noisy/grow-ok={grownOk}/{grown} source-data quirks (indices beyond palette)={quirks} failed={failed}");
        if (minPsnr.Value < 99) Console.WriteLine($"lowest DXT re-encode PSNR {minPsnr.Value:0.0} dB ({minPsnr.Where}); floor {MinDxtPsnr} dB");
        Console.WriteLine("types: " + string.Join(", ", types.Select(kv => $"{kv.Key}:{kv.Value}")));
        foreach (var (why, count) in reasons.OrderByDescending(r => r.Value)) Console.WriteLine($"unsupported ×{count}: {why}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Pixel equality; with <paramref name="hiddenOk"/> the colour under alpha 0 is ignored (a rebuilt palette collapses invisible pixels).</summary>
    private static bool SamePixels(byte[] a, byte[] b, bool hiddenOk)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i += 4)
        {
            if (hiddenOk && a[i + 3] == 0 && b[i + 3] == 0) continue;
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) return false;
        }
        return true;
    }

    /// <summary>Re-encoded DXT images must decode within this PSNR of the source decode.</summary>
    private const double MinDxtPsnr = 36;

    private static double Psnr(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return 0;
        double se = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i | 3] == 0 && b[i | 3] == 0) continue;   // colour hidden under zero alpha is irrelevant
            var e = a[i] - b[i]; se += e * e;
        }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * a.Length / se);
    }

    private static bool OutsidePalette(byte[] data, SshImage img)
    {
        if (img.Palette is not { } pal || (img.Header.Type & 0x7F) != 0x02) return false;
        var colors = pal.Header.Width * Math.Max(1, pal.Header.Height);
        for (var i = 0; i < img.Header.Width * img.Header.Height; i++) if (data[img.DataOffset + i] >= colors) return true;
        return false;
    }

    private static bool Covered(SshImage img, int i) =>
        i >= img.DataOffset && i < img.DataOffset + img.DataLength || img.Palette is { } p && i >= p.DataOffset && i < p.DataOffset + p.DataLength;
}

static class FormatEditTests
{
    /// <summary>
    /// edit-formats &lt;game&gt;: exercises the APT/const/LOCH editors on every file that has something to edit and checks that only the intended
    /// field changed after re-reading the result.
    /// </summary>
    public static int Run(AssetNode root)
    {
        int apts = 0, texts = 0, textOk = 0, placements = 0, placeOk = 0, consts = 0, constOk = 0, appended = 0, inPlace = 0, failed = 0;
        foreach (var n in root.EnumerateDeep())
        {
            if (n.IsFolder) continue;
            try
            {
                if (n.Extension == ".apt")
                {
                    var data = n.ReadAll();
                    var cn = n.Parent?.Children.FirstOrDefault(c => c.Name.Equals(Path.ChangeExtension(n.Name, ".const"), StringComparison.OrdinalIgnoreCase));
                    var consts_ = cn is null ? null : AptConstants.Read(cn.ReadAll());
                    var apt = AptFile.Read(data, consts_?.MovieOffset ?? 0);
                    apts++;
                    foreach (var c in apt.Characters.OfType<AptCharacter>().Where(c => c.Type == AptCharacterType.EditText).Take(3))
                    {
                        foreach (var text in new[] { "Hi", "A much longer replacement string that cannot fit in place \u00e9" })
                        {
                            texts++;
                            var edited = AptEditor.EditText(data, c, new EditTextChange { Text = text, Variable = "myVar", Color = 0xFF112233, FontHeight = 33.5f, Bounds = new System.Numerics.Vector4(1, 2, 303, 404), Align = 2, WordWrap = true }, out var how);
                            if (how.Contains("appended")) appended++; else inPlace++;
                            var re = AptFile.Read(edited, consts_?.MovieOffset ?? 0);
                            var rc = re.Characters[c.Id]!;
                            var ok = rc.Text == text && rc.Variable == "myVar" && rc.TextColor == 0xFF112233 && rc.FontHeight == 33.5f && rc.Bounds == new System.Numerics.Vector4(1, 2, 303, 404) && rc.TextAlign == 2 && rc.WordWrap && rc.FontId == c.FontId && rc.Multiline == c.Multiline;
                            // every other character must read back identically
                            for (var i = 0; ok && i < apt.Characters.Count; i++)
                                if (i != c.Id && apt.Characters[i] is { } a && re.Characters[i] is { } b) ok = a.Summary == b.Summary && a.Text == b.Text && a.Frames.Count == b.Frames.Count && a.Frames.SelectMany(f => f.Items).Select(x => x.Detail).SequenceEqual(b.Frames.SelectMany(f => f.Items).Select(x => x.Detail));
                            if (ok) textOk++; else { failed++; Console.WriteLine($"EDITTEXT FAIL {n.Path} char {c.Id}"); }
                        }
                    }
                    foreach (var item in apt.AllItems().Where(i => i.Type == 3).Take(3))
                    {
                        placements++;
                        var edited = AptEditor.Placement(data, item, new PlacementChange { X = 12.5f, Y = -7.25f, ScaleX = 2f, ScaleY = 0.5f, Tint = 0x80FFFFFF });
                        var re = AptFile.Read(edited, consts_?.MovieOffset ?? 0);
                        var rit = re.AllItems().First(i => i.Offset == item.Offset);
                        var ok = rit.Matrix.M31 == 12.5f && rit.Matrix.M32 == -7.25f && rit.Matrix.M11 == 2f && rit.Matrix.M22 == 0.5f && rit.Tint == 0x80FFFFFF && rit.Depth == item.Depth && rit.Character == item.Character && rit.Name == item.Name;
                        if (ok) placeOk++; else { failed++; Console.WriteLine($"PLACEMENT FAIL {n.Path} @0x{item.Offset:X}"); }
                    }
                }
                else if (n.Extension == ".const" && n.Format == FormatId.AptConst)
                {
                    var data = n.ReadAll();
                    var c = AptConstants.Read(data);
                    foreach (var item in c.Items.Where(i => i.Type == 1).Take(2))
                    {
                        consts++;
                        var edited = AptEditor.ConstantString(data, item, "EDITED constant text", out var how);
                        var re = AptConstants.Read(edited);
                        var ok = re.Items.Count == c.Items.Count && re.Items[item.Index].Text == "EDITED constant text" && re.Items.Where(i => i.Index != item.Index).Select(i => i.Display).SequenceEqual(c.Items.Where(i => i.Index != item.Index).Select(i => i.Display));
                        if (ok) constOk++; else { failed++; Console.WriteLine($"CONST FAIL {n.Path}[{item.Index}]"); }
                    }
                }
            }
            catch (Exception ex) { failed++; Console.WriteLine($"ERROR {n.Path}: {ex.Message}"); }
        }
        Console.WriteLine($"apt: files={apts} editText={textOk}/{texts} (in place {inPlace}, appended {appended}) placement={placeOk}/{placements} const-strings={constOk}/{consts} failed={failed}");

        int locs = 0, locOk = 0;
        foreach (var n in root.EnumerateDeep().Where(n => !n.IsFolder && n.Extension == ".loc"))
        {
            try
            {
                var data = n.ReadAll();
                if (data.Length < 4 || data[0] != 'L' || data[1] != 'O' || data[2] != 'C' || data[3] != 'H') continue;
                var t = LocTable.Read(data);
                locs++;
                var edits = new Dictionary<int, string> { [0] = "First string rewritten", [t.Strings.Count / 2] = "Middle string \u2026 with a much longer text\nand a line break", [t.Strings.Count - 1] = "" };
                var re = LocTable.Read(LocEditor.Write(data, edits));
                var ok = re.Strings.Count == t.Strings.Count;
                for (var i = 0; ok && i < t.Strings.Count; i++)
                    ok = edits.TryGetValue(i, out var e) ? re.Strings[i].Text == e : re.Strings[i].Text == t.Strings[i].Text && re.Strings[i].Id == t.Strings[i].Id;
                if (ok) locOk++; else { failed++; Console.WriteLine($"LOC FAIL {n.Path}"); }
            }
            catch (Exception ex) { failed++; Console.WriteLine($"ERROR {n.Path}: {ex.Message}"); }
        }
        Console.WriteLine($"loch: tables={locs} ok={locOk}");

        // text codec: every text-like asset must encode back to the identical bytes
        int tx = 0, txOk = 0;
        foreach (var n in root.EnumerateDeep().Where(n => !n.IsFolder && n.Format is FormatId.Text or FormatId.Csv or FormatId.LocText))
        {
            var d = n.ReadAll(); tx++;
            var (text, codec) = TextCodec.Decode(d);
            if (codec.Encode(text).AsSpan().SequenceEqual(d)) txOk++; else { failed++; Console.WriteLine($"TEXT ROUNDTRIP FAIL {n.Path}"); }
        }
        Console.WriteLine($"text: files={tx} byte-identical re-encode={txOk}");
        return failed == 0 ? 0 : 1;
    }
}

static class SwizzleTest
{
    /// <summary>No game image that the writer supports is actually swizzled, so check the forward swizzles against the verified un-swizzlers on synthetic data.</summary>
    public static int Run()
    {
        var failed = 0; var rng = new Random(11);
        foreach (var (w, h) in new[] { (16, 16), (32, 32), (64, 64), (128, 64), (128, 128), (256, 256), (64, 256) })
        {
            var linear = new byte[w * h]; rng.NextBytes(linear);
            try
            {
                var ok8 = Ps2Swizzle.Unswizzle8(Ps2Swizzle.Swizzle8(linear, w, h), w, h).AsSpan().SequenceEqual(linear);
                if (!ok8) { failed++; Console.WriteLine($"swizzle8 {w}x{h} FAIL"); }
            }
            catch (NotSupportedException ex) { Console.WriteLine($"swizzle8 {w}x{h}: rejected ({ex.Message})"); }
            var packed = new byte[(w * h + 1) / 2]; rng.NextBytes(packed);
            try
            {
                var ok4 = Ps2Swizzle.Unswizzle4(Ps2Swizzle.Swizzle4(packed, w, h), w, h).AsSpan().SequenceEqual(packed);
                if (!ok4) { failed++; Console.WriteLine($"swizzle4 {w}x{h} FAIL"); }
            }
            catch (NotSupportedException ex) { Console.WriteLine($"swizzle4 {w}x{h}: rejected ({ex.Message})"); }
        }
        Console.WriteLine($"swizzle inverse: failures={failed}");
        return failed == 0 ? 0 : 1;
    }
}
