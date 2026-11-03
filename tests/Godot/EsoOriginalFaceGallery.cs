using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using Process=System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// Read-only face-anatomy gallery made exclusively from original ESO
/// CustomizationSkeleton GR2 head meshes and their original skin weights.
/// The archive itself supplies the head shape and facial control joints.
///
/// Nothing is invented for eyes, nose, mouth, skin or hair, and no facial
/// customization transforms are applied. The neutral clay material makes
/// missing original texture/material data visible as a limitation.
/// </summary>
public partial class EsoOriginalFaceGallery:Node3D
{
    private static readonly (uint Id,string Mesh,int Vertices,int Bindings,int BoneCount)[] Sources=
    {
        (0x000E2CFF,"Head_004",404,6,39),
        (0x001076DD,"Head_600",6530,43,49),
        (0x001230E1,"Head_601",1443,38,44),
        (0x00129B5D,"Head_008",1721,38,44),
        (0x000DEE57,"Head_502",890,35,42),
        (0x000DEE59,"Head_503",866,43,50)
    };
    [Export]public bool SelfTest {get;set;}
    private readonly List<Node3D> _figures=new();
    private readonly Dictionary<uint,EsoSourceRig> _sourceRigs=new();
    private readonly List<MeshInstance3D> _originalHairs=new();
    private ShaderMaterial? _candidateHairMaterial;
    private Material? _neutralHairMaterial;
    private bool _candidateHairVisible=true;
    private bool _hairTintMaskVisible;
    private Camera3D? _camera;
    private Label? _hud;
    private int _focus=-1,_shotCount;
    private bool _dragging,_shot;
    private float _yaw,_pitch,_distance=2.0f;

    private static string Root=>ProjectSettings.GlobalizePath("res://");
    private static string Cache=>Path.Combine(Root,".research","npc-rigs");
    private static string Helper=>Path.Combine(Root,"tools","ModelGeometry",
        "bin","Debug","net8.0","ModelGeometry.dll");
    private static string Granny=>System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
        @"C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll";
    private static string Manifest=>System.Environment.GetEnvironmentVariable("ESO_MNF")??
        @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
    private static string Oodle=>Path.Combine(Root,".research","ooz.dll");
    public int OriginalHeadCount=>_figures.Count;
    public int SourceDdsHairCount=>_originalHairs.Count;
    public bool SourceDdsHairVisible=>_candidateHairVisible;
    public bool SourceHairTintMaskVisible=>_hairTintMaskVisible;

