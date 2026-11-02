using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace EsoTest;

/// <summary>
/// Isolated ORIGINAL ESO hybrid-arm animation proof. Only 13 native
/// mshCtrl/mshBn bones with matching names, parents and inverse rests are
/// animated. Four arm_* customization controls are intentionally NOT mapped
/// or modified. Shows original GR2 skinning under original GEN_Walk samples.
/// </summary>
public partial class EsoOriginalHybridArmsLab : Node3D
{
    private const float RestTolerance=0.0001f;
    private const float WorldTolerance=0.003f;
    private const int SourceFrameRate=60;
    private readonly record struct SourceTransform(Vector3 Position,Quaternion Orientation);
    private sealed record SourceFrame(float Time,SourceTransform[] Poses,Vector3[] World);
    private sealed class Arm
    {
        internal required EsoSourceRig Rig;
        internal required Skeleton3D Skeleton;
        internal required int[] BoneTargets;
        internal required int[] BoneSources;
        internal required string Name;
        internal int AnimatedSkin;
        internal int CustomizationSkin;
    }
    private readonly List<Arm> _arms=new();
    private SourceFrame[] _frames=Array.Empty<SourceFrame>();
    private Camera3D? _camera;
    private float _yaw=0.15f,_pitch=0.09f,_distance=3.2f,_elapsed;
    private float _duration;
    private bool _playing=true,_drag;
    private int _frameIndex;
    private float _maxError;
    [Export] public bool SelfTest{get;set;}
    public int AnimatedOriginalJointCount=>_arms.Count==0?0:_arms[0].BoneTargets.Length;
    public int OriginalArmMeshes=>_arms.Count;
    public int AnimatedSkinBindings=>_arms.Sum(x=>x.AnimatedSkin);
    public int UnmappedCustomizationSkinBindings=>_arms.Sum(x=>x.CustomizationSkin);
    public float MaximumNativeWorldError=>_maxError;
    public int CurrentFrame=>_frameIndex;
    private static string Root=>ProjectSettings.GlobalizePath("res://");
    private static string Cache=>Path.Combine(Root,".research","npc-rigs");
    private static string Helper=>Path.Combine(Root,"tools","ModelGeometry",
        "bin","Debug","net8.0","ModelGeometry.dll");
    private static string Granny=>System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
        @"C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll";
    private static string Manifest=>System.Environment.GetEnvironmentVariable("ESO_MNF")??
        @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
    private static string Oodle=>Path.Combine(Root,".research","ooz.dll");
    private static string SourcePose=>Path.Combine(Cache,
        "native-source-000020A2-0004C319-world-v2-60fps.json");
    private static readonly (string Id,string Mesh,int Vertices)[] Parts=
    {
        ("000E35AF","ArmsLower_062",528),
        ("000E3851","ArmsLower_061",740)
    };

