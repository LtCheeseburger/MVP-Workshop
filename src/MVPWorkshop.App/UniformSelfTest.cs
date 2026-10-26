using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Formats;
using MVPWorkshop.Core.Vfs;

namespace MVPWorkshop.App;

public static class UniformSelfTest
{
    public static async void Run(MainWindow window,string path)
    {
        var report=new StringBuilder(); int failed=0;
        void Check(string label,bool value) { report.AppendLine($"{(value?"PASS":"FAIL")} {label}"); if(!value)failed++; }
        IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
        {
            if(parent is T item)yield return item;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)foreach(var child in Find<T>(VisualTreeHelper.GetChild(parent,i)))yield return child;
        }
        async Task Settle() { await Task.Delay(500); window.UpdateLayout(); }
        try
        {
            window.Width=2100; window.Height=1200;
            window.LogRow.Height=new GridLength(0); window.LogSplitRow.Height=new GridLength(0);
            TreeItem? teams=null;
            for(int i=0;i<120&&teams is null;i++) { await Task.Delay(250); teams=window.Tree.Items.OfType<TreeItem>().FirstOrDefault(t=>t.Name=="Teams"&&t.Children.Any(c=>c.Team is not null)); }
            if(teams is null)throw new InvalidOperationException("No school catalog");
            foreach(var abbreviation in new[]{"LSU","BAMA","STAN"})
            {
                var profile=teams.Children.Single(t=>t.Team?.Team.Abbreviation==abbreviation).Team!;
                window.OpenTeam(profile); var tab=(DocumentTab)window.Docs.SelectedItem;
                ((TeamView)tab.Content).SelectedSection=6; await Settle();
                ComboBox Combo(string tag)=>Find<ComboBox>(tab.Content).Single(c=>Equals(c.Tag,tag));
                TextBox Text(string tag)=>Find<TextBox>(tab.Content).Single(c=>Equals(c.Tag,tag));
                var variant=Combo("UniformVariant");
                Check(abbreviation+" available variants",variant.Items.Count==UniformPreview.Variants(profile).Count);
                variant.SelectedIndex=variant.Items.Count-1; Combo("UniformPlayer").SelectedIndex=2;
                Text("UniformName").Text="ABCDEFGHIJKL"; Text("UniformNumber").Text="99";
                foreach(var check in Find<CheckBox>(tab.Content).Where(c=>c.Tag?.ToString()?.StartsWith("UniformDirt:")==true).ToList())check.IsChecked=true;
                Check(abbreviation+" native dirt state is preview only",((TeamView)tab.Content).UniformState.DirtRegions.Contains("dcht")&&Editor.Session!.Overrides.Count==0);
                Check(abbreviation+" original and edited models",Find<ModelView>(tab.Content).Count()==2);
                Check(abbreviation+" preview does not dirty project",Editor.Session!.Overrides.Count==0);
                var player=(TeamPlayer)Combo("UniformPlayer").SelectedItem;
                Find<Button>(tab.Content).Single(b=>Equals(b.Tag,"ApplyUniformRoster")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Settle();
                var source=player.Sources.Single(s=>s.Asset.Name=="attrib.dat").Asset;
                var record=PlayerAttributes.Read(source.ReadAll()).Records[player.Id];
                Check(abbreviation+" Apply edits real roster",record.Field(1)=="ABCDEFGHIJKL"&&record.Field(2)=="99");
                Check(abbreviation+" text and variant survive project refresh",Text("UniformName").Text=="ABCDEFGHIJKL"&&Text("UniformNumber").Text=="99"&&Combo("UniformVariant").SelectedIndex==variant.Items.Count-1);
                Editor.Session!.Undo(); await Settle();
                Check(abbreviation+" undo restores disc roster",source.ReadAll().SequenceEqual(source.ReadOriginal()));
                Check(abbreviation+" preview text survives undo",Text("UniformName").Text=="ABCDEFGHIJKL");
                Check(abbreviation+" dirt survives history refresh",Find<CheckBox>(tab.Content).Single(c=>Equals(c.Tag,"UniformDirt:dcht")).IsChecked==true);
                var dpi=VisualTreeHelper.GetDpi(window);
                var bitmap=new RenderTargetBitmap((int)(window.ActualWidth*dpi.DpiScaleX),(int)(window.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
                bitmap.Render(window); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using(var stream=File.Create(path+"-"+abbreviation+".png"))png.Save(stream);
                foreach(var model in Find<ModelView>(tab.Content))model.RestoreUniformCamera(model.CaptureUniformCamera() with { Yaw=0 });
                await Settle();
                bitmap=new RenderTargetBitmap((int)(window.ActualWidth*dpi.DpiScaleX),(int)(window.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
                bitmap.Render(window); png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using(var stream=File.Create(path+"-"+abbreviation+"-front.png"))png.Save(stream);
            }
        }
        catch(Exception ex) { report.AppendLine(ex.ToString()); failed++; }
        report.AppendLine($"Failures: {failed}"); File.WriteAllText(path,report.ToString());
        Application.Current.Shutdown(failed==0?0:1);
    }
}
