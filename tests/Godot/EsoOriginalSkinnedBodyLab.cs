using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Process=System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// A fully skinned, originally authored ESO creature/humanoid-model diagnostic.
/// The original source resource 0x0015D6CD ('VoriplasticCorpse') contains a
/// *complete* skinned mesh with 81 original mshBn_* bindings, but deliberately
/// no embedded skeleton. The installed Granny runtime independently confirms
/// all 81 source binding names resolve exactly onto source 0x000020A2.
///
/// Godot applies all 202 ORIGINAL native Granny poses, preserving the mesh's
/// source vertices, topology, binding indices, weights and inverse binds.
/// No defBn-to-mshBn alias, procedural walk, humanoid shape or made-up skin.
/// This is a diagnostic ESO-authored affected corpse, NOT a normal ESO NPC.
/// </summary>
public partial class EsoOriginalSkinnedBodyLab:Node3D
{
    private const uint MeshAsset=0x0015D6CD;
    private const uint SourceRig=0x000020A2;
    private const uint WalkClip=0x0004C319;
    private const int Fps=60;
    private const int SourceBones=202;
    private const int SkinBindings=81;
    private const int Vertices=8109;
    [Export] public bool SelfTest {get;set;}
    private readonly record struct NativeLocal(Vector3 Position,Quaternion Rotation);
    private sealed record SourceFrame(float Time,NativeLocal[] Local,Vector3[] World);
    private SourceFrame[] _frames=Array.Empty<SourceFrame>();
    private Skeleton3D? _skeleton;
    private MeshInstance3D? _body;
    private Camera3D? _camera;
    private Label? _label;
    private int _currentFrame,_screenshotTicks;
    private float _elapsed,_duration,_yaw=Mathf.Pi,_pitch,_distance=4.3f,_maximumWorldDifference;
    private bool _playing=true,_dragging,_shot;
    private static string Root=>ProjectSettings.GlobalizePath("res://");
    private static string Cache=>Path.Combine(Root,".research","npc-rigs");
    private static string Helper=>Path.Combine(Root,"tools","ModelGeometry",
        "bin","Debug","net8.0","ModelGeometry.dll");
    private static string Granny=>System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
        @"C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll";
    private static string Manifest=>System.Environment.GetEnvironmentVariable("ESO_MNF")??
        @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
    private static string Oodle=>Path.Combine(Root,".research","ooz.dll");
    private static string SourceMeshRig=>Path.Combine(Cache,
        $"original-external-mesh-{MeshAsset:X8}.json");
    private static string SourceMeshGeo=>Path.Combine(Cache,
        $"original-external-mesh-{MeshAsset:X8}.esm");
    private static string SourceSkeleton=>Path.Combine(Cache,
        "animation-skeleton-000020A2.json");
    private static string NativeFrames=>Path.Combine(Cache,
        $"native-source-{SourceRig:X8}-{WalkClip:X8}-world-v2-{Fps}fps.json");
    public int NativeBoneCount=>_skeleton?.GetBoneCount()??0;
    public int NativeBindingCount=>_body?.Skin?.GetBindCount()??0;
    public int OriginalVertexCount=>((ArrayMesh?)_body?.Mesh)?.SurfaceGetArrayLen(0)??0;
    public int CurrentFrame=>_currentFrame;
    public float MaximumWorldDifference=>_maximumWorldDifference;

