using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Process=System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// Six independently animated copies of ESO's original VoriplasticCorpse
/// (UESP names it "Voriplasmic Corpse"). The geometry is source asset
/// 0015D6CD, all six source clips are evaluated using the installed ESO
/// Granny library, and every one of the 81 original skin bindings resolves
/// exactly to source skeleton 000020A2. No guessed retarget or made-up gait.
/// Each clip runs at its own original duration and 60 Hz native sample rate.
/// Source assets and native pose caches stay in ignored .research/.
/// </summary>
public partial class EsoOriginalBodyWalkComparisonLab:Node3D
{
    private const int BoneCount=202, VertexCount=8109, SkinBindCount=81, Hz=60;
    private const string MeshId="0015D6CD", SkeletonId="000020A2";
    private static readonly (string Id,string Name)[] Walks={
        ("0004C319","GEN_Walk"),
        ("0000D200","1H_Walk"),
        ("0002C3EF","1HShield_Walk"),
        ("0000D20E","2HS_Walk"),
        ("00011BBB","2HL_Walk"),
        ("0002DB0F","Bow_Walk")
    };
    [Export] public bool SelfTest {get;set;}
    private readonly record struct NativeBone(Vector3 Position,Quaternion Rotation);
    private sealed record Pose(float Time,NativeBone[] Bones,Vector3[] World);
    private sealed class AnimatedFigure
    {
        public required string Id,Name;
        public required Node3D Container;
        public required Skeleton3D Skeleton;
        public required MeshInstance3D Body;
        public required Pose[] Poses;
        public float Duration;
        public int CurrentFrame;
    }
    private readonly List<AnimatedFigure> _figures=new();
    private Camera3D? _camera;
    private Label? _hud;
    private float _elapsed,_yaw,_pitch,_distance=9.7f;
    private bool _playing=true,_dragging,_screenshot;
    private int _focus=-1,_screenshotFrames;
    private float _greatestSourceDeviation;
    public int OriginalFigureCount=>_figures.Count;
    public float GreatestSourceDeviation=>_greatestSourceDeviation;
    public int GetClipFrame(int index)=>_figures[index].CurrentFrame;
    public string GetClipName(int index)=>_figures[index].Name;
    public Vector3 GetClipBonePosition(int index,string name)
    {
        var figure=_figures[index];
        int bone=figure.Skeleton.FindBone(name);
        if(bone<0)throw new ArgumentException("Original source bone missing: "+name);
        return figure.Skeleton.GetBoneGlobalPose(bone).Origin;
    }
    private static string Root=>ProjectSettings.GlobalizePath("res://");
    private static string Cache=>Path.Combine(Root,".research","npc-rigs");
    private static string Helper=>Path.Combine(Root,"tools","ModelGeometry",
        "bin","Debug","net8.0","ModelGeometry.dll");
    private static string Granny=>System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
        @"C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll";
    private static string Manifest=>System.Environment.GetEnvironmentVariable("ESO_MNF")??
        @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
    private static string Oodle=>Path.Combine(Root,".research","ooz.dll");
    private static string SourceMeshRig=>Path.Combine(Cache,"original-external-mesh-"+MeshId+".json");
    private static string SourceMeshGeo=>Path.Combine(Cache,"original-external-mesh-"+MeshId+".esm");
    private static string SourceSkeleton=>Path.Combine(Cache,"animation-skeleton-"+SkeletonId+".json");
    private static string ClipCache(string id)=>Path.Combine(Cache,
        $"native-source-{SkeletonId}-{id}-world-v2-{Hz}fps.json");

