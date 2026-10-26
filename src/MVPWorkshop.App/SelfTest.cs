using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

/// <summary>
/// Developer aid: <c>MVP-Workshop.exe --selftest result.txt</c> drives the editor through the same code paths the UI uses (apply edits in
/// real viewers, check the viewers refresh, undo/redo, dirty tracking, project save/load, export + verification) and writes a report.
/// </summary>
public static class SelfTest
{
    public static async void RunIfRequested(MainWindow w)
    {
        var args = Environment.GetCommandLineArgs();
        var isoTest = Array.IndexOf(args, "--iso-upscale-selftest");
        if (isoTest >= 0 && isoTest + 1 < args.Length) { IsoUpscaleSelfTest.Run(w, args[isoTest + 1]); return; }
        var aptTextureTest = Array.IndexOf(args, "--apt-texture-selftest");
        if (aptTextureTest >= 0 && aptTextureTest + 1 < args.Length) { AptTextureSelfTest.Run(w, args[aptTextureTest + 1]); return; }
        var menuTest = Array.IndexOf(args, "--menu-selftest");
        if (menuTest >= 0 && menuTest + 1 < args.Length) { MenuSelfTest.Run(w, args[menuTest + 1]); return; }
        var uniformTest = Array.IndexOf(args, "--uniform-selftest");
        if (uniformTest >= 0 && uniformTest + 1 < args.Length) { UniformSelfTest.Run(w, args[uniformTest + 1]); return; }
        var workspaceTest = Array.IndexOf(args, "--workspace-selftest");
        if (workspaceTest >= 0 && workspaceTest + 1 < args.Length) { WorkspaceSelfTest.Run(w, args[workspaceTest + 1]); return; }
        var assignmentTest = Array.IndexOf(args, "--assignment-selftest");
        if (assignmentTest >= 0 && assignmentTest + 1 < args.Length) { AssignmentSelfTest.Run(w, args[assignmentTest + 1]); return; }
        var i = Array.IndexOf(args, "--selftest");
        if (i < 0 || i + 1 >= args.Length) return;
        var report = new StringBuilder();
        var failed = 0;
        void Check(string name, bool ok, string detail = "") { report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}"); if (!ok) failed++; }
        async Task Settle() { await Task.Delay(900); }

        try
        {
            await Task.Delay(1500);
            var game = w.GameRoot ?? throw new InvalidOperationException("no game loaded");
            var s = Editor.Session!;
            Check("session attached, clean, empty", !s.IsDirty && s.Overrides.Count == 0);
            var gameHash = Hash(game.DiskPath!);

            // ---- APT: edit text + placement + constant through the real viewer
            var apt = w.FindNode("EACN/BUNDLE.BIG/cafeLib/stdPopup.big/stdPopup.apt")!;
            w.OpenAsset(apt);
            await Settle();
            var tab = w.Docs.Items.OfType<DocumentTab>().First(t => t.Node == apt);
            var panel = Find<AptEditPanel>(tab.Content);
            Check("APT viewer has an edit panel", panel is not null);
            var data = apt.ReadAll();
            var cn = apt.Parent!.Children.First(c => c.Name == "stdPopup.const");
            var file = AptFile.Read(data, AptConstants.Read(cn.ReadAll()).MovieOffset);
            var field = file.Characters.OfType<AptCharacter>().First(c => c.Type == AptCharacterType.EditText);
            var edited = AptEditor.EditText(data, field, new EditTextChange { Text = "SELFTEST TEXT" }, out _);
            var c1 = Editor.Apply(apt, edited, "selftest: edit text");
            await Settle();
            Check("text edit is a SAVED change", c1.Kind == ChangeKind.Saved);
            Check("tab is marked edited", tab.IsEdited);
            Check("session dirty", s.IsDirty);
            var panel2 = Find<AptEditPanel>(tab.Content);
            Check("APT viewer rebuilt with the new text", panel2 is not null && !ReferenceEquals(panel, panel2) && ListHas(panel2!, "SELFTEST TEXT"));
            Check("another viewer of the same node sees the edit", Encoding.Latin1.GetString(apt.ReadAll()).Contains("SELFTEST TEXT"));

            // ---- SSH: replace an image in the real texture viewer
            var ssh = w.FindNode("EACN/BUNDLE.BIG/cafeLib/stdPopup.big/stdPopup.ssh")!;
            w.OpenAsset(ssh);
            await Settle();
            var texTab = w.Docs.Items.OfType<DocumentTab>().First(t => t.Node == ssh);
            var tex = Find<TextureView>(texTab.Content)!;
            var sshFile = SshFile.Read(ssh.ReadAll());
            var img = sshFile.Images.First(x => SshWriter.Unsupported(sshFile, x) is null);
            var before = sshFile.Decode(img);
            var pic = new byte[before.Width * before.Height * 4];
            for (var p = 0; p < pic.Length; p += 4) { pic[p] = 40; pic[p + 1] = (byte)(p / 4 % 251); pic[p + 2] = 220; pic[p + 3] = 255; }
            tex.ReplaceWith(img.Index, pic, "selftest.png");
            await Settle();
            var after = SshFile.Read(ssh.ReadAll());
            var decoded = after.Decode(after.Images[img.Index]);
            Check("texture replacement is SAVED and decodes to the new picture", s.IsModified(ssh) && decoded.Bgra.AsSpan(0, 8).SequenceEqual(pic.AsSpan(0, 8)), $"{img.Tag} {before.Width}x{before.Height}");
            var otherOk = after.Images.Where(x => x.Index != img.Index).All(x => after.Decode(x).Bgra.AsSpan().SequenceEqual(sshFile.Decode(sshFile.Images[x.Index]).Bgra));
            Check("other images in the pack unchanged", otherOk);

            // ---- GST texture: preview-only
            var gst = w.FindNode("DATA/GHEAD.BIG")?.Children.FirstOrDefault(c => c.IsTexturePack && c.Format == FormatId.SshTexture && SshWriter.Unsupported(SshFile.Read(c.ReadAll()), SshFile.Read(c.ReadAll()).Images[0]) is not null);
            if (gst is not null)
            {
                w.OpenAsset(gst);
                await Settle();
                var gTab = w.Docs.Items.OfType<DocumentTab>().First(t => t.Node == gst);
                var gView = Find<TextureView>(gTab.Content)!;
                var g = SshFile.Read(gst.ReadAll()).Images[0];
                var gp = new byte[g.Header.Width * g.Header.Height * 4]; Array.Fill(gp, (byte)200);
                var countBefore = s.Overrides.Count;
                gView.ReplaceWith(0, gp, "gst.png");
                await Settle();
                Check("GST texture replacement is PREVIEW ONLY", s.PreviewOnlyCount >= 1 && s.Overrides.Count == countBefore && s.History.Last().Kind == ChangeKind.PreviewOnly, g.Tag);
            }
            else report.AppendLine("SKIP  no GST sample found");

            // ---- LOC string
            var loc = w.FindNode("DATA/FEENG.LOC")!;
            w.OpenAsset(loc);
            await Settle();
            var locBytes = LocEditor.Write(loc.ReadAll(), new Dictionary<int, string> { [3] = "Selftest string" });
            Editor.Apply(loc, locBytes, "selftest: loc");
            await Settle();
            var locTab = w.Docs.Items.OfType<DocumentTab>().First(t => t.Node == loc);
            Check("LOC edit applied and grid refreshed", LocTable.Read(loc.ReadAll()).Strings[3].Text == "Selftest string" && Find<LocEditView>(locTab.Content) is not null);

            // ---- text asset
            var fel = w.FindNode("DATA/FRONTEND/TITLE.BIG/fes_title.fel")!;
            var (txt, codec) = TextCodec.Decode(fel.ReadAll());
            Editor.Apply(fel, codec.Encode(txt + "\n// selftest\n"), "selftest: text");
            Check("text edit applied", Encoding.Latin1.GetString(fel.ReadAll()).Contains("// selftest"));

            // ---- non-writable container: preview-only through the generic path
            var blocked = game.EnumerateDeep().FirstOrDefault(n => !n.IsFolder && n.WriteBlocker is not null);
            if (blocked is not null)
            {
                var c = Editor.Apply(blocked, Encoding.ASCII.GetBytes("x"), "selftest: blocked");
                Check("asset in an unsupported container is PREVIEW ONLY", c.Kind == ChangeKind.PreviewOnly, blocked.Path);
            }
            else report.AppendLine("SKIP  no asset inside an unsupported container found");

            var saved = s.Overrides.Count;
            Check("saved changes counted", saved == 4, $"{saved} assets");

            // ---- undo / redo / dirty tracking
            var steps = s.History.Count;
            for (var k = 0; k < steps; k++) s.Undo();
            await Settle();
            Check("undo everything: no overrides, not dirty, no previews", s.Overrides.Count == 0 && !s.IsDirty && s.PreviewOnlyCount == 0);
            Check("viewer reverted with undo", !Encoding.Latin1.GetString(apt.ReadAll()).Contains("SELFTEST TEXT") && !tab.IsEdited);
            for (var k = 0; k < steps; k++) s.Redo();
            await Settle();
            Check("redo everything", s.Overrides.Count == saved && s.IsDirty && tab.IsEdited);

            // ---- project save / load
            var tmp = Path.Combine(Path.GetTempPath(), "mvpworkshop_selftest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var proj = Path.Combine(tmp, "t" + ProjectFile.Extension);
            Check("save project", w.SaveProjectTo(proj));
            Check("saved project is clean", !s.IsDirty);
            var undone = 0;
            while (!s.IsDirty && s.CanUndo) { s.Undo(); undone++; }
            Check("undoing a saved change after saving makes the project dirty again (preview-only undo does not)", s.IsDirty, $"{undone} undo step(s)");
            for (var k = 0; k < undone; k++) s.Redo();
            Check("redo back to the saved state is clean", !s.IsDirty);
            var loaded = ProjectFile.Load(proj, game, out var warnings);
            Check("project reloads with the same assets", loaded.Overrides.Count == saved && warnings.Count == 0 && loaded.Overrides.All(kv => s.Overrides[kv.Key].AsSpan().SequenceEqual(kv.Value)), string.Join("; ", warnings));
            Editor.Attach(loaded);

            // ---- export + verification, original files untouched
            var outDir = Path.Combine(tmp, "mod");
            loaded.OutputFolder = outDir;
            var export = ModExporter.Export(loaded, outDir, verify: true);
            Check("export verified", export.Ok && export.Files.Count == 3, string.Join("; ", export.Problems.Take(3)) + " files=" + string.Join(",", export.Files.Select(f => f.RelativePath)));
            Check("game files untouched", Hash(game.DiskPath!) == gameHash);
            var bad = ModExporter.OutputProblem(game, Path.Combine(game.DiskPath!, "mod"));
            Check("output inside the game folder is refused", bad is not null);
            Check("preview-only changes not exported", !export.Files.Any(f => f.RelativePath.Contains("GHEAD", StringComparison.OrdinalIgnoreCase)));
            Directory.Delete(tmp, true);
        }
        catch (Exception ex) { report.AppendLine("EXCEPTION " + ex); failed++; }

        foreach (var l in Log.Lines.Where(l => l.Level != "INFO").TakeLast(25)) report.AppendLine($"  log {l.Level}: {l.Text}");
        report.AppendLine(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
        File.WriteAllText(args[i + 1], report.ToString());
        Application.Current.Shutdown(failed == 0 ? 0 : 1);
    }

    private static bool ListHas(AptEditPanel panel, string text)
    {
        var lb = Find<ListBox>(panel);
        return lb?.ItemsSource is System.Collections.IEnumerable items && items.Cast<object>().Any(o => o.ToString()!.Contains(text) || o.GetType().GetProperty("Title")?.GetValue(o)?.ToString()?.Contains(text) == true);
    }

    private static T? Find<T>(DependencyObject d) where T : DependencyObject
    {
        if (d is T t) return t;
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
            if (Find<T>(c) is { } r) return r;
        if (d is Visual) for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) if (Find<T>(VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }

    /// <summary>Fingerprint of every file under the game folder that the test touches or could touch (all BIG/LOC/DAT sizes and times are cheap proxies; hash the key files fully).</summary>
    private static string Hash(string folder)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var fi = new FileInfo(f);
            sb.Append(f).Append(fi.Length).Append(fi.LastWriteTimeUtc.Ticks);
        }
        foreach (var key in new[] { "EACN/BUNDLE.BIG", "DATA/FEENG.LOC", "DATA/FRONTEND/TITLE.BIG" })
            sb.Append(Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(folder, key)))));
        return sb.ToString();
    }
}