    public override void _Ready()
    {
        try
        {
            EnsureSourceAssets();
            // Live names and bind indices have also been checked in the
            // independent ESO granny2 native mesh binding machinery.
            VerifyNativeExternalMeshBinding();
            EsoSourceRig rig=EsoSourceRig.ReadExternal(SourceMeshRig,SourceSkeleton);
            var (skeleton,skin)=rig.BuildSkeleton();
            if(rig.MeshName!="VoriplasticCorpse"||rig.VertexCount!=Vertices||
               rig.Bones.Count!=SourceBones||rig.BindingNames.Length!=SkinBindings||
               skeleton.GetBoneCount()!=SourceBones||skin.GetBindCount()!=SkinBindings)
                throw new InvalidDataException("Original VoriplasticCorpse source asset changed");
            if(rig.BindingNames.Distinct(StringComparer.Ordinal).Count()!=SkinBindings||
               rig.BindingNames.Any(b=>!b.StartsWith("mshBn_",StringComparison.Ordinal)))
                throw new InvalidDataException("Incomplete original source mesh bone names");
            var mesh=EsoHumanoidVisualLayer.LoadMesh(SourceMeshGeo,
                "VoriplasticCorpse",rig);
            if(mesh.SurfaceGetArrayLen(0)!=Vertices||mesh.GetSurfaceCount()!=1)
                throw new InvalidDataException("Original source corpse geometry unavailable");
            LoadNativeFrames(rig);
            _skeleton=skeleton;
            _skeleton.Name="ESOOriginal202BoneRig";
            AddChild(_skeleton);
            _body=new MeshInstance3D
            {
                Name="ESOOriginalVoriplasticCorpse_0015D6CD",
                Mesh=mesh,
                Skin=skin,
                Skeleton=new NodePath(".."),
                // Diagnostic colour only: GR2 embeds no texture here.
                MaterialOverride=new StandardMaterial3D
                {
                    AlbedoColor=new Color("#c0bdae"),
                    Roughness=0.83f,
                    CullMode=BaseMaterial3D.CullModeEnum.Disabled
                }
            };
            _skeleton.AddChild(_body);
            if(SelfTest)
            {
                for(int frame=0;frame<_frames.Length;frame++)
                {
                    Apply(frame);
                    CompareNativeWorldPose();
                }
                GD.Print($"PASS original ESO fully skinned body: " +
                    $"source {MeshAsset:X8} VoriplasticCorpse, {Vertices} actual GR2 vertices, " +
                    $"{SkinBindings}/{SkinBindings} native Granny source bindings, " +
                    $"{SourceBones} original bones and {_frames.Length} native GEN_Walk poses; " +
                    $"maximum source world error {_maximumWorldDifference:F7}m; " +
                    "no estimated rig mapping, positions, skinned weights or body geometry.");
                GetTree().Quit(0);
                return;
            }
            ConfigureScene();
            Apply(0);
            SetProcess(true);
        }
        catch(Exception error)
        {
            GD.PushError("Original source skinned body failed closed: "+error);
            GetTree().Quit(1);
        }
    }

