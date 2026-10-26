using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using MVPWorkshop.Core.Vfs;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App.Viewers;

public sealed class StadiumView : Grid
{
    Border? renderHost;
    readonly Image image = new() { Stretch = Stretch.Uniform };
    StadiumRenderer? renderer;
    readonly HashSet<StadiumComponent> visible = [];
    bool rendering, pending;
    readonly PerspectiveCamera camera = new() { FieldOfView = 60, NearPlaneDistance = 1, FarPlaneDistance = 200000 };

    readonly StackPanel components = new();
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    readonly List<AssetNode> nodes;
    readonly StadiumAssignments? map;
    readonly int? stadium;
    readonly AssetNode originalArchive;
    Point last;
    Point3D target = new(1000, 0, 1000);
    double yaw = -2.4, pitch = .55, distance = 6500;
    bool free;
    int field, sky, time, crowd;
    public StadiumView(AssetNode archive)
    {
        originalArchive=archive;
        nodes = MainWindow.Instance!.GameRoot!.EnumerateDeep().ToList();
        // The executable (SLUS ELF or default.xbe) carries the stadium/time-of-day tables; without them archives open directly.
        map = GameExecutable.Find(nodes) is { } executable ? StadiumAssignments.Read(executable.ReadAll()) : null;
        stadium = map is null ? null : Enumerable.Range(0, map.Count).Where(i => Enumerable.Range(0, 3).Any(t => map.Resolve(i, t)?.Code.Equals(System.IO.Path.GetFileNameWithoutExtension(archive.Name), StringComparison.OrdinalIgnoreCase) == true)).Select(i=>(int?)i).FirstOrDefault();

        time=archive.Name.Contains("nite",StringComparison.OrdinalIgnoreCase)?2:archive.Name.Contains("dusk",StringComparison.OrdinalIgnoreCase)?1:0;
        var host = new Border { Child = image, Background = Brushes.Black, Focusable = true };
        renderHost=host;
        host.SizeChanged += (_,_) => Draw();
        host.MouseDown += (_, e) => { host.Focus(); last = e.GetPosition(host); host.CaptureMouse(); };
        host.MouseUp += (_, _) => host.ReleaseMouseCapture();
        host.MouseMove += (_, e) => {
            var p = e.GetPosition(host); var dx = p.X-last.X; var dy = p.Y-last.Y; last = p;
            if(e.LeftButton == MouseButtonState.Pressed) { yaw -= dx*.006; pitch = Math.Clamp(pitch+dy*.006,-1.5,1.5); Update(); }
            else if(e.RightButton == MouseButtonState.Pressed) { var r = new Vector3D(Math.Cos(yaw),0,-Math.Sin(yaw)); target += r*(-dx*distance*.001)+new Vector3D(0,dy*distance*.001,0); Update(); }
        };
        host.MouseWheel += (_, e) => { if(free) {var forward=camera.LookDirection;forward.Normalize();target+=forward*(e.Delta>0?100:-100);Update();return;} distance = Math.Clamp(distance*(e.Delta>0?.88:1.14),10,50000); Update(); };
        host.KeyDown += (_, e) => {
            if(!free) return;
            var forward = camera.LookDirection; forward.Normalize(); var right = Vector3D.CrossProduct(forward,new Vector3D(0,1,0)); right.Normalize();
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?200:40;
            target += e.Key switch { Key.W => forward*step, Key.S => -forward*step, Key.A => -right*step, Key.D => right*step, Key.Q => new Vector3D(0,-step,0), Key.E => new Vector3D(0,step,0), _ => new Vector3D() }; Update();
        };
        ComboBox Pick(string[] values, Action<int> changed) { var c = new ComboBox { ItemsSource=values, SelectedIndex=0, MinWidth=90 }; c.SelectionChanged += (_,_) => changed(c.SelectedIndex); return c; }
        var timePick = Pick(["Day","Dusk","Night"], i => {time=i; Load();});
        var toolbar = B.Toolbar(timePick, Pick(["Field 0","Field 1","Field 2"],i=>{field=i; Load();}), Pick(["Sky 0","Sky 1","Sky 2","Sky 3","Sky 4"],i=>{sky=i; Load();}), Pick(["Crowd 0","Crowd 1","Crowd 2"],i=>{crowd=i; Load();}), B.Toggle("Free camera", false, v=>{ if(v) target=camera.Position; else target=camera.Position+camera.LookDirection; free=v; Update(); }), B.Button("Fit",null,(_,_)=>{target=new(1000,0,1000);distance=6500;yaw=-2.4;pitch=.55;if(free)target+=new Vector3D(Math.Sin(yaw)*Math.Cos(pitch),Math.Sin(pitch),Math.Cos(yaw)*Math.Cos(pitch))*distance;Update();}));
        var side = B.Rows((new ScrollViewer { Content=components, VerticalScrollBarVisibility=ScrollBarVisibility.Auto },B.Star()),(new ScrollViewer {Content=status, MaxHeight=220, VerticalScrollBarVisibility=ScrollBarVisibility.Auto}, B.Auto));
        Children.Add(B.Rows((toolbar,B.Auto),(B.Cols((side,B.Px(240)),(host,B.Star())),B.Star()),(B.Text("Drag orbit/look · Right drag pan · Wheel zoom · Free: W/A/S/D, Q/E, Shift faster", "Muted",11),B.Auto)));
        var args = Environment.GetCommandLineArgs(); var ti=Array.IndexOf(args,"--stadium-time"); if(ti>=0 && ti+1<args.Length && int.TryParse(args[ti+1], out var requested)) time=Math.Clamp(requested,0,2); timePick.SelectedIndex=time;
        Load();
    }
    void Update() {
        var dir = new Vector3D(Math.Sin(yaw)*Math.Cos(pitch),Math.Sin(pitch),Math.Cos(yaw)*Math.Cos(pitch));
        camera.Position = free ? target : target+dir*distance; camera.LookDirection=-dir*distance; camera.UpDirection=new(0,1,0); Draw();
    }
    async void Draw()
    {
        if(renderer is null || renderHost!.ActualWidth < 1 || renderHost!.ActualHeight < 1) return;
        if(rendering) { pending=true; return; }
        rendering=true;
        try {
            do {
                pending=false;
                var render=renderer; var enabled=new HashSet<StadiumComponent>(visible);
                int width=Math.Min(1100,Math.Max(1,(int)renderHost!.ActualWidth));
                int height=Math.Max(1,(int)(width*renderHost!.ActualHeight/renderHost!.ActualWidth));
                var p=camera.Position; var d=camera.LookDirection; var fov=(float)camera.FieldOfView;
                var pixels=await Task.Run(()=>render.Render(width,height,new((float)p.X,(float)p.Y,(float)p.Z),new((float)d.X,(float)d.Y,(float)d.Z),fov,enabled));
                if(render==renderer) image.Source=System.Windows.Media.Imaging.BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);
            } while(pending);
        } catch(Exception ex) {status.Text+="\nRender error: "+ex.Message;}
        finally {rendering=false;}
    }
    void Load() {
        components.Children.Clear();
        var resolution=stadium is int stadiumId && map is not null ? map.Resolve(stadiumId,time) : null;
        var code=resolution?.Code ?? System.IO.Path.GetFileNameWithoutExtension(originalArchive.Name);
        var archive=nodes.FirstOrDefault(n=>n.Name.Equals(code+".big",StringComparison.OrdinalIgnoreCase) && n.Path.Contains("/STADIUM/",StringComparison.OrdinalIgnoreCase));
        if(archive is null) { renderer=null; image.Source=null; status.Text="Missing archive: "+code; return; }
        var scene=StadiumScene.Load(archive,field,sky);
        var diagnostics=new List<string>(scene.Diagnostics);
        renderer = new StadiumRenderer(scene,crowd);
        diagnostics.AddRange(renderer.Diagnostics);
        visible.Clear();
        foreach(var component in scene.Components) {
            var enabled = component.Source.Name != "mshadow.ord";
            if (enabled) visible.Add(component);
            var cb=new CheckBox {Content=component.Source.Name+" / "+component.Model.Name, IsChecked=enabled, Margin=new Thickness(6), ToolTip=component.Source.Path};
            cb.Checked+=(_,_)=>{visible.Add(component); Draw();}; cb.Unchecked+=(_,_)=>{visible.Remove(component); Draw();}; components.Children.Add(cb);
        }
        foreach(var (name,text) in scene.Configuration) components.Children.Add(B.Button(name,null,(_,_)=>MainWindow.Instance?.OpenAsset(archive.Children.First(n=>n.Name==name)),"Outline"));
        components.Children.Add(B.Button("Texture sources",null,(_,_)=> { var box=new TextBox {IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Text=string.Join("\n",renderer.Sources.Select(p=>p.Key+" ← "+p.Value))+"\n\n"+string.Join("\n",diagnostics)}; new Window {Title="Stadium resources",Width=850,Height=600,Content=box,Owner=Window.GetWindow(this)}.Show(); },"Outline"));
        status.Text=$"Requested {new[]{"Day","Dusk","Night"}[time]} → {code}\n{(resolution is null?"Archive has no stadium enum mapping; time variant cannot be resolved.":resolution.UsedFallback?$"Fallback via {resolution.ResolvedTimeOfDay} mapping (dusk → night → day)":"Direct native resolution")}\n{archive.Path}\n{scene.Components.Count} components\n"+string.Join("\n",diagnostics)+"\nNative world coordinates; perspective GS preview with vertex colours, alpha, depth and clamp/repeat. Flag animation/texture variants, seat instancing and custom-venue construction remain unavailable. Mound-shadow overlay defaults off.";
        Update();
    }
}













