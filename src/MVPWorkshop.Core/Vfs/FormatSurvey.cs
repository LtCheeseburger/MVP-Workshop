using System.Text;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;

namespace MVPWorkshop.Core.Vfs;

public sealed class SurveyRow
{
    public string Format { get; init; } = "";
    public string Category { get; init; } = "";
    public string Extension { get; init; } = "";
    public int Count { get; set; }
    public long Bytes { get; set; }
    public int Compressed { get; set; }
    public int Parsed { get; set; }
    public int Failed { get; set; }
    public string Sample { get; set; } = "";
    public string FirstError { get; set; } = "";
    public SortedDictionary<string, int> Details { get; } = [];
    public string DetailText => string.Join(", ", Details.Select(kv => $"{kv.Key}×{kv.Value}"));
}

public sealed record SurveyProgress(int Files, string Current);

/// <summary>Walks every file (including archive contents) and records what each format is and whether it parses.</summary>
public static class FormatSurvey
{
    public static List<SurveyRow> Run(AssetNode root, bool deep, IProgress<SurveyProgress>? progress = null, CancellationToken ct = default)
    {
        var rows = new Dictionary<(FormatId, string), SurveyRow>();
        var files = 0;
        foreach (var n in root.EnumerateDeep())
        {
            ct.ThrowIfCancellationRequested();
            if (n.IsFolder) continue;
            files++;
            if (files % 200 == 0) progress?.Report(new SurveyProgress(files, n.Path));

            var fmt = n.Format;
            var key = (fmt, n.Extension);
            if (!rows.TryGetValue(key, out var row))
                rows[key] = row = new SurveyRow { Format = FormatDetector.Info[fmt].Name, Category = FormatDetector.Info[fmt].Category.ToString(), Extension = n.Extension, Sample = n.Path };
            row.Count++;
            row.Bytes += n.UnpackedSize ?? n.StoredSize;
            if (n.IsCompressed) row.Compressed++;
            if (n.LoadError is not null) Fail(row, n, n.LoadError);
            if (!deep) continue;

            try
            {
                Probe(n, fmt, row);
                row.Parsed++;
            }
            catch (Exception ex) { Fail(row, n, ex.Message); }

            // keep memory bounded on huge walks
            if (!n.IsContainer) n.DropCache();
        }
        progress?.Report(new SurveyProgress(files, "done"));
        return rows.Values.OrderBy(r => r.Category).ThenByDescending(r => r.Count).ToList();
    }

    private static void Fail(SurveyRow row, AssetNode n, string msg)
    {
        row.Failed++;
        if (row.FirstError.Length == 0) row.FirstError = $"{n.Path}: {msg}";
    }

    private static void Count(SurveyRow row, string key, int n) => row.Details[key] = row.Details.GetValueOrDefault(key) + n;

