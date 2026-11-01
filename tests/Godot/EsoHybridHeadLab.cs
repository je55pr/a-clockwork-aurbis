using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Process = System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// Read-only authentic ESO hybrid head diagnostic: model 000E2D77 contains
/// both CustomizationSkeleton and Bip01/mshBn bones in its original source
/// skeleton. It is NOT a guessed map from defBn_* to mshBn_*.
///
/// Only native Granny transforms whose NAME, PARENT and original INVERSE-BIND
/// are independently validated against source animation rig 000020A2 are
/// applied. The remaining customization-bound bones stay in source rest.
/// We do not claim to reproduce missing material/texture/customization data.
/// </summary>
public partial class EsoHybridHeadLab : Node3D
{
    private const uint HeadSource=0x000E2D77;
    private const uint RigSource=0x000020A2;
    private const uint WalkSource=0x0004C319;
    private const int SampleRate=60;
    // Maximum elementwise error observed between the two original ESO
    // source rest matrices is <0.000402. Reject variants outside this bound.
    private const float NativeBindTolerance=0.0005f;

    [Export] public bool SelfTest {get;set;}
    private readonly record struct NativeTransform(Vector3 Position,Quaternion Rotation);
    private sealed record PoseFrame(float Time,NativeTransform[] SourceBones);
    private readonly List<(int Target,int Source)> _animated=new();
    private PoseFrame[] _frames=Array.Empty<PoseFrame>();
    private Skeleton3D? _skeleton;
    private MeshInstance3D? _head;
    private Camera3D? _camera;
    private Label? _hud;
    private float _elapsed,_duration,_yaw=Mathf.Pi,_pitch,_distance=1.85f;
    private bool _playing=true,_dragging,_screenshotTaken;
    private int _screenshotFrames,_currentFrame;
    public int OriginalBonesAnimated=>_animated.Count;
    public int OriginalSourceSkinBindings=>_head?.Skin?.GetBindCount()??0;
    public int CurrentSourceFrame=>_currentFrame;
    public int GetAnimatedSourceBoneCount()=>OriginalBonesAnimated;
    public int GetOriginalBindingCount()=>OriginalSourceSkinBindings;
    public int GetOriginalFrameIndex()=>CurrentSourceFrame;

    private static string Root=>ProjectSettings.GlobalizePath("res://");
    private static string CacheRoot=>Path.Combine(Root,".research","npc-rigs");
    private static string Helper=>Path.Combine(Root,"tools","ModelGeometry",
        "bin","Debug","net8.0","ModelGeometry.dll");
    private static string Granny=>System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
        @"C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll";
    private static string Manifest=>System.Environment.GetEnvironmentVariable("ESO_MNF")??
        @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
    private static string Oodle=>Path.Combine(Root,".research","ooz.dll");
    private static string HybridRigFile=>Path.Combine(CacheRoot,"hybrid-head-000E2D77-rig.json");
    private static string HybridMeshFile=>Path.Combine(CacheRoot,"hybrid-head-000E2D77-mesh.esm");
    private static string AnimationRigFile=>Path.Combine(CacheRoot,"animation-skeleton-000020A2.json");
    private static string NativePoseFile=>Path.Combine(CacheRoot,
        $"native-source-{RigSource:X8}-{WalkSource:X8}-world-v2-{SampleRate}fps.json");

