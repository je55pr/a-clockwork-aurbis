using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using Process = System.Diagnostics.Process;

namespace EsoTest;

/// <summary>Real ESO torso/head skin bind-pose test. All source bytes are
/// local and ignored. Verifies that a pose change reaches named source bones
/// through Godot Skeleton3D and that each mesh's bone indices are retained.
/// This tests original skinning and a limited retarget of the actual
/// ESO PC_Walk clip; it is NOT the game's complete animation graph.</summary>
public partial class EsoSourceRigSmoke : Node3D
{
    private EsoArchive? _archive;
    private OodleDecoder? _decoder;
    private readonly List<Skeleton3D> _skeletons=new();
    [Export] public bool InteractivePreview {get;set;}
    private EsoSourceAnimationClip? _walking;
    private Skeleton3D? _animatedRig;
    private Skeleton3D? _faceRig;
    private int _facePreset=2;
    private readonly List<(Skeleton3D Rig,EsoSourceAnimationClip Clip)> _animatedPieces=new();
    private float _elapsed;
    private bool _playing=true;
    private bool _savedPreview;
    private int _previewFrames;
    public int ActiveRetargetedBones=>_walking?.MappedBoneCount??0;

    public override async void _Ready()
    {
        try
        {
            string root=ProjectSettings.GlobalizePath("res://");
            string eso=System.Environment.GetEnvironmentVariable("ESO_ROOT")??
                "C:/ESO/The Elder Scrolls Online";
            _archive=new EsoArchive(Path.Combine(eso,"depot","eso.mnf"));
            _decoder=OodleDecoder.FromEnvironment(Path.Combine(root,".research","ooz.dll"))
                ??throw new InvalidDataException("Local Oodle decoder unavailable");
            var torso=CreateSkin(0x000DEF03,"Torso_",
                InteractivePreview?new Vector3(0,0,-4f):new Vector3(-0.9f,0,-4f));
            var head=CreateSkin(0x000E2CFF,"Head_004",
                InteractivePreview?new Vector3(0,0,-4f):new Vector3(0.9f,0,-4f));
            if(torso.Skeleton.GetBoneCount()!=43 || head.Skeleton.GetBoneCount()!=39)
                throw new Exception("Source rig bone counts changed");
            foreach(var pair in new[]{torso,head})
            {
                if(pair.Mesh.Skin.GetBindCount()!=pair.Source.BindingNames.Length)
                    throw new Exception("Missing native skin binding");
                var arrays=((ArrayMesh)pair.Mesh.Mesh).SurfaceGetArrays(0);
                var influence=(int[])arrays[(int)Mesh.ArrayType.Bones];
                var weights=(float[])arrays[(int)Mesh.ArrayType.Weights];
                if(influence.Length!=pair.Source.VertexCount*4 || weights.Length!=influence.Length)
                    throw new Exception("Source skin vertex buffers missing");
            }
            int arm=torso.Skeleton.FindBone("defBn_upperArm_R");
            int jaw=head.Skeleton.FindBone("H_jaw3");
            int face=head.Skeleton.FindBone("H");
            if(arm<0||jaw<0||face<0)throw new Exception("Source pose or face control joints missing");
            ApplyFacePreset(head.Skeleton,1);
            if(head.Skeleton.GetBonePoseScale(face).X >= 0.98f)
                throw new Exception("Original head source bone scaling did not apply");
            ApplyFacePreset(head.Skeleton,2);
            var before=torso.Skeleton.GetBoneGlobalPose(arm);
            torso.Skeleton.SetBonePoseRotation(arm,new Quaternion(Vector3.Right,0.35f));
            head.Skeleton.SetBonePoseRotation(jaw,new Quaternion(Vector3.Right,0.10f));
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            var after=torso.Skeleton.GetBoneGlobalPose(arm);
            if(before.Basis.IsEqualApprox(after.Basis))
                throw new Exception("Source bone pose did not respond to rotation");
            string clipFile=Path.Combine(ProjectSettings.GlobalizePath("res://"),
                ".research","npc-rigs","clip-0008942F-source24.json");
            if(!File.Exists(clipFile))ExportOriginalWalk(clipFile);
            var walk=EsoSourceAnimationClip.Read(clipFile,torso.Skeleton);
            if(walk.Name!="PC_Walk"||walk.MappedBoneCount<5)
                throw new Exception($"Source walk clip isn't compatible: {walk.Name}, {walk.MappedBoneCount} mapped bones");
            walk.Apply(torso.Skeleton,0f);
            var restRotation=torso.Skeleton.GetBoneRest(arm).Basis.GetRotationQuaternion();
            if(!torso.Skeleton.GetBonePoseRotation(arm).IsEqualApprox(restRotation))
                throw new Exception("Source retarget corrupted ESO bind pose at animation frame zero");
            var firstPose=torso.Skeleton.GetBoneGlobalPose(arm);
            walk.Apply(torso.Skeleton,0.35f);
            var secondPose=torso.Skeleton.GetBoneGlobalPose(arm);
            if(firstPose.Basis.IsEqualApprox(secondPose.Basis))
                throw new Exception("Original PC_Walk sampled curves did not animate the upper arm");
            GD.Print($"PASS source ESO skinned meshes: torso {torso.Source.VertexCount} vertices / 43 bones, " +
                $"head {head.Source.VertexCount} vertices / 39 bones, source PC_Walk retargeted {walk.MappedBoneCount} bones");
            if(InteractivePreview)
            {
                _animatedRig=torso.Skeleton;
                _faceRig=head.Skeleton;
                _walking=walk;
                _animatedPieces.Add((torso.Skeleton,walk));
                _elapsed=0;
                // A full seven-part test body; individual original garment
                // files contain their own subset skeletons and skin tables.
                // Keep parts independently skinned until the canonical
                // shared humanoid skeleton has been reconstructed.
                foreach(var (id,prefix) in new[]{
                    (0x000DEE23u,"Feet_"),
                    (0x000DEE5Bu,"LegsLower_"),
                    (0x000DEE9Du,"LegsUpper_"),
                    (0x000DEDDBu,"ArmsUpper_"),
                    (0x000DEDA1u,"ArmsLower_")
                })
                {
                    var part=CreateSkin(id,prefix,new Vector3(0,0,-4));
                    var clip=EsoSourceAnimationClip.Read(clipFile,part.Skeleton);
                    _animatedPieces.Add((part.Skeleton,clip));
                }
                head.Skeleton.ResetBonePoses();
                ConfigureInteractivePreview();
                // The rest pose is trustworthy. Partial source-clip retargeting
                // is opt-in until its coordinate bases are fully reconciled.
                _playing=System.Environment.GetEnvironmentVariable("ESO_RIG_AUTOPLAY")=="1";
                foreach(var piece in _animatedPieces)piece.Rig.ResetBonePoses();
                SetProcess(true);
                return;
            }
            string? screenshot=System.Environment.GetEnvironmentVariable("ESO_RIG_PREVIEW_PATH");
            if(!string.IsNullOrEmpty(screenshot)&&DisplayServer.GetName()!="headless")
            {
                AddChild(new DirectionalLight3D{
                    RotationDegrees=new Vector3(-45,22,0),LightEnergy=1.4f});
                AddChild(new WorldEnvironment{Environment=new Godot.Environment{
                    BackgroundMode=Godot.Environment.BGMode.Color,
                    BackgroundColor=new Color("#344353"),
                    AmbientLightSource=Godot.Environment.AmbientSource.Color,
                    AmbientLightColor=Colors.White,AmbientLightEnergy=0.9f}});
                var camera=new Camera3D{Current=true,Fov=34,Position=new Vector3(0,1.8f,4.2f)};
                AddChild(camera);camera.LookAt(new Vector3(0,1.6f,-4));
                for(int i=0;i<5;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                GD.Print($"RIG PREVIEW {GetViewport().GetTexture().GetImage().SavePng(screenshot)} {screenshot}");
            }
            GetTree().Quit(0);
        }
        catch(Exception e)
        {
            GD.PushError("Source ESO rig smoke failed: "+e);
            GetTree().Quit(1);
        }
    }

    private void ConfigureInteractivePreview()
    {
        AddChild(new DirectionalLight3D{
            RotationDegrees=new Vector3(-48,35,0),LightEnergy=1.65f});
        AddChild(new WorldEnvironment{Environment=new Godot.Environment{
            BackgroundMode=Godot.Environment.BGMode.Color,
            BackgroundColor=new Color("#344353"),
            AmbientLightSource=Godot.Environment.AmbientSource.Color,
            AmbientLightColor=Colors.White,AmbientLightEnergy=0.85f}});
        var camera=new Camera3D{Current=true,Fov=42,
            Position=new Vector3(0,2.1f,3.5f)};
        AddChild(camera);camera.LookAt(new Vector3(0,1.4f,-4f));
        var hud=new CanvasLayer{Name="RigLabHUD"};
        AddChild(hud);
        var hint=new Label
        {
            Position=new Vector2(22,22),
            Text="ESO SOURCE RIG LAB  |  PC_Walk source curve samples\n"+
                 "Seven original ESO body-part meshes, each with original skin weights\n"+
                 "Head: genuine ESO bind pose + facial control bones (not canonical character)\n"+
                 "Space toggle experimental limb motion | 1/2/3 face shape tests | Esc exit\n"+
                 "Character materials and complete animation retargeting still under investigation",
        };
        hint.AddThemeFontSizeOverride("font_size",17);
        hud.AddChild(hint);
        Input.MouseMode=Input.MouseModeEnum.Visible;
    }

    public override void _Process(double delta)
    {
        if(!InteractivePreview||_walking==null||_animatedRig==null)return;
        if(_playing)
        {
            _elapsed+=(float)delta;
            foreach(var part in _animatedPieces)
                part.Clip.Apply(part.Rig,_elapsed);
        }
        if(!_savedPreview && ++_previewFrames>100)
        {
            string? save=System.Environment.GetEnvironmentVariable("ESO_RIG_PREVIEW_PATH");
            if(!string.IsNullOrWhiteSpace(save) && DisplayServer.GetName()!="headless")
            {
                _savedPreview=true;
                var status=GetViewport().GetTexture().GetImage().SavePng(save);
                GD.Print($"RIG LAB PREVIEW {status} {save}");
            }
        }
    }

    private static void ApplyFacePreset(Skeleton3D face,int preset)
    {
        // Source H and H_jaw3 bones genuinely influence head vertices.
        // These are diagnostic morphology controls, not recovered original
        // NPC complexion or character-creator slider settings.
        int skull=face.FindBone("H"), jaw=face.FindBone("H_jaw3");
        if(skull<0||jaw<0)return;
        var headScale=preset switch
        {
            1=>new Vector3(0.93f,1.025f,0.97f),
            3=>new Vector3(1.065f,0.982f,1.025f),
            _=>Vector3.One
        };
        var jawScale=preset switch
        {
            1=>new Vector3(0.95f,1f,1f),
            3=>new Vector3(1.04f,1f,1f),
            _=>Vector3.One
        };
        face.SetBonePoseScale(skull,headScale);
        face.SetBonePoseScale(jaw,jawScale);
    }

    public override void _Input(InputEvent evt)
    {
        if(!InteractivePreview||evt is not InputEventKey key||!key.Pressed||key.Echo)return;
        if(key.Keycode==Key.Space)_playing=!_playing;
        else if(key.Keycode is Key.Key1 or Key.Key2 or Key.Key3)
        {
            _facePreset=key.Keycode switch {Key.Key1=>1,Key.Key3=>3,_=>2};
            if(_faceRig!=null)ApplyFacePreset(_faceRig,_facePreset);
        }
        else if(key.Keycode==Key.Escape)GetTree().Quit();
    }

    private void ExportOriginalWalk(string output)
    {
        string root=ProjectSettings.GlobalizePath("res://");
        string helper=Path.Combine(root,"tools","ModelGeometry","bin","Debug","net8.0",
            "ModelGeometry.dll");
        string granny=System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
            Path.Combine(root,".research","uesp-esoapps","EsoExtractData",
                "EsoExtractData","granny2_x64.dll");
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardError=true,RedirectStandardOutput=true
        };
        foreach(var arg in new[]{helper,"--sample-animation",granny,
            "0008942F",_archive!.SourcePath,_decoder!.LibraryPath,output,"24"})
            process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())throw new IOException("Cannot sample original source animation");
        var outputTask=process.StandardOutput.ReadToEndAsync();
        var errorTask=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Original ESO animation sampler exceeded 45 seconds");
        }
        _=outputTask.GetAwaiter().GetResult();
        string errors=errorTask.GetAwaiter().GetResult();
        if(process.ExitCode!=0||!File.Exists(output))
            throw new InvalidDataException("Original source clip sample failed: "+
                errors[..Math.Min(errors.Length,300)]);
    }

    private (EsoSourceRig Source,Skeleton3D Skeleton,MeshInstance3D Mesh)
        CreateSkin(uint id,string prefix,Vector3 position)
    {
        var record=_archive!.FindId(id).First(r=>
            _archive.GetIdIndexFields(r.Ordinal).Flags==0);
        string root=ProjectSettings.GlobalizePath("res://");
        string folder=Path.Combine(root,".research","npc-rigs");
        Directory.CreateDirectory(folder);
        string basename=$"{id:X8}-a{record.ArchiveIndex}-o{record.Offset}-"+
            $"h{record.Hash:X8}-u{record.UncompressedSize}";
        string skinFile=Path.Combine(folder,basename+"-skin-v1.json");
        string meshFile=Path.Combine(folder,basename+"-mesh-v5.esm");
        if(!File.Exists(skinFile))
            RunHelper(new[]{"--inspect-rig",id.ToString("X8")},skinFile);
        if(!File.Exists(meshFile))
            RunHelper(new[]{id.ToString("X8"),meshFile},null);
        var rig=EsoSourceRig.Read(skinFile);
        var mesh=EsoHumanoidVisualLayer.LoadMesh(meshFile,prefix,rig);
        var pair=rig.BuildSkeleton();
        pair.Skeleton.Name=$"Skeleton_{id:X8}";
        pair.Skeleton.Position=position;
        AddChild(pair.Skeleton);
        var instance=new MeshInstance3D
        {
            Name=$"SkinnedSource_{id:X8}",
            Mesh=mesh, Skin=pair.Skin,Skeleton=new NodePath(".."),
            MaterialOverride=new StandardMaterial3D{
                AlbedoColor=new Color("#b3a18a"),Roughness=0.85f,
                CullMode=BaseMaterial3D.CullModeEnum.Disabled}
        };
        pair.Skeleton.AddChild(instance);
        _skeletons.Add(pair.Skeleton);
        return (rig,pair.Skeleton,instance);
    }

    private void RunHelper(string[] args,string? output)
    {
        string root=ProjectSettings.GlobalizePath("res://");
        string helper=Path.Combine(root,"tools","ModelGeometry","bin","Debug","net8.0",
            "ModelGeometry.dll");
        string granny=System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
            Path.Combine(root,".research","uesp-esoapps","EsoExtractData",
                "EsoExtractData","granny2_x64.dll");
        if(!File.Exists(helper)||!File.Exists(granny))
            throw new FileNotFoundException("Source rig helper or native decoder absent");
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        process.StartInfo.ArgumentList.Add(helper);
        if(args[0]=="--inspect-rig")process.StartInfo.ArgumentList.Add(args[0]);
        process.StartInfo.ArgumentList.Add(granny);
        process.StartInfo.ArgumentList.Add(args[args[0]=="--inspect-rig"?1:0]);
        if(args[0]!="--inspect-rig")process.StartInfo.ArgumentList.Add(args[1]);
        process.StartInfo.ArgumentList.Add(_archive!.SourcePath);
        process.StartInfo.ArgumentList.Add(_decoder!.LibraryPath);
        if(!process.Start())throw new IOException("Cannot start isolated GR2 helper");
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Source skin extraction timed out");
        }
        string result=stdout.GetAwaiter().GetResult();
        string error=stderr.GetAwaiter().GetResult();
        if(process.ExitCode!=0)throw new IOException("Source skin extraction: "+
            error[..Math.Min(error.Length,300)]);
        if(output!=null)
        {
            string tmp=output+".tmp";
            File.WriteAllText(tmp,result);
            File.Move(tmp,output,true);
        }
    }

    public override void _ExitTree()
    {
        _decoder?.Dispose();
        _archive?.Dispose();
    }
}