    public override void _Ready()
    {
        try
        {
            EnsureSources();
            string nativeBinding=RunHelper("--inspect-native-external-binding",
                Granny,MeshId,SkeletonId,Manifest,Oodle);
            if(!nativeBinding.Contains(
                "total=81, exact=81, name-differing=0, index-unmapped=0",
                StringComparison.Ordinal)||
                !nativeBinding.Contains("COMPATIBLE_SOURCE_RIG:",StringComparison.Ordinal))
                throw new InvalidDataException("Original ESO mesh did not bind natively (81/81)");
            var source=EsoSourceRig.ReadExternal(SourceMeshRig,SourceSkeleton);
            if(source.MeshName!="VoriplasticCorpse"||
               source.VertexCount!=VertexCount||
               source.Bones.Count!=BoneCount||
               source.BindingNames.Length!=SkinBindCount||
               source.BindingNames.Any(n=>!n.StartsWith("mshBn_",StringComparison.Ordinal)))
                throw new InvalidDataException("Original ESO corpse mesh/rig identity mismatch");
            ArrayMesh sharedMesh=EsoHumanoidVisualLayer.LoadMesh(
                SourceMeshGeo,"VoriplasticCorpse",source);
            if(sharedMesh.GetSurfaceCount()!=1||
               sharedMesh.SurfaceGetArrayLen(0)!=VertexCount)
                throw new InvalidDataException("Original 8109 source vertices unavailable");

            for(int i=0;i<Walks.Length;i++)
            {
                var (id,name)=Walks[i];
                (Pose[] poses,float duration)=ReadClip(id,name,source);
                var (skeleton,skin)=source.BuildSkeleton();
                if(skeleton.GetBoneCount()!=BoneCount||skin.GetBindCount()!=SkinBindCount)
                    throw new InvalidDataException("Original 202-bone skeleton skin changed");
                var container=new Node3D
                {
                    Name="SourceWalk_"+name,
                    Position=new Vector3(((i%3)-1)*2.75f,i<3?1.45f:-1.45f,0)
                };
                AddChild(container);
                skeleton.Name="Original202BoneRig";
                container.AddChild(skeleton);
                var body=new MeshInstance3D
                {
                    Name="OriginalVoriplasticCorpse",
                    Mesh=sharedMesh,
                    Skin=skin,
                    Skeleton=new NodePath(".."),
                    // Diagnostic neutral colour only. The source's actual
                    // external ESO material has not been identified.
                    MaterialOverride=new StandardMaterial3D
                    {
                        AlbedoColor=new Color("#bdbeb4"),
                        Roughness=0.84f,
                        CullMode=BaseMaterial3D.CullModeEnum.Disabled
                    }
                };
                skeleton.AddChild(body);
                var caption=new Label3D
                {
                    Name="OriginalAnimationLabel",
                    Text=$"{i+1}. {name}\n0x{id}",
                    FontSize=42,
                    PixelSize=0.0033f,
                    Position=new Vector3(0,2.45f,0),
                    Modulate=new Color("#e2ece4")
                };
                container.AddChild(caption);
                _figures.Add(new AnimatedFigure
                {
                    Id=id,Name=name,Container=container,
                    Skeleton=skeleton,Body=body,Poses=poses,
                    Duration=duration
                });
                ApplyPose(_figures[^1],0);
            }

            VerifySourceMovement();
            if(SelfTest)
            {
                VerifyEveryNativePose();
                GD.Print($"PASS six original ESO Voriplasmic Corpse walks: "+
                    $"{_figures.Count} separate source-skinned figures, "+
                    $"{VertexCount} original vertices per figure, "+
                    $"{SkinBindCount}/81 Granny exact bindings each, "+
                    $"{BoneCount} native bones each, "+
                    $"max Granny/Godot source joint position error {_greatestSourceDeviation:F7}m.");
                GetTree().Quit(0);
                return;
            }
            BuildView();
            UpdateVisibleFigures();
            SetProcess(true);
        }
        catch(Exception error)
        {
            GD.PushError("Original ESO full-mesh six-walk comparison failed closed: "+error);
            GetTree().Quit(1);
        }
    }