    private static string RunHelper(params string[] args)
    {
        foreach(string dep in new[]{Helper,Granny,Manifest,Oodle})
            if(!File.Exists(dep))throw new FileNotFoundException(
                $"Original game source dependency missing: {dep}");
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            WorkingDirectory=Root,
            UseShellExecute=false,
            CreateNoWindow=true,
            RedirectStandardOutput=true,
            RedirectStandardError=true
        };
        process.StartInfo.ArgumentList.Add(Helper);
        foreach(string arg in args)process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())throw new IOException("Cannot start isolated native source reader");
        var output=process.StandardOutput.ReadToEndAsync();
        var errors=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Native source model decoder timed out");
        }
        string stdout=output.GetAwaiter().GetResult();
        string stderr=errors.GetAwaiter().GetResult();
        if(process.ExitCode!=0)
            throw new InvalidDataException("Source Granny asset read refused: "+
                stdout[..Math.Min(250,stdout.Length)]+" / "+
                stderr[..Math.Min(250,stderr.Length)]);
        return stdout;
    }

    private static void EnsureSourceAssets()
    {
        Directory.CreateDirectory(Cache);
        if(!File.Exists(SourceMeshRig))
        {
            string json=RunHelper("--inspect-rig",Granny,MeshAsset.ToString("X8"),
                Manifest,Oodle);
            File.WriteAllText(SourceMeshRig,json);
        }
        if(!File.Exists(SourceMeshGeo))
            RunHelper(Granny,MeshAsset.ToString("X8"),
                SourceMeshGeo,Manifest,Oodle);
        if(!File.Exists(SourceSkeleton))
        {
            string json=RunHelper("--inspect-rig",Granny,SourceRig.ToString("X8"),
                Manifest,Oodle);
            File.WriteAllText(SourceSkeleton,json);
        }
        if(!File.Exists(NativeFrames))
            RunHelper("--sample-native-pose",Granny,SourceRig.ToString("X8"),
                WalkClip.ToString("X8"),Manifest,Oodle,NativeFrames,Fps.ToString());
    }

    private static void VerifyNativeExternalMeshBinding()
    {
        string result=RunHelper("--inspect-native-external-binding",Granny,
            MeshAsset.ToString("X8"),SourceRig.ToString("X8"),Manifest,Oodle);
        if(!result.Contains("total=81, exact=81, name-differing=0, index-unmapped=0",
                StringComparison.Ordinal) ||
           !result.Contains("COMPATIBLE_SOURCE_RIG:",StringComparison.Ordinal))
            throw new InvalidDataException("Granny did not verify all 81 mesh bindings");
    }

    private void LoadNativeFrames(EsoSourceRig rig)
    {
        using var record=JsonDocument.Parse(File.ReadAllText(NativeFrames));
        var source=record.RootElement;
        if(source.GetProperty("SkeletonId").GetString()!=SourceRig.ToString("X8")||
           source.GetProperty("AnimationId").GetString()!=WalkClip.ToString("X8")||
           source.GetProperty("SkeletonName").GetString()!="GrannyRootBone"||
           source.GetProperty("AnimationName").GetString()!="GEN_Walk"||
           source.GetProperty("FrameRate").GetInt32()!=Fps)
            throw new InvalidDataException("Unexpected original ESO pose source");
        var names=source.GetProperty("Bones").EnumerateArray().ToArray();
        if(names.Length!=SourceBones)
            throw new InvalidDataException("Native source animation skeleton incomplete");
        for(int i=0;i<SourceBones;i++)
        {
            if(names[i].GetProperty("Name").GetString()!=rig.Bones[i].Name||
               names[i].GetProperty("Parent").GetInt32()!=rig.Bones[i].Parent)
                throw new InvalidDataException(
                    "Original source pose bone hierarchy differs from original model");
        }
        _duration=source.GetProperty("DurationSeconds").GetSingle();
        if(!float.IsFinite(_duration)||_duration is <0.8f or >1.5f)
            throw new InvalidDataException("Expected original GEN_Walk duration");
        var frames=new List<SourceFrame>();
        foreach(var frame in source.GetProperty("Frames").EnumerateArray())
        {
            var local=frame.GetProperty("Bones").EnumerateArray().ToArray();
            var world=frame.GetProperty("WorldPositions").EnumerateArray().ToArray();
            float time=frame.GetProperty("Time").GetSingle();
            if(local.Length!=SourceBones||world.Length!=SourceBones||
               !float.IsFinite(time)||time<0||
               (frames.Count>0&&time<=frames[^1].Time))
                throw new InvalidDataException("Native original pose sample malformed");
            var samples=new NativeLocal[SourceBones];
            var worldPoints=new Vector3[SourceBones];
            for(int i=0;i<SourceBones;i++)
            {
                var p=local[i].GetProperty("Position");
                var q=local[i].GetProperty("Orientation");
                var scale=local[i].GetProperty("ScaleShear");
                if(p.GetArrayLength()!=3||q.GetArrayLength()!=4||
                   scale.GetArrayLength()!=9||world[i].GetArrayLength()!=3)
                    throw new InvalidDataException("Native pose transform dimension mismatch");
                Vector3 position=new(p[0].GetSingle(),p[1].GetSingle(),p[2].GetSingle());
                Quaternion rotation=new(q[0].GetSingle(),q[1].GetSingle(),
                    q[2].GetSingle(),q[3].GetSingle());
                Vector3 worldPosition=new(world[i][0].GetSingle(),
                    world[i][1].GetSingle(),world[i][2].GetSingle());
                if(!position.IsFinite()||!worldPosition.IsFinite()||
                   !rotation.IsNormalized())
                    throw new InvalidDataException("Non-finite original native bone pose");
                for(int k=0;k<9;k++)
                    if(!float.IsFinite(scale[k].GetSingle())||
                       Math.Abs(scale[k].GetSingle()-(k%4==0?1f:0f))>0.0005f)
                        throw new InvalidDataException(
                            "Original source transform requires unsupported scale/shear");
                samples[i]=new NativeLocal(position,rotation);
                worldPoints[i]=worldPosition;
            }
            frames.Add(new SourceFrame(time,samples,worldPoints));
        }
        if(frames.Count is <55 or >75 ||
           Math.Abs(frames[^1].Time-_duration)>0.0002f)
            throw new InvalidDataException("Incomplete ESO original walk animation");
        _frames=frames.ToArray();
    }

    private void Apply(int frame)
    {
        if(_skeleton==null)
            throw new InvalidOperationException("Original skinning skeleton missing");
        _currentFrame=Math.Clamp(frame,0,_frames.Length-1);
        var poses=_frames[_currentFrame].Local;
        for(int i=0;i<SourceBones;i++)
        {
            _skeleton.SetBonePosePosition(i,poses[i].Position);
            _skeleton.SetBonePoseRotation(i,poses[i].Rotation);
        }
        if(_label!=null)
            _label.Text=$"ORIGINAL ESO FULLY SKINNED BODY LAB\n"+
                $"ESO asset 0x{MeshAsset:X8} VoriplasticCorpse (NOT an ordinary NPC)\n"+
                $"{Vertices} source vertices, {SkinBindings} exact Granny binding names, "+
                $"{SourceBones} native source bones\n"+
                $"GEN_Walk frame {_currentFrame+1}/{_frames.Length}; " +
                "original native skin weights, no guessed maps or gait\n"+
                "Neutral diagnostic material: actual ESO character materials are not yet decoded\n"+
                "Space pause | Left/Right frame | Drag orbit | Wheel zoom | Esc exit";
    }

    private void CompareNativeWorldPose()
    {
        if(_skeleton==null)
            throw new InvalidOperationException("Original source skeleton unavailable");
        var source=_frames[_currentFrame].World;
        for(int bone=0;bone<SourceBones;bone++)
        {
            float delta=_skeleton.GetBoneGlobalPose(bone).Origin.DistanceTo(source[bone]);
            _maximumWorldDifference=Math.Max(_maximumWorldDifference,delta);
            if(!float.IsFinite(delta)||delta>0.003f)
                throw new InvalidDataException(
                    $"Native world pose differs from Godot bone {bone} by {delta:F6}m");
        }
    }

    private void ConfigureScene()
    {
        AddChild(new WorldEnvironment{Environment=new Godot.Environment
        {
            BackgroundMode=Godot.Environment.BGMode.Color,
            BackgroundColor=new Color("#18242a"),
            AmbientLightSource=Godot.Environment.AmbientSource.Color,
            AmbientLightColor=new Color("#b4bec3")
        }});
        AddChild(new DirectionalLight3D
        {
            RotationDegrees=new Vector3(-38,25,0),
            LightEnergy=1.55f
        });
        _camera=new Camera3D{Name="SourceSkinnedBodyCamera",Current=true,Fov=43f};
        AddChild(_camera);
        var canvas=new CanvasLayer();
        AddChild(canvas);
        _label=new Label{Name="SourceSkinDetails",Position=new Vector2(16,14)};
        _label.AddThemeFontSizeOverride("font_size",17);
        canvas.AddChild(_label);
        Input.MouseMode=Input.MouseModeEnum.Visible;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        if(_camera==null)return;
        var target=new Vector3(0,1.22f,-0.15f);
        var vector=new Vector3(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),
            Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch));
        _camera.Position=target+vector*_distance;
        _camera.LookAt(target,Vector3.Up);
    }
    public override void _Process(double delta)
    {
        if(SelfTest)return;
        if(_playing)
        {
            _elapsed=(_elapsed+Mathf.Min((float)delta,0.10f))%_duration;
            Apply(Math.Clamp((int)Math.Round(_elapsed*Fps),0,_frames.Length-1));
        }
        string? path=System.Environment.GetEnvironmentVariable("ESO_ORIGINAL_BODY_PREVIEW");
        if(!_shot&&!string.IsNullOrWhiteSpace(path)&&
           DisplayServer.GetName()!="headless"&&++_screenshotTicks>=65)
        {
            _shot=true;
            GD.Print("ORIGINAL ESO SKINNED BODY PREVIEW "+
                GetViewport().GetTexture().GetImage().SavePng(path)+" "+path);
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
                    (mouse.ButtonIndex==MouseButton.WheelUp?0.85f:1.16f),1.8f,9f);
                UpdateCamera();
            }
            return;
        }
        if(evt is InputEventMouseMotion motion&&_dragging)
        {
            _yaw+=motion.Relative.X*0.005f;
            _pitch=Mathf.Clamp(_pitch-motion.Relative.Y*0.005f,-0.85f,0.85f);
            UpdateCamera();
        }
        if(evt is not InputEventKey key||!key.Pressed||key.Echo)return;
        switch(key.Keycode)
        {
            case Key.Space:_playing=!_playing;break;
            case Key.Left:
                _playing=false;
                Apply(_currentFrame==0?_frames.Length-1:_currentFrame-1);
                _elapsed=_frames[_currentFrame].Time;
                break;
            case Key.Right:
                _playing=false;
                Apply((_currentFrame+1)%_frames.Length);
                _elapsed=_frames[_currentFrame].Time;
                break;
            case Key.Escape:GetTree().Quit();break;
        }
    }
}