    public override void _Ready()
    {
        try
        {
            Directory.CreateDirectory(Cache);
            for(int i=0;i<Sources.Length;i++)
            {
                var (id,meshName,vertexCount,bindings,bones)=Sources[i];
                string prefix=Path.Combine(Cache,$"face-gallery-{id:X8}");
                string rigPath=prefix+"-rig.json";
                string meshPath=prefix+".esm";
                if(!File.Exists(rigPath))
                    File.WriteAllText(rigPath,RunSource("--inspect-rig",Granny,
                        id.ToString("X8"),Manifest,Oodle));
                if(!File.Exists(meshPath))
                    RunSource(Granny,id.ToString("X8"),meshPath,Manifest,Oodle);
                EsoSourceRig rig=EsoSourceRig.Read(rigPath);
                _sourceRigs.Add(id,rig);
                if(rig.MeshName!=meshName||rig.VertexCount!=vertexCount||
                   rig.Bones.Count!=bones||rig.BindingNames.Length!=bindings)
                    throw new InvalidDataException($"Original ESO facial rig changed: {id:X8}");
                var (skeleton,skin)=rig.BuildSkeleton();
                ArrayMesh mesh=EsoHumanoidVisualLayer.LoadMesh(meshPath,meshName,rig);
                if(mesh.GetSurfaceCount()!=1||mesh.SurfaceGetArrayLen(0)!=vertexCount||
                   skin.GetBindCount()!=bindings)
                    throw new InvalidDataException("Source face geometry or skin changed");
                // These are source bind poses, no approximations or motion.
                var container=new Node3D
                {
                    Name="OriginalFace_"+meshName,
                    Position=new Vector3(((i%3)-1)*0.47f,
                        (i<3?0.34f:-0.34f),0)
                };
                AddChild(container);
                skeleton.Name="OriginalCustomizationSkeleton";
                container.AddChild(skeleton);
                var face=new MeshInstance3D
                {
                    Name="OriginalSourceHead",
                    Mesh=mesh,
                    Skin=skin,
                    Skeleton=new NodePath(".."),
                    MaterialOverride=new StandardMaterial3D
                    {
                        AlbedoColor=new Color("#d2cbbf"),
                        Roughness=0.83f,
                        CullMode=BaseMaterial3D.CullModeEnum.Disabled
                    }
                };
                skeleton.AddChild(face);
                if(id is 0x00129B5D or 0x001076DD)
                    AttachVerifiedOriginalHair(skeleton,rig,id);
                var label=new Label3D
                {
                    Name="OriginalFaceLabel",
                    Text=$"{i+1}. {meshName}\n0x{id:X8} | {vertexCount} vertices",
                    FontSize=42,PixelSize=0.00068f,
                    Position=new Vector3(0,2.41f,0),
                    Modulate=new Color("#eef5ec")
                };
                container.AddChild(label);
                _figures.Add(container);
                GD.Print($"ORIGINAL ESO {meshName} from 0x{id:X8}: "+
                    $"{vertexCount} source vertices, {bindings} original skinned binds, "+
                    $"{bones} customization bones");
            }
            if(SelfTest)
            {
                if(_originalHairs.Count!=2||_candidateHairMaterial==null)
                    throw new InvalidDataException(
                        "Two source-exact high-detail heads lost provisional shared-authoring hair DDS");
                var originalHair=EsoSourceRig.Read(Path.Combine(Cache,
                    "face-gallery-hair-0029B2AF-rig.json"));
                // Native bone names alone are insufficient. The source
                // low-detail Head_601 has visibly different inverse binds
                // from the Head_600/Head_008 matching source family.
                bool refused=false;
                try{_ = originalHair.BindToIdenticalSourceRest(
                    _sourceRigs[0x001230E1]);}
                catch(InvalidDataException){refused=true;}
                if(!refused)
                    throw new InvalidDataException(
                        "Wrong original hair/head source rest must be rejected");
                GD.Print("PASS source face assembly negative control: "+
                    "Head_601 rejects hair despite matching bone names; "+
                    "original inverse-world matrices differ.");
                GD.Print("PASS six unaltered ESO original face meshes: "+
                    "Head_004 404/6, Head_600 6530/43, Head_601 1443/38, "+
                    "Head_008 1721/38, Head_502 890/35, Head_503 866/43; "+
                    "original bind-pose topology and skinning, no fake facial details");
                GetTree().Quit(0);
                return;
            }
            SetupView();
            SetProcess(true);
        }
        catch(Exception error)
        {
            GD.PushError("Original ESO face anatomy gallery failed closed: "+error);
            GetTree().Quit(1);
        }
    }

