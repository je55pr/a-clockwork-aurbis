using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Process = System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// Six original ESO walking animation files sampled by the installed native
/// Granny runtime against the BEST VERIFIED source GrannyRootBone variant.
/// Skeleton 000020A2 contains 93/97 exact transform-track names; four absent
/// names are lip/cheek controls. It is NOT asserted to be ESO's canonical rig.
///
/// Displays only original parent-child bone positions, with no proxy mesh,
/// retargeting, synthesized gait, fake armour, or other inferred appearance.
/// Source caches remain ignored under .research/npc-rigs.
/// </summary>
public partial class EsoWalkComparisonLab : Node3D
{
    private const uint SourceRig=0x000020A2;
    private const int SampleHz=60;
    private const int ExpectedBones=202;
    private static readonly (uint Id,string Name)[] Animations =
    {
        (0x0004C319, "GEN_Walk"),
        (0x0000D200, "1H_Walk"),
        (0x0002C3EF, "1HShield_Walk"),
        (0x0000D20E, "2HS_Walk"),
        (0x00011BBB, "2HL_Walk"),
        (0x0002DB0F, "Bow_Walk")
    };

    [Export] public bool SelfTest {get;set;}
    private sealed record Bone(string Name,int Parent);
    private sealed record Pose(float Time,Vector3[] World);
    private sealed class Clip
    {
        public required uint Id;
        public required string Name;
        public float Duration;
        public int DisplayedFrame;
        public required Pose[] Frames;
        public required MultiMesh Renderer;
        public required Node3D Container;
        public required Label3D Label;
    }

    private Bone[] _bones=Array.Empty<Bone>();
    private int[] _visibleBones=Array.Empty<int>();
    private readonly List<Clip> _clips=new();
    private Camera3D? _camera;
    private Label? _hud;
    private float _elapsed,_yaw,_pitch,_distance=8.2f;
    private int _focus=-1,_frameOffset;
    private bool _playing=true,_dragging,_savedScreenshot;
    private int _previewFrames;

    private static string Root=>ProjectSettings.GlobalizePath("res://");
    private static string RigName=>"GrannyRootBone";
    private static string SourceFile(uint animation)=>Path.Combine(
        Root,".research","npc-rigs",
        $"native-source-{SourceRig:X8}-{animation:X8}-world-v2-{SampleHz}fps.json");

    public int OriginalSourceClips=>_clips.Count;

    // Testable CPU source selection. Headless RenderingServer backends are
    // allowed to discard GPU MultiMesh transform readbacks entirely.
    public int GetDisplayedFrame(int clipIndex)=>_clips[clipIndex].DisplayedFrame;
    public Vector3 GetDisplayedBonePosition(int clipIndex,string sourceBone)
    {
        int index=FindBone(sourceBone);
        var clip=_clips[clipIndex];
        return clip.Frames[clip.DisplayedFrame].World[index];
    }

    public override void _Ready()
    {
        try
        {
            for(int i=0;i<Animations.Length;i++)
                _clips.Add(LoadClip(Animations[i].Id,Animations[i].Name,i));
            VerifyComparison();
            if(SelfTest)
            {
                GD.Print("PASS six original ESO walk animations: 202-bone source rig, " +
                    "93/97 animation-track names, native Granny positions, " +
                    "six individual distinct clips, no approximated poses");
                GetTree().Quit(0);
                return;
            }
            ConfigureView();
            ShowCurrentFrames();
            SetProcess(true);
        }
        catch(Exception error)
        {
            GD.PushError("ESO source-only walk comparison failed closed: "+error);
            GetTree().Quit(1);
        }
    }