    public override void _Ready()
    {
        try
        {
            EnsureSources();
            EsoSourceRig rig=EsoSourceRig.Read(HybridRigFile);
            var (skeleton,skin)=rig.BuildSkeleton();
            if(rig.VertexCount!=404 || rig.Bones.Count!=54 ||
               rig.BindingNames.Length!=9 || skeleton.GetBoneCount()!=54)
                throw new InvalidDataException("Original hybrid head geometry/skin changed");
            var mesh=EsoHumanoidVisualLayer.LoadMesh(HybridMeshFile,"Head_004",rig);
            var sourceMesh=((ArrayMesh)mesh).SurfaceGetArrays(0);
            var vertices=(Vector3[])sourceMesh[(int)Mesh.ArrayType.Vertex];
            var influences=(int[])sourceMesh[(int)Mesh.ArrayType.Bones];
            if(vertices.Length!=404 || influences.Length!=404*4 ||
               skin.GetBindCount()!=rig.BindingNames.Length)
                throw new InvalidDataException("Original head skinning buffers unavailable");
            float animatedWeight=0;
            for(int i=0;i<rig.VertexCount*4;i++)
                if(rig.BindingNames[rig.Indices[i]].StartsWith("msh",StringComparison.Ordinal))
                    animatedWeight+=rig.Weights[i];
            if(animatedWeight/rig.VertexCount<0.90f)
                throw new InvalidDataException("Hybrid head lacks genuine animation skinning");

            LoadNativeFrames(rig);
            _skeleton=skeleton;
            _skeleton.Name="OriginalHybridESOHeadRig";
            AddChild(_skeleton);
            _head=new MeshInstance3D
            {
                Name="OriginalESOHead_000E2D77",
                Mesh=mesh,
                Skin=skin,
                Skeleton=new NodePath(".."),
                // Diagnostic only. Real ESO skin materials still need their
                // external asset links; do not fake eyes, skin or hair.
                MaterialOverride=new StandardMaterial3D
                {
                    AlbedoColor=new Color("#c5c6c0"),
                    Roughness=0.85f,
                    CullMode=BaseMaterial3D.CullModeEnum.Disabled
                }
            };
            _skeleton.AddChild(_head);

            if(SelfTest)
            {
                TestRealSourceAnimation(rig,animatedWeight/rig.VertexCount);
                GD.Print($"PASS original ESO HYBRID head 0x{HeadSource:X8}: " +
                    $"{rig.VertexCount} source vertices, {rig.BindingNames.Length} skin bindings, " +
                    $"{_animated.Count} exact-name/parent/rest-compatible animation bones, " +
                    $"{_frames.Length} native Granny frames, " +
                    $"{animatedWeight/rig.VertexCount:P1} source influence on animated bones; " +
                    "no fabricated geometry, gait, animation retarget or face details");
                GetTree().Quit(0);
                return;
            }
            ConfigureCamera();
            if(System.Environment.GetEnvironmentVariable("ESO_HYBRID_CAPTURE_REST")=="1")
            {
                _playing=false;
                if(_hud!=null) _hud.Text="ORIGINAL ESO HEAD 0x000E2D77: SOURCE REST POSE\nNo animation, source vertices and bind matrices unmodified";
                GD.Print("HYBRID HEAD CAPTURE: original source rest pose (no animation applied)");
            }
            else
                ApplyNativeFrame(0);
            SetProcess(true);
        }
        catch(Exception error)
        {
            GD.PushError("ESO original hybrid head test failed closed: "+error);
            GetTree().Quit(1);
        }
    }