    private static string RunHelper(params string[] args)
    {
        foreach(string dep in new[]{Helper,Granny,Manifest,Oodle})
            if(!File.Exists(dep))
                throw new FileNotFoundException("Installed original ESO source missing: "+dep);
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            UseShellExecute=false,
            CreateNoWindow=true,
            WorkingDirectory=Root,
            RedirectStandardOutput=true,
            RedirectStandardError=true
        };
        process.StartInfo.ArgumentList.Add(Helper);
        foreach(string arg in args)process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())throw new IOException("Original ESO source decoder unavailable");
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Original ESO Granny decoder exceeded 45 seconds");
        }
        string output=stdout.GetAwaiter().GetResult();
        string error=stderr.GetAwaiter().GetResult();
        if(process.ExitCode!=0)
            throw new InvalidDataException($"Original ESO source decoder refused: "+
                error[..Math.Min(500,error.Length)]+
                output[..Math.Min(150,output.Length)]);
        return output;
    }

    private static void EnsureSources()
    {
        Directory.CreateDirectory(Cache);
        if(!File.Exists(SourceMeshRig))
            File.WriteAllText(SourceMeshRig,RunHelper(
                "--inspect-rig",Granny,MeshId,Manifest,Oodle));
        if(!File.Exists(SourceMeshGeo))
            RunHelper(Granny,MeshId,SourceMeshGeo,Manifest,Oodle);
        if(!File.Exists(SourceSkeleton))
            File.WriteAllText(SourceSkeleton,RunHelper(
                "--inspect-rig",Granny,SkeletonId,Manifest,Oodle));
        foreach(var (id,_) in Walks)
            if(!File.Exists(ClipCache(id)))
                RunHelper("--sample-native-pose",Granny,SkeletonId,
                    id,Manifest,Oodle,ClipCache(id),Hz.ToString());
    }

    private static (Pose[],float) ReadClip(string clipId,string clipName,EsoSourceRig rig)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(ClipCache(clipId)));
        var root=document.RootElement;
        if(root.GetProperty("SkeletonId").GetString()!=SkeletonId||
           root.GetProperty("SkeletonName").GetString()!="GrannyRootBone"||
           root.GetProperty("AnimationId").GetString()!=clipId||
           root.GetProperty("AnimationName").GetString()!=clipName||
           root.GetProperty("FrameRate").GetInt32()!=Hz)
            throw new InvalidDataException("Original source rig/animation identity mismatch: "+clipName);
        var names=root.GetProperty("Bones").EnumerateArray().ToArray();
        if(names.Length!=BoneCount)
            throw new InvalidDataException("Original native rig has incomplete bone table");
        for(int bone=0;bone<BoneCount;bone++)
            if(names[bone].GetProperty("Name").GetString()!=rig.Bones[bone].Name||
               names[bone].GetProperty("Parent").GetInt32()!=rig.Bones[bone].Parent)
                throw new InvalidDataException("Native source bone order/hierarchy mismatch");
        float duration=root.GetProperty("DurationSeconds").GetSingle();
        if(!float.IsFinite(duration)||duration is <0.5f or >2f)
            throw new InvalidDataException("Invalid source walk duration for "+clipName);
        var frames=new List<Pose>();
        foreach(var frame in root.GetProperty("Frames").EnumerateArray())
        {
            float t=frame.GetProperty("Time").GetSingle();
            var local=frame.GetProperty("Bones");
            var world=frame.GetProperty("WorldPositions");
            if(!float.IsFinite(t)||t<0||t>duration+0.0001f||
               (frames.Count>0&&t<=frames[^1].Time)||
               local.GetArrayLength()!=BoneCount||world.GetArrayLength()!=BoneCount)
                throw new InvalidDataException("Native source sample invalid for "+clipName);
            var sources=new NativeBone[BoneCount];
            var positions=new Vector3[BoneCount];
            for(int bone=0;bone<BoneCount;bone++)
            {
                var position=local[bone].GetProperty("Position");
                var orientation=local[bone].GetProperty("Orientation");
                var shear=local[bone].GetProperty("ScaleShear");
                var target=world[bone];
                if(position.GetArrayLength()!=3||orientation.GetArrayLength()!=4||
                   shear.GetArrayLength()!=9||target.GetArrayLength()!=3)
                    throw new InvalidDataException("Native transform dimension mismatch");
                Vector3 p=new(position[0].GetSingle(),position[1].GetSingle(),position[2].GetSingle());
                Quaternion q=new(orientation[0].GetSingle(),orientation[1].GetSingle(),
                    orientation[2].GetSingle(),orientation[3].GetSingle());
                Vector3 w=new(target[0].GetSingle(),target[1].GetSingle(),target[2].GetSingle());
                if(!p.IsFinite()||!w.IsFinite()||!q.IsNormalized())
                    throw new InvalidDataException("Original source contains invalid native bone transforms");
                for(int k=0;k<9;k++)
                    if(!float.IsFinite(shear[k].GetSingle())||
                       Math.Abs(shear[k].GetSingle()-(k%4==0?1f:0f))>0.0005f)
                        throw new InvalidDataException("Original ESO source requires unsupported bone shear");
                sources[bone]=new NativeBone(p,q);
                positions[bone]=w;
            }
            frames.Add(new Pose(t,sources,positions));
        }
        if(frames.Count is <45 or >130||
           Math.Abs(frames[^1].Time-duration)>0.0002f)
            throw new InvalidDataException("Native source walk samples incomplete: "+clipName);
        return (frames.ToArray(),duration);
    }

    private static void ApplyPose(AnimatedFigure figure,int frame)
    {
        int selected=Math.Clamp(frame,0,figure.Poses.Length-1);
        var joints=figure.Poses[selected].Bones;
        for(int i=0;i<BoneCount;i++)
        {
            figure.Skeleton.SetBonePosePosition(i,joints[i].Position);
            figure.Skeleton.SetBonePoseRotation(i,joints[i].Rotation);
        }
        figure.CurrentFrame=selected;
    }

    private void VerifySourceMovement()
    {
        int index=_figures[0].Skeleton.FindBone("mshBn_hand_L");
        int foot=_figures[0].Skeleton.FindBone("mshBn_foot_L");
        if(index<0||foot<0)
            throw new InvalidDataException("Original source humanoid joint missing");
        foreach(var figure in _figures)
        {
            int quarter=figure.Poses.Length/4;
            if(figure.Poses[0].World[foot].DistanceTo(figure.Poses[quarter].World[foot])<0.1f)
                throw new InvalidDataException("Missing original walking leg motion: "+figure.Name);
        }
        Vector3 left=_figures[0].Poses[_figures[0].Poses.Length/4].World[index];
        Vector3 twoHanded=_figures[3].Poses[_figures[3].Poses.Length/4].World[index];
        if(left.DistanceTo(twoHanded)<0.20f)
            throw new InvalidDataException("GEN and 2HS native hand poses unexpectedly coincide");
    }

    private void VerifyEveryNativePose()
    {
        foreach(var figure in _figures)
        {
            foreach(var index in Enumerable.Range(0,figure.Poses.Length))
            {
                ApplyPose(figure,index);
                for(int bone=0;bone<BoneCount;bone++)
                {
                    float deviation=figure.Skeleton.GetBoneGlobalPose(bone).Origin
                        .DistanceTo(figure.Poses[index].World[bone]);
                    _greatestSourceDeviation=Math.Max(_greatestSourceDeviation,deviation);
                    if(!float.IsFinite(deviation)||deviation>0.003f)
                        throw new InvalidDataException(
                            $"Native source world mismatch: {figure.Name}, bone {bone}, "+
                            $"frame {index}, deviation {deviation:F6}m");
                }
            }
            ApplyPose(figure,0);
        }
    }

    private void BuildView()
    {
        AddChild(new WorldEnvironment{Environment=new Godot.Environment
        {
            BackgroundMode=Godot.Environment.BGMode.Color,
            BackgroundColor=new Color("#17252c"),
            AmbientLightSource=Godot.Environment.AmbientSource.Color,
            AmbientLightColor=new Color("#a9b7bf")
        }});
        AddChild(new DirectionalLight3D
        {
            RotationDegrees=new Vector3(-42,30,0),
            LightEnergy=1.65f
        });
        _camera=new Camera3D{Name="OriginalBodyComparisonCamera",Current=true,Fov=43f};
        AddChild(_camera);
        var canvas=new CanvasLayer{Name="OriginalWalkComparisonHud"};
        AddChild(canvas);
        _hud=new Label{Name="SourceClipLabels",Position=new Vector2(15,12)};
        _hud.AddThemeFontSizeOverride("font_size",16);
        canvas.AddChild(_hud);
        Input.MouseMode=Input.MouseModeEnum.Visible;
        UpdateVisibleFigures();
    }

    private void UpdateVisibleFigures()
    {
        if(_camera==null)return;
        Vector3 target=_focus<0
            ?new Vector3(0,1.15f,0)
            :_figures[_focus].Container.Position+new Vector3(0,1.15f,0);
        float distance=_focus<0?_distance:Mathf.Clamp(_distance*0.43f,3f,7f);
        var axis=new Vector3(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),
            Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch));
        _camera.Position=target+axis*distance;
        _camera.LookAt(target,Vector3.Up);
        for(int i=0;i<_figures.Count;i++)
            _figures[i].Container.Visible=_focus<0||_focus==i;
    }

    private void UpdatePoses()
    {
        foreach(var figure in _figures)
        {
            float t=_elapsed%figure.Duration;
            if(t<0)t+=figure.Duration;
            ApplyPose(figure,(int)Math.Round(t*Hz));
        }
        if(_hud!=null)
            _hud.Text="ESO SOURCE WALK LAB\n"+
                "Voriplasmic Corpse\n"+
                "8,109 original vertices\n"+
                "81/81 native bindings\n"+
                "Six ESO animation clips\n"+
                (_playing?"PLAYING":"PAUSED")+"\n"+
                "1-6 focus one\n"+
                "0 compare all\n"+
                "Space pause\n"+
                "Left / Right step\n"+
                "Drag to orbit\n"+
                "Wheel to zoom\n"+
                "Esc exit\n"+
                "Source textures not mapped";
    }
    public override void _Process(double delta)
    {
        if(SelfTest)return;
        if(_playing)
        {
            _elapsed+=Mathf.Min((float)delta,0.1f);
            UpdatePoses();
        }
        string? screenshot=System.Environment.GetEnvironmentVariable("ESO_SIX_BODY_WALKS_PREVIEW");
        if(!_screenshot&&!string.IsNullOrWhiteSpace(screenshot)&&
           DisplayServer.GetName()!="headless"&&++_screenshotFrames>=75)
        {
            _screenshot=true;
            GD.Print("ESO SIX BODY WALKS PREVIEW "+
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
                    (mouse.ButtonIndex==MouseButton.WheelUp?0.85f:1.18f),7f,20f);
                UpdateVisibleFigures();
            }
            return;
        }
        if(evt is InputEventMouseMotion motion&&_dragging)
        {
            _yaw+=motion.Relative.X*0.005f;
            _pitch=Mathf.Clamp(_pitch-motion.Relative.Y*0.005f,-0.75f,0.75f);
            UpdateVisibleFigures();
            return;
        }
        if(evt is not InputEventKey key||!key.Pressed||key.Echo)return;
        switch(key.Keycode)
        {
            case Key.Space:
                _playing=!_playing;
                UpdatePoses();
                break;
            case Key.Key0:
                _focus=-1;
                UpdateVisibleFigures();
                break;
            case Key.Key1 or Key.Key2 or Key.Key3 or Key.Key4 or Key.Key5 or Key.Key6:
                _focus=(int)key.Keycode-(int)Key.Key1;
                UpdateVisibleFigures();
                break;
            case Key.Left:
                _playing=false;
                _elapsed=Math.Max(0,_elapsed-1f/Hz);
                UpdatePoses();
                break;
            case Key.Right:
                _playing=false;
                _elapsed+=1f/Hz;
                UpdatePoses();
                break;
            case Key.Escape:
                GetTree().Quit();
                break;
        }
    }
}
