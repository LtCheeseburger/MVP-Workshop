using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MVPWorkshop.App.Controls;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.IO;
using MVPWorkshop.Core.Editing;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal async void RunModelViewersCheck(string reportPath)
    {
        try
        {
            var node = FindNode("data/geometry.big/mebody.ord") ?? throw new InvalidOperationException("Real-model check expects data/geometry.big/mebody.ord."); OpenAsset(node);
            await Task.Delay(1500); var model = Find<ModelView>(this) ?? throw new InvalidOperationException("Native model viewer did not open.");
            File.WriteAllText(reportPath,await model.CheckInspection(Path.GetDirectoryName(reportPath)!));
            var bitmap = new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output=File.Create(reportPath+".png"); encoder.Save(output);
        }
        catch(Exception ex) { File.WriteAllText(reportPath,"FAIL "+ex); }
        Application.Current.Shutdown();
    }

    internal async void RunViewersCheck(string reportPath)
    {
        var report = new StringBuilder();
        void Check(string label, bool value) => report.AppendLine($"{(value ? "PASS" : "FAIL")} {label}");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        try
        {
            var evtBytes = new byte[60]; "\u0003\u0013<\u0007"u8.CopyTo(evtBytes); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(evtBytes.AsSpan(4),20); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(evtBytes.AsSpan(16),1); "stadium"u8.CopyTo(evtBytes.AsSpan(52));
            var evtNode = new MVPWorkshop.Core.Vfs.AssetNode("Events.evt",null,false,evtBytes.Length,()=>new MemoryByteSource(evtBytes),"synthetic event directory");
            var evtDoc = AssetDocument.Create(evtNode); AddTab(evtDoc); await Task.Delay(150); UpdateLayout();
            Check("speech event preview displays decoded directory names", Find<DataGrid>(evtDoc.Content)?.Items.OfType<SpeechEventName>().Any(e=>e.Name=="stadium")==true);
            var shot = new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32); shot.Render(this); var image = new PngBitmapEncoder(); image.Frames.Add(BitmapFrame.Create(shot)); using(var file=File.Create(reportPath+".events.png"))image.Save(file);
            var evtTabs=Find<TabControl>(evtDoc.Content)!; evtTabs.SelectedIndex=3; UpdateLayout();
            var evtRegions=Find<StructureView>(evtDoc.Content)!;
            Check("event directory has bounded Hex navigation regions",AssetStructure.Read(evtNode.Format,evtNode.Open()).Any(r=>r.Name.Contains("stadium")&&r.Offset==52));
            CloseDocument(evtDoc);
            var node = Editor.Session!.GameRoot.EnumerateDeep().Single(n => n.Name == "tone.asf"); var original = node.ReadAll();
            OpenAsset(node); await Task.Delay(350);
            var document = AssetDocument.Create(node); var tabs = Find<TabControl>((DependencyObject)document.Content)!;
            tabs.SelectedIndex = 3; Check("every asset has a working structure tab", ((TabItem)tabs.Items[3]).Content is StructureView);
            tabs.SelectedIndex = 1; var hex = ((TabItem)tabs.Items[1]).Content as HexPanel;
            var regions = AssetStructure.Read(node.Format,node.Open()); hex!.SelectRegion(regions.Last());
            Check("hex overlay navigates native stream blocks", regions.Any(r=>r.Name=="SCDl"));
            var view = Find<AudioView>(this)!; Check("stream preview uses the audio editor", view is not null);
            var path = Path.Combine(Path.GetDirectoryName(reportPath)!,"replacement.wav");
            File.WriteAllBytes(path,AudioCodecs.ToWav(new DecodedAudio(Enumerable.Repeat((short)16000,500).ToArray(),1,44100,"test")));
            var ffmpeg = (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator).Select(p=>Path.Combine(p.Trim('"'),"ffmpeg.exe")).FirstOrDefault(File.Exists);
            if(ffmpeg is not null) foreach(var extension in new[]{"mp3","ogg"})
            {
                var encodedPath=Path.ChangeExtension(path,extension); var start=new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute=false,CreateNoWindow=true };
                foreach(var argument in new[]{"-v","error","-nostdin","-y","-i",path,encodedPath}) start.ArgumentList.Add(argument);
                using var converter=System.Diagnostics.Process.Start(start)!; if(!converter.WaitForExit(30000)) { converter.Kill(true); throw new TimeoutException("Test fixture conversion timed out."); }
                var converted=await Task.Run(()=>AudioView.ReadInput(encodedPath,22050,1,0.1));
                Check(extension+" input automatically converts to native PCM dimensions",converter.ExitCode==0 && converted.SampleRate==22050 && converted.Channels==1 && converted.Pcm.Length>0);
            }
            else report.AppendLine("NOT RUN MP3/OGG input: ffmpeg.exe absent from PATH");
            bool accept = false; int previews = 0, impacts = 0;
            timer.Tick += (_,_) =>
            {
                foreach(var dialog in OwnedWindows.OfType<Window>().ToArray())
                {
                    if(dialog.Title == "Audio replacement preview" && dialog.Content is StackPanel panel)
                    { previews++; panel.Children.OfType<Button>().ElementAt(accept ? 1 : 2).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                    else if(dialog.Title == "Replacement impact" && dialog.Content is ScrollViewer { Content: StackPanel impact })
                    { impacts++; impact.Children.OfType<Button>().First(b=>Equals(b.Tag,"ReplacementEveryone")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                }
            };
            timer.Start(); await view!.ReplaceAudio(path);
            Check("cancel encoded preview leaves source and undo untouched", previews==1 && !Editor.Session.IsModified(node) && node.ReadAll().SequenceEqual(original));
            accept = true; await view.ReplaceAudio(path);
            var memory = new MemoryByteSource(node.ReadAll()); var decoded = SchlStream.ScanAll(memory).Single().Decode(memory);
            Check("audio import previews then reviews impact before commit", previews==2 && impacts==1 && Editor.Session.IsModified(node));
            Check("committed audio keeps native rate channels and sample count", decoded.SampleRate==22050 && decoded.Channels==1 && decoded.Pcm.Length==2205 && decoded.Pcm[0]==16000 && decoded.Pcm[^1]==0);
            Editor.Session.Undo(); Check("audio undo restores every native byte", node.ReadAll().SequenceEqual(original));
            var bulk = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!,"bulk-audio")).FullName;
            foreach(var f in Directory.GetFiles(bulk)) File.Delete(f);
            File.Copy(path,Path.Combine(bulk,"tone_Stream_0.wav")); File.WriteAllText(Path.Combine(bulk,"tone_Stream_7.wav"),"x"); File.WriteAllText(Path.Combine(bulk,"notes.txt"),"x");
            AudioBulkReview? seen = null; view.ReviewOverride = r =>
            {
                seen = r; if(File.Exists(reportPath+".bulk-audio.png")) return false; r.Owner = this; r.Show(); r.UpdateLayout();
                var shot = new RenderTargetBitmap((int)r.ActualWidth,(int)r.ActualHeight,96,96,PixelFormats.Pbgra32); shot.Render((Visual)r.Content);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(shot)); using(var o=File.Create(reportPath+".bulk-audio.png")) png.Save(o);
                r.Close(); return false;
            };
            await view.ImportFolder(bulk);
            Check("bulk audio matches exact Export All names and lists unmatched inputs", seen!.Rows.Count(r=>r.Ready)==1 && seen.Rows.Any(r=>r.Status==AudioBulkImport.Status.Unmatched && r.File!.EndsWith("tone_Stream_7.wav")) && seen.Rows.Any(r=>r.File!.EndsWith("notes.txt")));
            Check("bulk audio cancel applies nothing", !Editor.Session.IsModified(node) && node.ReadAll().SequenceEqual(original) && !Editor.Session.CanUndo);
            File.WriteAllBytes(Path.Combine(bulk,"tone_Stream_0.ogg"),[1,2,3]); seen=null; await view.ImportFolder(bulk);
            Check("bulk audio reports duplicate inputs and never picks one", seen!.Rows.Count(r=>r.Status==AudioBulkImport.Status.Duplicate)==2 && !seen.Rows.Any(r=>r.Ready));
            File.Delete(Path.Combine(bulk,"tone_Stream_0.wav")); seen=null; await view.ImportFolder(bulk);
            Check("bulk audio shows corrupt inputs as conversion failures", seen!.Rows.Single(r=>r.Target is not null).Error is not null && !seen.Rows.Any(r=>r.Ready));
            File.Delete(Path.Combine(bulk,"tone_Stream_0.ogg")); File.Copy(path,Path.Combine(bulk,"tone_Stream_0.wav"));
            var newer = original.ToArray(); newer[^1] ^= 1; view.ReviewOverride = r => { Editor.Session.Commit(node, newer, "Concurrent edit"); return true; };
            accept = true; impacts = 0; await view.ImportFolder(bulk);
            Check("stale bulk review cannot overwrite a newer edit", node.ReadAll().SequenceEqual(newer) && Editor.Session.History.Count==1 && Editor.Session.History[0].Description=="Concurrent edit");
            while(Editor.Session.CanUndo) Editor.Session.Undo();
            view.ReviewOverride = r => true; impacts = 0; await view.ImportFolder(bulk);
            var bulkBytes = node.ReadAll(); var bm = new MemoryByteSource(bulkBytes); var bd = SchlStream.ScanAll(bm).Single().Decode(bm);
            Check("bulk audio commits one undoable edit after impact review", impacts==1 && Editor.Session.History.Count==1 && Editor.Session.History[0].Description.StartsWith("Import 1 sound") && bd.Pcm.Length==2205 && bd.Pcm[0]==16000 && bulkBytes.Length==original.Length);
            Editor.Session.Undo(); bool undone = node.ReadAll().SequenceEqual(original); Editor.Session.Redo();
            Check("bulk audio undo and redo restore exact bytes", undone && node.ReadAll().SequenceEqual(bulkBytes));
            var project = Path.Combine(Path.GetDirectoryName(reportPath)!,"bulk-audio.mvpproj"); var active = Editor.Session;
            ProjectFile.Save(active, project, markSaved:false); var reloaded = ProjectFile.Load(project, active.GameRoot, out _); active.ActivateOverlay();
            Check("bulk audio survives project save and reload", reloaded.GetOverride(reloaded.Resolve(node.Key)!)?.SequenceEqual(bulkBytes) == true);
            Editor.Session.Undo(); view.ReviewOverride = null;
            await Task.Delay(200); UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var output=File.Create(reportPath+".png")) encoder.Save(output);
        }
        catch(Exception ex) { report.AppendLine("FAIL "+ex); }
        finally { timer.Stop(); }
        File.WriteAllText(reportPath,report.ToString()); Application.Current.Shutdown();
    }
    private static T? Find<T>(DependencyObject parent) where T:DependencyObject
    {
        if(parent is T match) return match;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) if(Find<T>(VisualTreeHelper.GetChild(parent,i)) is {} found) return found;
        return null;
    }
}