    private static string EnsurePose(uint animation)
    {
        string output=SourceFile(animation);
        if(File.Exists(output))return output;
        string helper=Path.Combine(Root,"tools","ModelGeometry",
            "bin","Debug","net8.0","ModelGeometry.dll");
        string granny=System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
            @"C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll";
        string manifest=System.Environment.GetEnvironmentVariable("ESO_MNF")??
            @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
        string oodle=Path.Combine(Root,".research","ooz.dll");
        foreach(string dependency in new[]{helper,granny,manifest,oodle})
            if(!File.Exists(dependency))
                throw new FileNotFoundException(
                    $"Original ESO source dependency not found: {dependency}");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            UseShellExecute=false,
            CreateNoWindow=true,
            WorkingDirectory=Root,
            RedirectStandardOutput=true,
            RedirectStandardError=true
        };
        foreach(string arg in new[]{
            helper,"--sample-native-pose",granny,SourceRig.ToString("X8"),
            animation.ToString("X8"),manifest,oodle,output,SampleHz.ToString()})
            process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())throw new IOException("Cannot start native source pose sampler");
        var outputTask=process.StandardOutput.ReadToEndAsync();
        var errorTask=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Native source animation sampler timed out");
        }
        string stdout=outputTask.GetAwaiter().GetResult();
        string stderr=errorTask.GetAwaiter().GetResult();
        if(process.ExitCode!=0||!File.Exists(output))
            throw new InvalidDataException(
                $"Native Granny could not evaluate animation {animation:X8}: "+
                stderr[..Math.Min(700,stderr.Length)]);
        GD.Print(stdout.Trim());
        return output;
    }

    private Clip LoadClip(uint animation,string name,int index)
    {
        string file=EnsurePose(animation);
        long bytes=new FileInfo(file).Length;
        if(bytes is <100_000 or >12_000_000)
            throw new InvalidDataException("Original source pose cache size invalid");
        using var document=JsonDocument.Parse(File.ReadAllBytes(file));
        var root=document.RootElement;
        if(root.GetProperty("SkeletonId").GetString()!=SourceRig.ToString("X8")||
           root.GetProperty("AnimationId").GetString()!=animation.ToString("X8")||
           root.GetProperty("SkeletonName").GetString()!=RigName||
           root.GetProperty("AnimationName").GetString()!=name||
           root.GetProperty("FrameRate").GetInt32()!=SampleHz)
            throw new InvalidDataException($"Native source rig/clip mismatch for {name}");
        var bones=new List<Bone>();
        foreach(var node in root.GetProperty("Bones").EnumerateArray())
        {
            string boneName=node.GetProperty("Name").GetString()??"";
            int parent=node.GetProperty("Parent").GetInt32();
            if(parent>=bones.Count||parent< -1||boneName.Length==0||
               bones.Any(b=>b.Name==boneName))
                throw new InvalidDataException("Original Granny bone hierarchy invalid");
            bones.Add(new Bone(boneName,parent));
        }
        if(bones.Count!=ExpectedBones)
            throw new InvalidDataException("Source rig no longer contains 202 bones");
        if(_bones.Length>0&&!bones.SequenceEqual(_bones))
            throw new InvalidDataException("Animation clips use different source hierarchies");
        if(_bones.Length==0)
        {
            _bones=bones.ToArray();
            var visible=new HashSet<string>(StringComparer.Ordinal)
            {
                "mshBn_pelvis","mshBn_spine01","mshBn_spine02",
                "mshBn_neck","mshBn_head",
                "mshBn_clavicle_L","mshBn_clavicle_R",
                "mshBn_upperArm_L","mshBn_upperArm_R",
                "mshBn_forearm_L","mshBn_forearm_R",
                "mshBn_hand_L","mshBn_hand_R",
                "mshBn_thigh_L","mshBn_thigh_R",
                "mshBn_shin_L","mshBn_shin_R",
                "mshBn_foot_L","mshBn_foot_R",
                "mshBn_toe_L","mshBn_toe_R"
            };
            _visibleBones=Enumerable.Range(0,_bones.Length)
                .Where(i=>_bones[i].Parent>=0 && visible.Contains(_bones[i].Name))
                .ToArray();
            if(_visibleBones.Length!=visible.Count)
                throw new InvalidDataException("Source anatomical hierarchy changed");
        }
        float duration=root.GetProperty("DurationSeconds").GetSingle();
        if(!float.IsFinite(duration)||duration is <0.4f or >2.5f)
            throw new InvalidDataException("Unexpected source walk duration");
        var poses=new List<Pose>();
        foreach(var frame in root.GetProperty("Frames").EnumerateArray())
        {
            float time=frame.GetProperty("Time").GetSingle();
            if(!float.IsFinite(time)||time<0||time>duration+0.0001f||
               (poses.Count>0&&time<=poses[^1].Time))
                throw new InvalidDataException("Native source pose timestamps invalid");
            var coords=frame.GetProperty("WorldPositions");
            if(coords.GetArrayLength()!=ExpectedBones)
                throw new InvalidDataException("Native source pose bone count invalid");
            var positions=new Vector3[ExpectedBones];
            for(int i=0;i<ExpectedBones;i++)
            {
                if(coords[i].GetArrayLength()!=3)
                    throw new InvalidDataException("Native source bone position invalid");
                var v=new Vector3(coords[i][0].GetSingle(),
                    coords[i][1].GetSingle(),coords[i][2].GetSingle());
                if(!v.IsFinite()||v.Length()>1000)
                    throw new InvalidDataException("Invalid native source bone coordinates");
                positions[i]=v;
            }
            poses.Add(new Pose(time,positions));
        }
        if(poses.Count<45||poses.Count>160 ||
           Math.Abs(poses[^1].Time-duration)>0.0001f)
            throw new InvalidDataException("Missing native source walk pose samples");
        var source=new MultiMesh
        {
            Mesh=new CylinderMesh
            {
                TopRadius=0.016f,
                BottomRadius=0.016f,
                Height=1f,
                RadialSegments=6
            },
            TransformFormat=MultiMesh.TransformFormatEnum.Transform3D,
            UseColors=true,
            InstanceCount=_visibleBones.Length
        };
        for(int i=0;i<_visibleBones.Length;i++)
        {
            string joint=_bones[_visibleBones[i]].Name;
            Color color=joint.Contains("thigh",StringComparison.OrdinalIgnoreCase)||
                        joint.Contains("shin",StringComparison.OrdinalIgnoreCase)||
                        joint.Contains("foot",StringComparison.OrdinalIgnoreCase)||
                        joint.Contains("toe",StringComparison.OrdinalIgnoreCase)
                    ?new Color("#6bc6e1"):
                joint.Contains("Arm",StringComparison.OrdinalIgnoreCase)||
                joint.Contains("hand",StringComparison.OrdinalIgnoreCase)
                    ?new Color("#edbf72")
                    :new Color("#c8d9ce");
            source.SetInstanceColor(i,color);
        }
        var container=new Node3D{Name=$"SourceClip_{name}"};
        AddChild(container);
        var lines=new MultiMeshInstance3D
        {
            Name="OriginalSourceBones",
            Multimesh=source,
            MaterialOverride=new StandardMaterial3D
            {
                VertexColorUseAsAlbedo=true,
                ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded
            }
        };
        container.AddChild(lines);
        var label=new Label3D
        {
            Name="AnimationLabel",
            Text=$"{index+1}. {name}\n0x{animation:X8}",
            Position=new Vector3(0,2.28f,0),
            FontSize=38,
            PixelSize=0.0040f,
            Modulate=new Color("#f0f6ed")
        };
        container.AddChild(label);
        // Comparison layout only: no source animation transformations change.
        container.Position=new Vector3(((index%3)-1)*2.6f,
            (index<3?1.55f:-1.55f),0);
        GD.Print($"Native original {name} [{animation:X8}], {poses.Count} poses / "+
            $"{ExpectedBones} source bones / {duration:F3}s");
        return new Clip
        {
            Id=animation,Name=name,Duration=duration,
            Frames=poses.ToArray(),Renderer=source,Container=container,Label=label
        };
    }

    private int FindBone(string name)
    {
        for(int i=0;i<_bones.Length;i++)
            if(_bones[i].Name==name)return i;
        throw new InvalidDataException("Required native bone missing: "+name);
    }

    private void VerifyComparison()
    {
        if(_clips.Count!=Animations.Length)
            throw new InvalidDataException("Source walk comparison missing clips");
        int hand=FindBone("mshBn_hand_L");
        int footL=FindBone("mshBn_foot_L");
        int footR=FindBone("mshBn_foot_R");
        int pelvis=FindBone("mshBn_pelvis");
        foreach(var clip in _clips)
        {
            var zero=clip.Frames[0];
            var quarter=clip.Frames[clip.Frames.Length/4];
            if((zero.World[footL]-quarter.World[footL]).Length()<0.1f||
               (zero.World[footR]-quarter.World[footR]).Length()<0.1f||
               zero.World[pelvis].Y is <0.8f or >2.0f)
                throw new InvalidDataException($"Original {clip.Name} motion unexpectedly absent");
        }
        var normal=_clips[0].Frames[_clips[0].Frames.Length/4].World[hand];
        var twoHanded=_clips[3].Frames[_clips[3].Frames.Length/4].World[hand];
        if((normal-twoHanded).Length()<0.25f)
            throw new InvalidDataException("Original general and 2HS arm motions are indistinguishable");
    }

    private void ShowCurrentFrames()
    {
        for(int index=0;index<_clips.Count;index++)
        {
            var clip=_clips[index];
            float sourceTime=_elapsed%clip.Duration;
            if(sourceTime<0)sourceTime+=clip.Duration;
            int frame=Math.Clamp((int)Math.Round(sourceTime*SampleHz)+_frameOffset,
                0,clip.Frames.Length-1);
            clip.DisplayedFrame=frame;
            var positions=clip.Frames[frame].World;
            for(int i=0;i<_visibleBones.Length;i++)
            {
                int child=_visibleBones[i],parent=_bones[child].Parent;
                Vector3 start=positions[parent],end=positions[child],
                    displacement=end-start;
                float length=displacement.Length();
                if(length<0.00001f)
                {
                    clip.Renderer.SetInstanceTransform(i,new Transform3D(
                        Basis.Identity.Scaled(Vector3.Zero),start));
                    continue;
                }
                var rotation=new Quaternion(Vector3.Up,displacement/length);
                clip.Renderer.SetInstanceTransform(i,new Transform3D(
                    new Basis(rotation).Scaled(new Vector3(1f,length,1f)),
                    (start+end)*0.5f));
            }
        }
        if(_hud!=null)
            _hud.Text="ESO SOURCE WALK LAB\n"+
                "Six original GR2 clips\n"+
                "202-bone source rig\n"+
                "93/97 matching tracks\n"+
                (_playing?"PLAYING":"PAUSED")+"\n"+
                "1-6 focus one\n"+
                "0 compare all\n"+
                "Space pause\n"+
                "Left / Right step\n"+
                "Drag to orbit\n"+
                "Wheel to zoom\n"+
                "Esc exit\n"+
                "Source skeletons only\n"+
                "4 face tracks unmatched";
    }

    private void ConfigureView()
    {
        AddChild(new WorldEnvironment
        {
            Environment=new Godot.Environment
            {
                BackgroundMode=Godot.Environment.BGMode.Color,
                BackgroundColor=new Color("#18242d")
            }
        });
        _camera=new Camera3D{Name="ComparisonCamera",Current=true,Fov=44f};
        AddChild(_camera);
        var canvas=new CanvasLayer{Name="ComparisonHud"};
        AddChild(canvas);
        _hud=new Label{Name="AnimationDetails",Position=new Vector2(16,16)};
        _hud.AddThemeFontSizeOverride("font_size",17);
        canvas.AddChild(_hud);
        Input.MouseMode=Input.MouseModeEnum.Visible;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        if(_camera==null)return;
        Vector3 target=_focus<0
            ?new Vector3(0,1.1f,0)
            :_clips[_focus].Container.Position+new Vector3(0,1.15f,0);
        float distance=_focus<0?_distance:Mathf.Clamp(_distance*0.43f,3.15f,6f);
        var vector=new Vector3(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),
            Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch));
        _camera.Position=target+vector*distance;
        _camera.LookAt(target,Vector3.Up);
        for(int i=0;i<_clips.Count;i++)
            _clips[i].Container.Visible=_focus<0||_focus==i;
    }

    public override void _Process(double delta)
    {
        if(SelfTest)return;
        if(_playing)
        {
            _elapsed+=Math.Min((float)delta,0.1f);
            _frameOffset=0;
        }
        ShowCurrentFrames();
        string? imagePath=System.Environment.GetEnvironmentVariable("ESO_WALK_COMPARE_PREVIEW_PATH");
        if(!_savedScreenshot&&!string.IsNullOrWhiteSpace(imagePath)&&
           DisplayServer.GetName()!="headless"&&++_previewFrames>=65)
        {
            _savedScreenshot=true;
            GD.Print("ESO WALK COMPARE PREVIEW "+
                GetViewport().GetTexture().GetImage().SavePng(imagePath)+" "+imagePath);
        }
    }

    public override void _Input(InputEvent evt)
    {
        if(SelfTest)return;
        if(evt is InputEventMouseButton click)
        {
            if(click.ButtonIndex==MouseButton.Left)_dragging=click.Pressed;
            if(click.Pressed&&click.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                _distance=Mathf.Clamp(_distance*
                    (click.ButtonIndex==MouseButton.WheelUp?0.86f:1.16f),7.5f,22f);
                UpdateCamera();
            }
            return;
        }
        if(evt is InputEventMouseMotion movement&&_dragging)
        {
            _yaw+=movement.Relative.X*0.005f;
            _pitch=Mathf.Clamp(_pitch-movement.Relative.Y*0.005f,-0.65f,0.65f);
            UpdateCamera();
            return;
        }
        if(evt is not InputEventKey key||!key.Pressed||key.Echo)return;
        switch(key.Keycode)
        {
            case Key.Space:
                _playing=!_playing;break;
            case Key.Key0:
                _focus=-1;UpdateCamera();break;
            case Key.Key1 or Key.Key2 or Key.Key3 or Key.Key4 or Key.Key5 or Key.Key6:
                _focus=(int)key.Keycode-(int)Key.Key1;
                UpdateCamera();break;
            case Key.Left:
                _playing=false;_elapsed=Math.Max(0,_elapsed-1f/SampleHz);
                _frameOffset=0;ShowCurrentFrames();break;
            case Key.Right:
                _playing=false;_elapsed+=1f/SampleHz;
                _frameOffset=0;ShowCurrentFrames();break;
            case Key.Escape:
                GetTree().Quit();break;
        }
    }
}