    private static void Probe(AssetNode n, FormatId fmt, SurveyRow row)
    {
        switch (fmt)
        {
            case FormatId.SshTexture:
            {
                var ssh = SshFile.Read(n.ReadAll());
                foreach (var img in ssh.Images)
                {
                    var k = $"0x{img.Header.Type:X2}";
                    row.Details[k] = row.Details.GetValueOrDefault(k) + 1;
                    ssh.Decode(img);
                }
                break;
            }
            case FormatId.SchlStream:
            case FormatId.SchlContainer:
            {
                foreach (var s in SchlStream.ScanAll(n.Open(), 50000))
                {
                    if (s.Header is null) throw new InvalidDataException($"Stream @0x{s.Offset:X} has no PT header");
                    var k = $"{EaPatchHeader.CodecName(s.Header.Codec2)} {s.Header.SampleRate}Hz {s.Header.Channels}ch";
                    row.Details[k] = row.Details.GetValueOrDefault(k) + 1;
                }
                break;
            }
            case FormatId.EaBank:
            {
                var bank = EaBank.Read(n.ReadAll());
                foreach (var s in bank.Sounds)
                {
                    var k = $"{EaPatchHeader.CodecName(s.Header.Codec2)} {s.Header.SampleRate}Hz";
                    row.Details[k] = row.Details.GetValueOrDefault(k) + 1;
                }
                break;
            }
            case FormatId.Elf:
            case FormatId.ElfFragment:
            {
                var (data, partner) = n.ReadElfJoined();
                if (partner is null && fmt == FormatId.ElfFragment)
                {
                    row.Details["unpaired (partner in another archive)"] = row.Details.GetValueOrDefault("unpaired (partner in another archive)") + 1;
                    break;
                }
                var elf = ElfFile.Read(data);
                var k = elf.Kind + (partner is not null ? " (split pair)" : elf.Sections.Count == 0 ? " (no sections)" : "");
                row.Details[k] = row.Details.GetValueOrDefault(k) + 1;
                if (partner is not null && fmt == FormatId.Elf)
                {
                    var obj = Formats.Eagl.RelocatedObject.Load(data);
                    if (Formats.Eagl.EaglAnimBank.LooksLikeBank(obj))
                    {
                        var bank = Formats.Eagl.EaglAnimBank.Read(obj);
                        row.Details["EAGL anim bank"] = row.Details.GetValueOrDefault("EAGL anim bank") + 1;
                        row.Details["animations"] = row.Details.GetValueOrDefault("animations") + bank.Anims.Count;
                        var bad = bank.Warnings.Count + bank.Anims.Sum(a => a.Warnings.Count);
                        if (bad > 0) throw new InvalidDataException($"{bad} animation channels failed to decode");
                    }
                    else if (Formats.Eagl.EaglReader.LooksLikeModel(obj))
                    {
                        var models = Formats.Eagl.EaglReader.Read(obj).Models;
                        var tris = models.Sum(m => m.Mesh.Indices.Count / 3);
                        var mk = tris > 0 ? "EAGL model" : "EAGL model (empty)";
                        row.Details[mk] = row.Details.GetValueOrDefault(mk) + 1;
                        row.Details["model triangles"] = row.Details.GetValueOrDefault("model triangles") + tris;
                    }
                }
                break;
            }
            case FormatId.BigArchive or FormatId.OffsetPack:
                _ = n.Children.Count;
                break;
            case FormatId.EaFontSfn:
            {
                var f = SfnFont.Read(n.ReadAll());
                f.Atlas.Decode(f.Atlas.Images[0]);
                Count(row, "glyphs", f.Glyphs.Count);
                Count(row, "kerning pairs", f.Kerning.Count);
                break;
            }
            case FormatId.XboxResource:
                { var xpr = SshFile.Read(XprTexture.ToShpx(n.ReadAll())); xpr.Decode(xpr.Images[0]); }
                break;
            case FormatId.XboxExecutable:
            {
                var xbe = XbeFile.Read(n.ReadAll());
                Count(row, xbe.Title, 1);
                Count(row, "sections", xbe.Sections.Count);
                break;
            }
            case FormatId.LocBinary:
                Count(row, "strings", LocTable.Read(n.ReadAll()).Strings.Count);
                break;
            case FormatId.AudioBankAbk:
            {
                var all = n.ReadAll();
                var bnk = AbkBank.Read(all).ExtractBank(all);
                if (bnk is null) { Count(row, "stream-only (.AST)", 1); break; }
                var bank = EaBank.Read(bnk);
                Count(row, "embedded BNKl", 1);
                Count(row, "sounds", bank.Sounds.Count);
                foreach (var s in bank.Sounds) EaBank.Decode(bnk, s);
                break;
            }
            case FormatId.MoirCsi:
                Count(row, "labels", MoirScript.Read(n.ReadAll()).Labels.Count);
                break;
            case FormatId.SpeechHeader:
            {
                var h = SpeechHeader.Parse(n.ReadAll()) ?? throw new InvalidDataException("Record shorter than its 18-byte head");
                Count(row, $"kind {h.Kind}", 1);
                break;
            }
            case FormatId.AptMovie:
            {
                var consts = n.Parent?.Children.FirstOrDefault(c => c.Name.Equals(System.IO.Path.ChangeExtension(n.Name, ".const"), StringComparison.OrdinalIgnoreCase)) is { } cn
                    ? AptConstants.Read(cn.ReadAll()) : null;
                var data = n.ReadAll();
                var apt = AptFile.Read(data, consts?.MovieOffset ?? 0);
                Count(row, "characters", apt.Characters.Count);
                foreach (var item in apt.AllItems().Where(i => i.Code is not null))
                {
                    var code = AptBytecode.Disassemble(data, item.Code!.Value, consts);
                    Count(row, "actions", 1);
                    Count(row, "instructions", code.Count);
                    if (code.LastOrDefault() is { Mnemonic: var m } && m.StartsWith("op_", StringComparison.Ordinal)) Count(row, $"stopped at {m}", 1);
                }
                break;
            }
            case FormatId.AptConst:
                Count(row, "constants", AptConstants.Read(n.ReadAll()).Items.Count);
                break;
            case FormatId.Binary or FormatId.Unknown:
            {
                var head = n.Open().ReadBytes(0, 4);
                var k = Convert.ToHexString(head);
                if (row.Details.Count < 24 || row.Details.ContainsKey(k)) row.Details[k] = row.Details.GetValueOrDefault(k) + 1;
                break;
            }
        }
    }

    public static string ToMarkdown(IEnumerable<SurveyRow> rows, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        sb.AppendLine("| Category | Format | Ext | Count | Size | RefPack | Parsed | Failed | Details | Sample |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| {r.Category} | {r.Format} | {r.Extension} | {r.Count} | {IO.Bin.Size(r.Bytes)} | {r.Compressed} | {r.Parsed} | {r.Failed} | {r.DetailText} | `{r.Sample}` |");
        var errs = rows.Where(r => r.FirstError.Length > 0).ToList();
        if (errs.Count > 0)
        {
            sb.AppendLine().AppendLine("## First error per format").AppendLine();
            foreach (var r in errs) sb.AppendLine($"- **{r.Format} {r.Extension}**: {r.FirstError}");
        }
        return sb.ToString();
    }
}