    private static void RunNativeHelper(string[] arguments,string? standardOutput=null)
    {
        foreach(string dependency in new[]{Helper,Granny,Manifest,Oodle})
            if(!File.Exists(dependency))
                throw new FileNotFoundException(
                    $"Locally installed ESO source dependency missing: {dependency}");
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
        foreach(string arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())
            throw new IOException("Cannot launch isolated native ESO decoder");
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Native ESO decoder exceeded timeout");
        }
        string text=stdout.GetAwaiter().GetResult();
        string errors=stderr.GetAwaiter().GetResult();
        if(process.ExitCode!=0)
            throw new InvalidDataException("Native ESO decoder failed: "+
                errors[..Math.Min(errors.Length,600)]);
        if(standardOutput!=null)
            File.WriteAllText(standardOutput,text);
    }

    private static void EnsureSources()
    {
        Directory.CreateDirectory(CacheRoot);
        if(!File.Exists(HybridRigFile))
            RunNativeHelper(new[]{"--inspect-rig",Granny,HeadSource.ToString("X8"),
                Manifest,Oodle},HybridRigFile);
        if(!File.Exists(HybridMeshFile))
            RunNativeHelper(new[]{Granny,HeadSource.ToString("X8"),
                HybridMeshFile,Manifest,Oodle});
        if(!File.Exists(AnimationRigFile))
            RunNativeHelper(new[]{"--inspect-rig",Granny,RigSource.ToString("X8"),
                Manifest,Oodle},AnimationRigFile);
        if(!File.Exists(NativePoseFile))
            RunNativeHelper(new[]{"--sample-native-pose",Granny,
                RigSource.ToString("X8"),WalkSource.ToString("X8"),
                Manifest,Oodle,NativePoseFile,SampleRate.ToString(CultureInfo.InvariantCulture)});
    }

    private static JsonDocument ReadSource(string file)
    {
        var info=new FileInfo(file);
        if(info.Length is <1000 or >18_000_000)
            throw new InvalidDataException("Source record is empty or too large");
        // The existing read-only research skeleton catalogue may use a
        // UTF-16 BOM from Windows PowerShell redirection. Other source
        // caches are UTF-8. File.ReadAllText detects both without changing
        // any original source values.
        return JsonDocument.Parse(File.ReadAllText(file));
    }

    private static float[] RawMatrix(JsonElement bone)=>
        bone.GetProperty("InverseWorld").EnumerateArray().Select(v=>v.GetSingle()).ToArray();

    private static bool NearRest(float[] left,float[] right,float tolerance)
    {
        if(left.Length!=16||right.Length!=16)
            throw new InvalidDataException("Granny source bind matrix has wrong length");
        for(int i=0;i<16;i++)
            if(!float.IsFinite(left[i])||!float.IsFinite(right[i])||
               Math.Abs(left[i]-right[i])>tolerance)return false;
        return true;
    }

    private void LoadNativeFrames(EsoSourceRig rig)
    {
        using var source=ReadSource(AnimationRigFile);
        using var headSource=ReadSource(HybridRigFile);
        var sourceBones=source.RootElement.GetProperty("RigSkeletons")[0]
            .GetProperty("Bones").EnumerateArray().ToArray();
        var headBones=headSource.RootElement.GetProperty("RigSkeletons")[0]
            .GetProperty("Bones").EnumerateArray().ToArray();
        if(sourceBones.Length!=202||headBones.Length!=54)
            throw new InvalidDataException("Unexpected native source skeleton variants");
        var byName=new Dictionary<string,int>(StringComparer.Ordinal);
        for(int i=0;i<sourceBones.Length;i++)
        {
            string n=sourceBones[i].GetProperty("Name").GetString()??"";
            if(!byName.TryAdd(n,i))
                throw new InvalidDataException("Duplicate original animation bone");
        }
        int animatedBindings=0;
        for(int i=0;i<rig.Bones.Count;i++)
        {
            var bone=rig.Bones[i];
            if(!bone.Name.StartsWith("msh",StringComparison.Ordinal))continue;
            if(!byName.TryGetValue(bone.Name,out int sourceIndex))
                throw new InvalidDataException($"Native joint {bone.Name} absent from animation skeleton");
            string HeadParent(int index)=>index<0?"<root>":
                rig.Bones[index].Name;
            int sourceParent=sourceBones[sourceIndex].GetProperty("Parent").GetInt32();
            string sourceParentName=sourceParent<0?"<root>":
                sourceBones[sourceParent].GetProperty("Name").GetString()??"";
            if(HeadParent(bone.Parent)!=sourceParentName)
                throw new InvalidDataException($"Original hierarchy mismatch: {bone.Name}");
            if(!NearRest(RawMatrix(headBones[i]),RawMatrix(sourceBones[sourceIndex]),NativeBindTolerance))
                throw new InvalidDataException($"Original bind pose incompatible: {bone.Name}");
            _animated.Add((i,sourceIndex));
        }
        if(_animated.Count!=43)
            throw new InvalidDataException("Original hybrid animation subtree incomplete");
        foreach(string binding in rig.BindingNames)
            if(binding.StartsWith("msh",StringComparison.Ordinal))
            {
                if(!_animated.Any(pair=>rig.Bones[pair.Target].Name==binding))
                    throw new InvalidDataException("Animated head binding has no native joint");
                animatedBindings++;
            }
        if(animatedBindings!=5)
            throw new InvalidDataException("Original hybrid head expected five animated bindings");

        using var poses=ReadSource(NativePoseFile);
        var record=poses.RootElement;
        if(record.GetProperty("SkeletonId").GetString()!=RigSource.ToString("X8")||
           record.GetProperty("AnimationId").GetString()!=WalkSource.ToString("X8")||
           record.GetProperty("FrameRate").GetInt32()!=SampleRate||
           record.GetProperty("AnimationName").GetString()!="GEN_Walk")
            throw new InvalidDataException("Wrong native ESO animation clip");
        var frameBones=record.GetProperty("Bones").EnumerateArray().ToArray();
        if(frameBones.Length!=202)
            throw new InvalidDataException("Native pose bone table incorrect");
        for(int i=0;i<frameBones.Length;i++)
            if(frameBones[i].GetProperty("Name").GetString()!=
               sourceBones[i].GetProperty("Name").GetString())
                throw new InvalidDataException("Native pose indices differ from source rig");
        _duration=record.GetProperty("DurationSeconds").GetSingle();
        if(!float.IsFinite(_duration)||_duration is <0.5f or >2f)
            throw new InvalidDataException("Original walk duration invalid");
        var frames=new List<PoseFrame>();
        foreach(var frame in record.GetProperty("Frames").EnumerateArray())
        {
            var sourceTransforms=frame.GetProperty("Bones");
            float t=frame.GetProperty("Time").GetSingle();
            if(!float.IsFinite(t)||t<0||t>_duration+0.0001f||
               (frames.Count>0&&t<=frames[^1].Time))
                throw new InvalidDataException("Original animation timestamps invalid");
            if(sourceTransforms.GetArrayLength()!=202)
                throw new InvalidDataException("Native pose has missing bones");
            var transforms=new NativeTransform[_animated.Count];
            for(int i=0;i<_animated.Count;i++)
            {
                var transform=sourceTransforms[_animated[i].Source];
                var p=transform.GetProperty("Position");
                var r=transform.GetProperty("Orientation");
                var s=transform.GetProperty("ScaleShear");
                if(p.GetArrayLength()!=3||r.GetArrayLength()!=4||s.GetArrayLength()!=9)
                    throw new InvalidDataException("Original transform data missing");
                var pos=new Vector3(p[0].GetSingle(),p[1].GetSingle(),p[2].GetSingle());
                var rot=new Quaternion(r[0].GetSingle(),r[1].GetSingle(),
                    r[2].GetSingle(),r[3].GetSingle());
                if(!pos.IsFinite()||pos.Length()>15||
                   !rot.IsNormalized())
                    throw new InvalidDataException("Native animation transform invalid");
                // No deformation approximation: affine scale/shear cannot
                // be represented by quaternion + translation. Reject if
                // native transform ever requires it.
                for(int k=0;k<9;k++)
                    if(!float.IsFinite(s[k].GetSingle())||
                       Math.Abs(s[k].GetSingle()-(k%4==0?1f:0f))>0.0005f)
                        throw new InvalidDataException("Original joint has unsupported native shear");
                transforms[i]=new NativeTransform(pos,rot);
            }
            frames.Add(new PoseFrame(t,transforms));
        }
        if(frames.Count<55||frames.Count>75)
            throw new InvalidDataException("Original native animation has missing sample frames");
        _frames=frames.ToArray();
    }

    private void ApplyNativeFrame(int index)
    {
        if(_skeleton==null)
            throw new InvalidOperationException("Native hybrid skeleton missing");
        _currentFrame=Math.Clamp(index,0,_frames.Length-1);
        var transforms=_frames[_currentFrame].SourceBones;
        for(int i=0;i<_animated.Count;i++)
        {
            int target=_animated[i].Target;
            _skeleton.SetBonePosePosition(target,transforms[i].Position);
            _skeleton.SetBonePoseRotation(target,transforms[i].Rotation);
        }
        if(_hud!=null)
            _hud.Text=$"ESO HYBRID HEAD 0x{HeadSource:X8} (original geometry and weights)\n"+
                $"GEN_Walk | original 202-bone source rig | frame {_currentFrame+1}/{_frames.Length}\n"+
                $"43 exact-name, original-hierarchy, bind-compatible animated joints; "+
                $"5/9 skinned bindings animated\n"+
                "No source skin texture yet; neutral diagnostic material only. "+
                "Customization-only bones remain at original rest.\n"+
                "Space pause | Left/Right frame | Drag orbit | Wheel zoom | Esc exit";
    }

    private void TestRealSourceAnimation(EsoSourceRig rig,float movingSkinFraction)
    {
        if(_skeleton==null||_head==null ||
           movingSkinFraction<0.90f||
           _head.Mesh is not ArrayMesh||
           _head.Skin?.GetBindCount()!=9)
            throw new InvalidDataException("Original source skin/mesh unavailable");
        int moving=_skeleton.FindBone("mshCtrl_body");
        int independent=_skeleton.FindBone("defCtrl_body");
        if(moving<0||independent<0||rig.Bones.Count!=54)
            throw new InvalidDataException("Both native skeleton roots not present");
        var beforeCustomization=_skeleton.GetBonePosePosition(independent);
        ApplyNativeFrame(0);
        var first=_skeleton.GetBonePosePosition(moving);
        ApplyNativeFrame(_frames.Length/3);
        var second=_skeleton.GetBonePosePosition(moving);
        if((first-second).Length()<0.03f)
            throw new InvalidDataException("Original native walk did not animate head-bearing skeleton");
        if(!_skeleton.GetBonePosePosition(independent).IsEqualApprox(beforeCustomization))
            throw new InvalidDataException("Native walk illegally changed customization skeleton");
    }

    private void ConfigureCamera()
    {
        AddChild(new WorldEnvironment{Environment=new Godot.Environment
        {
            BackgroundMode=Godot.Environment.BGMode.Color,
            BackgroundColor=new Color("#202a36"),
            AmbientLightSource=Godot.Environment.AmbientSource.Color,
            AmbientLightColor=new Color("#aebac7")
        }});
        AddChild(new DirectionalLight3D{RotationDegrees=new Vector3(-40,25,0),
            LightEnergy=1.35f});
        _camera=new Camera3D{Name="NativeHeadCamera",Current=true,Fov=37f};
        AddChild(_camera);
        _hud=new Label{Position=new Vector2(14,13)};
        _hud.AddThemeFontSizeOverride("font_size",16);
        var canvas=new CanvasLayer();
        AddChild(canvas);
        canvas.AddChild(_hud);
        Input.MouseMode=Input.MouseModeEnum.Visible;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        if(_camera==null)return;
        var target=new Vector3(0,2.07f,0);
        var orientation=new Vector3(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),
            Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch));
        _camera.Position=target+orientation*_distance;
        _camera.LookAt(target,Vector3.Up);
    }

    public override void _Process(double delta)
    {
        if(SelfTest||_frames.Length==0)return;
        if(_playing)
        {
            _elapsed=(_elapsed+(float)Math.Min(delta,0.1))%_duration;
            int frame=(int)Math.Round(_elapsed*SampleRate);
            ApplyNativeFrame(Math.Min(frame,_frames.Length-1));
        }
        string? screenshot=System.Environment.GetEnvironmentVariable("ESO_HYBRID_HEAD_PREVIEW_PATH");
        if(!_screenshotTaken&&!string.IsNullOrWhiteSpace(screenshot)&&
           DisplayServer.GetName()!="headless"&&++_screenshotFrames>=70)
        {
            _screenshotTaken=true;
            GD.Print($"HYBRID HEAD PREVIEW "+
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
                    (mouse.ButtonIndex==MouseButton.WheelUp?0.84f:1.19f),0.4f,3.0f);
                UpdateCamera();
            }
            return;
        }
        if(evt is InputEventMouseMotion motion&&_dragging)
        {
            _yaw+=motion.Relative.X*0.005f;
            _pitch=Mathf.Clamp(_pitch-motion.Relative.Y*0.005f,-0.95f,0.95f);
            UpdateCamera();
            return;
        }
        if(evt is not InputEventKey key||!key.Pressed||key.Echo)return;
        switch(key.Keycode)
        {
            case Key.Space:_playing=!_playing;break;
            case Key.Left:
                _playing=false;
                ApplyNativeFrame(_currentFrame==0?_frames.Length-1:_currentFrame-1);
                _elapsed=_frames[_currentFrame].Time;
                break;
            case Key.Right:
                _playing=false;
                ApplyNativeFrame((_currentFrame+1)%_frames.Length);
                _elapsed=_frames[_currentFrame].Time;
                break;
            case Key.Escape:GetTree().Quit();break;
        }
    }
}