    private void AttachVerifiedOriginalHair(
        Skeleton3D skeleton,EsoSourceRig headRig,uint headId)
    {
        const uint hairId=0x0029B2AF;
        string baseFile=Path.Combine(Cache,$"face-gallery-hair-{hairId:X8}");
        string rigPath=baseFile+"-rig.json";
        string meshPath=baseFile+".esm";
        if(!File.Exists(rigPath))
            File.WriteAllText(rigPath,RunSource("--inspect-rig",Granny,
                hairId.ToString("X8"),Manifest,Oodle));
        if(!File.Exists(meshPath))
            RunSource(Granny,hairId.ToString("X8"),meshPath,Manifest,Oodle);
        string native=RunSource("--inspect-native-external-binding",
            Granny,hairId.ToString("X8"),headId.ToString("X8"),Manifest,Oodle);
        if(!native.Contains("total=12, exact=12, name-differing=0, index-unmapped=0",
                StringComparison.Ordinal)||
           !native.Contains("COMPATIBLE_SOURCE_RIG:",StringComparison.Ordinal))
            throw new InvalidDataException("Granny rejected original head/hair skin bindings");
        EsoSourceRig hair=EsoSourceRig.Read(rigPath);
        if(hair.MeshName!="HAR_M_HU_0343"||hair.VertexCount!=2841||
           hair.BindingNames.Length!=12||hair.Bones.Count!=17)
            throw new InvalidDataException("Original ESO hair source changed");
        // Compare EVERY hair-bone rest matrix with the source head's
        // inverse-world bind. No rest remap, offsets, or inferred anatomy.
        Skin skin=hair.BindToIdenticalSourceRest(headRig);
        ArrayMesh mesh=EsoHumanoidVisualLayer.LoadMesh(
            meshPath,"HAR_M_HU_0343",hair);
        if(mesh.SurfaceGetArrayLen(0)!=2841||skin.GetBindCount()!=12)
            throw new InvalidDataException("Original ESO hair geometry mismatch");
        if(_candidateHairMaterial==null)
        {
            string candidateIndex=Path.Combine(Root,"data",
                "eso_original_face_hair_texture_candidates.tsv");
            using var sourceTextures=new EsoOriginalCreatureTextures(
                Manifest,Oodle,candidateIndex);
            var mapping=sourceTextures.SourceMapping(hairId);
            if(sourceTextures.MappingCount!=1||mapping==null||
               mapping.Evidence!="archive-triplet-authoring-provisional"||
               mapping.Diffuse!=0x0029A877u||
               mapping.Normal!=0x0029A879u||
               mapping.Specular!=0x0029A87Bu||
               mapping.Tint!=0x0029A87Du)
                throw new InvalidDataException(
                    "Original male hair authoring-shared DDS candidate IDs changed");
            _candidateHairMaterial=sourceTextures.LoadAlphaCutoutMaterial(hairId)??
                throw new InvalidDataException(
                    "Original female-authoring shared hair DDS assets could not decode");
        }
        _neutralHairMaterial??=new StandardMaterial3D
        {
            AlbedoColor=new Color("#86878a"),
            Roughness=0.82f,
            CullMode=BaseMaterial3D.CullModeEnum.Disabled
        };
        var instance=new MeshInstance3D
        {
            Name="OriginalSourceHair_0029B2AF",
            Mesh=mesh,Skin=skin,Skeleton=new NodePath(".."),
            MaterialOverride=_candidateHairMaterial
        };
        skeleton.AddChild(instance);
        _originalHairs.Add(instance);
        GD.Print($"PASS original ESO head/hair assembly: Head source 0x{headId:X8} "+
            "with HAR_M_HU_0343, 12/12 exact Granny bindings, "+
            "12/12 inverse-world rest matrices within 0.00001, "+
            "2841 original hair vertices, no attachment offset");
    }