    private static string ExtractOriginal(params string[] args)
    {
        foreach(string source in new[]{Helper,Granny,Manifest,Oodle})
            if(!File.Exists(source))
                throw new FileNotFoundException("Installed ESO source dependency missing: "+source);
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            WorkingDirectory=Root,CreateNoWindow=true,UseShellExecute=false,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        process.StartInfo.ArgumentList.Add(Helper);
        foreach(string arg in args)process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())
            throw new IOException("Original ESO native GR2 reader could not start");
        var output=process.StandardOutput.ReadToEndAsync();
        var errors=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Original ESO GR2 helper exceeded bounded timeout");
        }
        string result=output.GetAwaiter().GetResult();
        string error=errors.GetAwaiter().GetResult();
        if(process.ExitCode!=0)
            throw new InvalidDataException(
                "Original ESO GR2 helper rejected source: "+
                error[..Math.Min(600,error.Length)]);
        return result;
    }

    private static void EnsureOriginalSources()
    {
        Directory.CreateDirectory(Cache);
        string rigPath=Path.Combine(Cache,"animation-skeleton-000020A2.json");
        if(!File.Exists(rigPath))
            File.WriteAllText(rigPath,ExtractOriginal(
                "--inspect-rig",Granny,"000020A2",Manifest,Oodle));
        if(!File.Exists(SourcePose))
            ExtractOriginal("--sample-native-pose",Granny,"000020A2",
                "0004C319",Manifest,Oodle,SourcePose,"60");
        foreach(var part in Parts)
        {
            string rig=Path.Combine(Cache,"hybrid-"+part.Id+"-rig.json");
            string mesh=Path.Combine(Cache,"hybrid-"+part.Id+"-mesh.esm");
            if(!File.Exists(rig))
                File.WriteAllText(rig,ExtractOriginal(
                    "--inspect-rig",Granny,part.Id,Manifest,Oodle));
            if(!File.Exists(mesh))
                ExtractOriginal(Granny,part.Id,mesh,Manifest,Oodle);
        }
    }

    public override void _Ready()
    {
        try
        {
            EnsureOriginalSources();
            string animationRig=Path.Combine(Cache,"animation-skeleton-000020A2.json");
            using var rigDocument=JsonDocument.Parse(File.ReadAllText(animationRig));
            using var posesDocument=JsonDocument.Parse(File.ReadAllText(SourcePose));
            JsonElement animationBones=rigDocument.RootElement
                .GetProperty("RigSkeletons")[0].GetProperty("Bones");
            var byName=new Dictionary<string,int>(StringComparer.Ordinal);
            for(int i=0;i<animationBones.GetArrayLength();i++)
            {
                string name=animationBones[i].GetProperty("Name").GetString()??"";
                if(!byName.TryAdd(name,i))
                    throw new InvalidDataException("Duplicate original animation bone");
            }
            if(animationBones.GetArrayLength()!=202||
               byName.GetValueOrDefault("Bip01")!=1)
                throw new InvalidDataException("Wrong source 202-joint walk skeleton");

            foreach(var (id,meshName,vertices) in Parts)
            {
                string rigPath=Path.Combine(Cache,"hybrid-"+id+"-rig.json");
                string meshPath=Path.Combine(Cache,"hybrid-"+id+"-mesh.esm");
                if(!File.Exists(rigPath)||!File.Exists(meshPath))
                    throw new FileNotFoundException(
                        "Source exact hybrid arm GR2 missing: "+id+
                        "; run installed ModelGeometry --inspect-rig and "+
                        "original --extract before launching");
                var source=EsoSourceRig.Read(rigPath);
                if(source.MeshName!=meshName || source.VertexCount!=vertices||
                   source.Bones.Count!=30 || source.BindingNames.Length!=12)
                    throw new InvalidDataException(
                        "Original hybrid arm geometry/source metadata changed: "+id);
                var (skeleton,skin)=source.BuildSkeleton();
                var targets=new List<int>();
                var matches=new List<int>();
                foreach(var bone in source.Bones.Select((value,index)=>(value,index)))
                {
                    bool isMsh=bone.value.Name.StartsWith("mshBn_",StringComparison.Ordinal)||
                               bone.value.Name=="mshCtrl_body";
                    if(!isMsh)continue;
                    if(!byName.TryGetValue(bone.value.Name,out int sourceIndex))
                        throw new InvalidDataException("Source walking bone absent: "+bone.value.Name);
                    JsonElement animated=animationBones[sourceIndex];
                    string sourceParent=bone.value.Parent<0?"(root)":
                        source.Bones[bone.value.Parent].Name;
                    int animatedParent=animated.GetProperty("Parent").GetInt32();
                    string targetParent=animatedParent<0?"(root)":
                        animationBones[animatedParent].GetProperty("Name").GetString()??"";
                    if(sourceParent!=targetParent)
                        throw new InvalidDataException(
                            "Source hybrid arm hierarchy incompatible: "+bone.value.Name);
                    JsonElement matrix=animated.GetProperty("InverseWorld");
                    var parts=new float[16];
                    for(int k=0;k<16;k++)parts[k]=matrix[k].GetSingle();
                    Transform3D old=bone.value.InverseWorld;
                    Transform3D native=new(
                        new Basis(new Vector3(parts[0],parts[1],parts[2]),
                            new Vector3(parts[4],parts[5],parts[6]),
                            new Vector3(parts[8],parts[9],parts[10])),
                        new Vector3(parts[12],parts[13],parts[14]));
                    float diff=MaxTransformDelta(old,native);
                    if(diff>RestTolerance)
                        throw new InvalidDataException(
                            $"Original animation rig rest for {bone.value.Name} differs {diff:F7}");
                    targets.Add(bone.index);
                    matches.Add(sourceIndex);
                }
                if(targets.Count!=13)
                    throw new InvalidDataException("Hybrid arm requires 13 original matching motion bones");
                int animatedSkin=source.BindingNames.Count(n=>n.StartsWith("mshBn_",StringComparison.Ordinal));
                int custom=source.BindingNames.Count(n=>n.StartsWith("arm_",StringComparison.Ordinal));
                if(animatedSkin!=8 || custom!=4)
                    throw new InvalidDataException(
                        "Source hybrid arm has unexpected animation/customization bind counts");
                var mesh=EsoHumanoidVisualLayer.LoadMesh(meshPath,meshName,source);
                if(mesh.SurfaceGetArrayLen(0)!=vertices||skin.GetBindCount()!=12)
                    throw new InvalidDataException("Original arm mesh or native skin lost");
                skeleton.Name="Original_"+meshName+"_HybridRig";
                skeleton.Position=new Vector3(_arms.Count==0?-0.65f:0.65f,0,0);
                AddChild(skeleton);
                var instance=new MeshInstance3D
                {
                    Name="Original_"+meshName+"_Skin",
                    Mesh=mesh,Skin=skin,Skeleton=new NodePath(".."),
                    MaterialOverride=new StandardMaterial3D
                    {
                        AlbedoColor=new Color("#9facb4"),
                        Roughness=0.8f,
                        CullMode=BaseMaterial3D.CullModeEnum.Disabled
                    }
                };
                skeleton.AddChild(instance);
                _arms.Add(new Arm
                {
                    Rig=source,Skeleton=skeleton,Name=meshName,
                    BoneTargets=targets.ToArray(),BoneSources=matches.ToArray(),
                    AnimatedSkin=animatedSkin,CustomizationSkin=custom
                });
            }
            LoadNativeFrames(posesDocument.RootElement,animationBones);
            int[] checks={0,17,34,52,69};
            foreach(int frame in checks)
            {
                ApplyNative(frame);
                CheckNativeWorld();
            }
            var left=_arms[0];
            int moving=Array.FindIndex(left.BoneTargets,i=>
                left.Rig.Bones[i].Name=="mshBn_upperArm_L");
            if(moving<0)
                throw new InvalidDataException("Source hybrid left upper arm absent");
            Vector3 start=_frames[0].World[left.BoneSources[moving]];
            Vector3 later=_frames[34].World[left.BoneSources[moving]];
            if(start.DistanceTo(later)<0.01f)
                throw new InvalidDataException("Original walking arm did not move");
            GD.Print($"PASS 2 original hybrid ESO lower-arm meshes / 13 exact source "+
                $"animation bones per source / 8 moving + 4 unconverted original "+
                $"customization skin binds per model / {_frames.Length} GEN_Walk "+
                $"native frames / world deviation {_maxError:F7}m");
            ApplyNative(0);
            if(SelfTest){GetTree().Quit(0);return;}
            ConfigureDisplay();
            SetProcess(true);
        }
        catch(Exception error)
        {
            GD.PushError("Original hybrid arms failed closed: "+error);
            GetTree().Quit(1);
        }
    }

    private static float MaxTransformDelta(Transform3D a,Transform3D b)
    {
        float max=Math.Max(Math.Abs(a.Origin.X-b.Origin.X),
            Math.Max(Math.Abs(a.Origin.Y-b.Origin.Y),
                Math.Abs(a.Origin.Z-b.Origin.Z)));
        for(int j=0;j<3;j++)
        {
            Vector3 x=a.Basis[j],y=b.Basis[j];
            max=Math.Max(max,Math.Max(Math.Abs(x.X-y.X),
                Math.Max(Math.Abs(x.Y-y.Y),Math.Abs(x.Z-y.Z))));
        }
        return max;
    }
    private static Vector3 Vector3Array(JsonElement values)=>
        new(values[0].GetSingle(),values[1].GetSingle(),values[2].GetSingle());

    private void LoadNativeFrames(JsonElement root,JsonElement sourceBones)
    {
        if(root.GetProperty("SkeletonId").GetString()!="000020A2"||
           root.GetProperty("AnimationId").GetString()!="0004C319"||
           root.GetProperty("AnimationName").GetString()!="GEN_Walk"||
           root.GetProperty("FrameRate").GetInt32()!=SourceFrameRate)
            throw new InvalidDataException("Wrong source ESO native walking clip");
        _duration=root.GetProperty("DurationSeconds").GetSingle();
        var recordBones=root.GetProperty("Bones");
        if(recordBones.GetArrayLength()!=202)
            throw new InvalidDataException("Source walk lacks 202 original bones");
        for(int i=0;i<202;i++)
            if(sourceBones[i].GetProperty("Name").GetString()!=
               recordBones[i].GetProperty("Name").GetString())
                throw new InvalidDataException("Native walk samples use wrong skeleton");
        var frames=new List<SourceFrame>();
        foreach(var f in root.GetProperty("Frames").EnumerateArray())
        {
            float seconds=f.GetProperty("Time").GetSingle();
            if(!float.IsFinite(seconds)||seconds<0||
               seconds>_duration+0.001f||
               (frames.Count>0&&seconds<=frames[^1].Time))
                throw new InvalidDataException("Invalid native walk frame time");
            var samples=f.GetProperty("Bones");
            var world=f.GetProperty("WorldPositions");
            if(samples.GetArrayLength()!=202||world.GetArrayLength()!=202)
                throw new InvalidDataException("Native Granny frame incomplete");
            var transforms=new SourceTransform[202];
            var positions=new Vector3[202];
            for(int i=0;i<202;i++)
            {
                var t=samples[i];
                Vector3 pos=Vector3Array(t.GetProperty("Position"));
                var q=t.GetProperty("Orientation");
                Quaternion rotation=new(q[0].GetSingle(),q[1].GetSingle(),
                    q[2].GetSingle(),q[3].GetSingle());
                if(!pos.IsFinite()||!rotation.IsFinite()||
                   Math.Abs(rotation.Length()-1)>0.002f)
                    throw new InvalidDataException("Invalid native animation transform");
                transforms[i]=new SourceTransform(pos,rotation);
                positions[i]=Vector3Array(world[i]);
                if(!positions[i].IsFinite())
                    throw new InvalidDataException("Nonfinite native world position");
            }
            frames.Add(new SourceFrame(seconds,transforms,positions));
        }
        if(frames.Count!=70||_duration is <0.8f or >1.3f)
            throw new InvalidDataException("Original GEN_Walk frame count/duration changed");
        _frames=frames.ToArray();
    }
    private void ApplyNative(int index)
    {
        _frameIndex=Math.Clamp(index,0,_frames.Length-1);
        var transforms=_frames[_frameIndex].Poses;
        foreach(Arm arm in _arms)
        {
            for(int i=0;i<arm.BoneTargets.Length;i++)
            {
                int target=arm.BoneTargets[i];
                var native=transforms[arm.BoneSources[i]];
                arm.Skeleton.SetBonePosePosition(target,native.Position);
                arm.Skeleton.SetBonePoseRotation(target,native.Orientation);
            }
        }
    }
    private void CheckNativeWorld()
    {
        foreach(Arm arm in _arms)
        {
            for(int i=0;i<arm.BoneTargets.Length;i++)
            {
                Vector3 got=arm.Skeleton.GetBoneGlobalPose(arm.BoneTargets[i]).Origin;
                Vector3 native=_frames[_frameIndex].World[arm.BoneSources[i]];
                float err=got.DistanceTo(native);
                _maxError=Math.Max(_maxError,err);
                if(!float.IsFinite(err)||err>WorldTolerance)
                    throw new InvalidDataException(
                        $"Hybrid {arm.Name} real {arm.Rig.Bones[arm.BoneTargets[i]].Name} "+
                        $"Granny native frame {_frameIndex} drifted {err:F6}m");
            }
        }
    }
    public override void _Process(double delta)
    {
        if(!_playing||_frames.Length==0)return;
        _elapsed=(_elapsed+(float)delta)%_duration;
        int idx=Math.Clamp((int)Mathf.Round(_elapsed*SourceFrameRate),
            0,_frames.Length-1);
        ApplyNative(idx);
    }
    private void ConfigureDisplay()
    {
        AddChild(new WorldEnvironment
        {
            Environment=new Godot.Environment
            {
                BackgroundMode=Godot.Environment.BGMode.Color,
                BackgroundColor=new Color("#17242e"),
                AmbientLightSource=Godot.Environment.AmbientSource.Color,
                AmbientLightColor=new Color("#cdd6de"),
                AmbientLightEnergy=0.8f
            }
        });
        AddChild(new DirectionalLight3D{RotationDegrees=new Vector3(-40,-30,0)});
        _camera=new Camera3D{Name="OriginalHybridArmsCamera",Current=true,Fov=48};
        AddChild(_camera);
        var hud=new CanvasLayer();
        AddChild(hud);
        var label=new Label
        {
            Position=new Vector2(18,18),
            Text="ORIGINAL ESO HYBRID ARM ANIMATION\n"+
                "ArmsLower_062 / ArmsLower_061\n"+
                "GEN_Walk: 13 exact native motion joints per arm\n"+
                "8 motion + 4 unmapped customization skin binds\n"+
                "Neither is a complete NPC or source retarget\n"+
                "Space pause | Left/Right step | Drag orbit | Wheel zoom"
        };
        hud.AddChild(label);
        UpdateCamera();
    }
    private void UpdateCamera()
    {
        if(_camera==null)return;
        Vector3 center=new(0,1.2f,0);
        Vector3 vec=new(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),
            Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch));
        _camera.Position=center+vec*_distance;
        _camera.LookAt(center,Vector3.Up);
    }
    public override void _Input(InputEvent e)
    {
        if(SelfTest)return;
        if(e is InputEventMouseButton button)
        {
            if(button.ButtonIndex==MouseButton.Left)_drag=button.Pressed;
            if(button.Pressed&&button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                _distance=Mathf.Clamp(_distance*
                    (button.ButtonIndex==MouseButton.WheelUp?0.84f:1.17f),1.3f,15f);
                UpdateCamera();
            }
        }
        else if(e is InputEventMouseMotion motion && _drag)
        {
            _yaw+=motion.Relative.X*0.004f;
            _pitch=Mathf.Clamp(_pitch-motion.Relative.Y*0.004f,-1.1f,1.1f);
            UpdateCamera();
        }
        else if(e is InputEventKey key&&key.Pressed&&!key.Echo)
        {
            if(key.Keycode==Key.Escape)GetTree().Quit();
            else if(key.Keycode==Key.Space)_playing=!_playing;
            else if(key.Keycode==Key.Right)
            {
                _playing=false;
                ApplyNative((_frameIndex+1)%_frames.Length);
            }
            else if(key.Keycode==Key.Left)
            {
                _playing=false;
                ApplyNative((_frameIndex+_frames.Length-1)%_frames.Length);
            }
        }
    }
}
