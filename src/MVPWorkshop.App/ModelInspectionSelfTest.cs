using System.IO;
using System.Text;
using MVPWorkshop.Core.Formats.Eagl;

namespace MVPWorkshop.App.Viewers;

public sealed partial class ModelView
{
    internal async Task<string> CheckInspection(string directory)
    {
        var report = new StringBuilder();
        void Check(string name,bool ok) => report.AppendLine($"{(ok ? "PASS" : "FAIL")} {name}");
        RefreshMaterials(); Check("native materials list exposes every texture group",_materialList!.Items.Count==_model.Mesh.Draws.Select(d=>d.Texture??"untextured").Distinct().Count());
        Check("native body resolves LOD sources",LodPicker().Items.Count>=2);
        _wireframe=true; _showNormals=true; AddInspectionOverlay(); int children=_scene.Children.Count;
        AddInspectionOverlay(); Check("wireframe/normals render without accumulating scene nodes",_inspectionOverlay.Children.Count==1 && _scene.Children.Count==children);
        _wireframe=false; _showNormals=false; AddInspectionOverlay();
        Check("body resolves bones for pose and animation",Skeleton is { Bones.Count: >0 });
        for(int i=0;i<50 && (_animList?.Items.Count??0)==0;i++) await Task.Delay(100);
        var clip = _animList?.Items.OfType<ClipRow>().FirstOrDefault(c=>c.Anim.SkeletonHash16==(Skeleton!.Hash&65535));
        Check("animation picker resolves clips compatible with the model",clip is not null);
        if(clip is not null)
        {
            _animList!.SelectedItem=clip; TogglePlay(); _frameSlider.Value=Math.Min(3,_frameSlider.Maximum); UpdateDeformation();
            Check("timeline scrubs and pauses the native clip",!_playing && _frame==(float)_frameSlider.Value && _frameText.Text.Length>0);
            Check("play/pause retains a visible button label",_playButton!.Content is System.Windows.Controls.StackPanel panel && panel.Children.OfType<System.Windows.Controls.TextBlock>().Last().Text=="Play");
            GltfInterchange.Export(Path.Combine(directory,"native-clip.gltf"),_model,TexturePngs(),Skeleton,_anim,_animRoot);
            Check("native animation and skin export to glTF",new FileInfo(Path.Combine(directory,"native-clip.gltf")).Length>1000);
        }
        GltfInterchange.Export(Path.Combine(directory,"native-model.gltf"),_model,TexturePngs(),Skeleton);
        Check("model with resolved texture materials exports",GltfInterchange.ReadPositions(Path.Combine(directory,"native-model.gltf"),_model.Mesh).SequenceEqual(_model.Mesh.Positions));
        return report.ToString();
    }
}