    private static string RunSource(params string[] args)
    {
        foreach(string file in new[]{Helper,Granny,Manifest,Oodle})
            if(!File.Exists(file))
                throw new FileNotFoundException("Original ESO source missing: "+file);
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            UseShellExecute=false,CreateNoWindow=true,
            WorkingDirectory=Root,RedirectStandardError=true,
            RedirectStandardOutput=true
        };
        process.StartInfo.ArgumentList.Add(Helper);
        foreach(var arg in args)process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())throw new IOException("Cannot start original ESO source decoder");
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Original ESO face source decode timeout");
        }
        string output=stdout.GetAwaiter().GetResult();
        string error=stderr.GetAwaiter().GetResult();
        if(process.ExitCode!=0)
            throw new InvalidDataException("Original face extraction failed: "+
                error[..Math.Min(error.Length,500)]);
        return output;
    }

    private void SetupView()
    {
        AddChild(new WorldEnvironment{Environment=new Godot.Environment
        {
            BackgroundMode=Godot.Environment.BGMode.Color,
            BackgroundColor=new Color("#1b252c"),
            AmbientLightSource=Godot.Environment.AmbientSource.Color,
            AmbientLightColor=new Color("#9faab6")
        }});
        AddChild(new DirectionalLight3D{
            RotationDegrees=new Vector3(-30,-20,0),LightEnergy=1.4f
        });
        _camera=new Camera3D{Name="OriginalFaceCamera",Current=true,Fov=41};
        AddChild(_camera);
        var canvas=new CanvasLayer();
        AddChild(canvas);
        _hud=new Label{Name="SourceFaceInfo",Position=new Vector2(15,14)};
        _hud.AddThemeFontSizeOverride("font_size",16);
        canvas.AddChild(_hud);
        Input.MouseMode=Input.MouseModeEnum.Visible;
        UpdateView();
    }

    private void UpdateView()
    {
        if(_camera==null)return;
        Vector3 target=_focus<0
            ?new Vector3(0,2.08f,0)
            :_figures[_focus].Position+new Vector3(0,2.08f,0);
        float distance=_focus<0?_distance:Mathf.Clamp(_distance*0.45f,0.55f,1.7f);
        var angle=new Vector3(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),
            Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch));
        _camera.Position=target+angle*distance;
        _camera.LookAt(target,Vector3.Up);
        for(int i=0;i<_figures.Count;i++)
            _figures[i].Visible=_focus<0||_focus==i;
        if(_hud!=null)
            _hud.Text="ESO ORIGINAL FACES\n"+
                "6 source-skinned head meshes\n"+
                "No procedural facial features\n"+
                "No custom morphs applied\n"+
                "Original skin materials still missing\n"+
                "Hair: original DDS candidate (unverified)\n"+
                "T: compare textured/neutral hair\n"+
                "Y: original tint mask R (diagnostic)\n"+
                "1-6 focus on one\n"+
                "0 compare all\n"+
                "Drag to orbit\n"+
                "Wheel to zoom\n"+
                "Esc exit";
    }

    public override void _Process(double delta)
    {
        if(SelfTest)return;
        string? screenshot=System.Environment.GetEnvironmentVariable("ESO_ORIGINAL_FACES_PREVIEW");
        if(!_shot&&!string.IsNullOrWhiteSpace(screenshot)&&
           DisplayServer.GetName()!="headless"&&++_shotCount>=75)
        {
            _shot=true;
            GD.Print("ESO ORIGINAL FACE GALLERY PREVIEW "+
                GetViewport().GetTexture().GetImage().SavePng(screenshot)+" "+screenshot);
        }
    }

    public override void _Input(InputEvent evt)
    {
        if(SelfTest)return;
        if(evt is InputEventMouseButton mouse)
        {
            if(mouse.ButtonIndex==MouseButton.Left)_dragging=mouse.Pressed;
            if(mouse.Pressed&&mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                _distance=Mathf.Clamp(_distance*
                    (mouse.ButtonIndex==MouseButton.WheelUp?0.86f:1.17f),0.8f,4.3f);
                UpdateView();
            }
            return;
        }
        if(evt is InputEventMouseMotion move&&_dragging)
        {
            _yaw+=move.Relative.X*0.005f;
            _pitch=Mathf.Clamp(_pitch-move.Relative.Y*0.005f,-0.8f,0.8f);
            UpdateView();
            return;
        }
        if(evt is not InputEventKey key||!key.Pressed||key.Echo)return;
        switch(key.Keycode)
        {
            case Key.Key0:_focus=-1;UpdateView();break;
            case Key.Key1 or Key.Key2 or Key.Key3 or Key.Key4 or Key.Key5 or Key.Key6:
                _focus=(int)key.Keycode-(int)Key.Key1;
                UpdateView();break;
            case Key.Y:
                _hairTintMaskVisible=!_hairTintMaskVisible;
                _candidateHairMaterial?.SetShaderParameter("tint_debug_view",
                    _hairTintMaskVisible?1:0);
                break;
            case Key.T:
                _candidateHairVisible=!_candidateHairVisible;
                foreach(var hair in _originalHairs)
                    hair.MaterialOverride=_candidateHairVisible?
                        _candidateHairMaterial:_neutralHairMaterial;
                break;
            case Key.Escape:GetTree().Quit();break;
        }
    }
}
