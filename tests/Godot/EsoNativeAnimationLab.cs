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
/// A faithful wireframe display of ESO's original GrannyRootBone skeleton
/// animated by the original PC_Walk asset through ESO's shipped granny2 DLL.
///
/// This intentionally does NOT render a character body. ESO costume meshes
/// use separate CustomizationSkeleton bone names, and the native Granny
/// binding probe confirms these cannot bind directly onto GrannyRootBone.
/// No fabricated animation retargets, cloth, faces, or material assignments.
/// Source pose cache is local, ignored and regenerable from the installed ESO.
/// </summary>
public partial class EsoNativeAnimationLab : Node3D
{
    private const uint SkeletonId=0x0000209F;
    private const uint AnimationId=0x0008942F;
    private const int SampleRate=60;
    private const string ExpectedSkeletonName="GrannyRootBone";
    private const string ExpectedAnimationName="PC_Walk";

    [Export] public bool SelfTest {get;set;}
    private sealed record NativeBone(string Name,int Parent);
    private sealed record NativeFrame(float Time,Vector3[] World);
    private NativeBone[] _bones=Array.Empty<NativeBone>();
    private NativeFrame[] _frames=Array.Empty<NativeFrame>();
    private int[] _segments=Array.Empty<int>();
    private MultiMesh? _lines;
    private Camera3D? _camera;
    private Label? _hud;
    private int _currentFrame;
    private float _elapsed,_duration,_cameraDistance=3.6f,_yaw,_pitch;
    private bool _playing=true,_orbiting,_previewWritten;
    private int _previewFrames;
    public int NativeBoneCount=>_bones.Length;
    public int NativeFrameCount=>_frames.Length;
    public string SourceDescription=>
        $"ESO 0x{SkeletonId:X8} {ExpectedSkeletonName} + 0x{AnimationId:X8} {ExpectedAnimationName}";

    public override void _Ready()
    {
        try
        {
            string file=EnsureNativePoseCache();
            LoadNativeFrames(file);
            BuildSourceSkeletonVisual();
            if(SelfTest)
            {
                VerifyNativeMotion();
                GD.Print($"PASS native ESO PC_Walk on original {NativeBoneCount}-bone " +
                    $"GrannyRootBone skeleton: {NativeFrameCount} source poses; " +
                    "no character mesh or invented retargeting");
                GetTree().Quit(0);
                return;
            }
            ConfigureScene();
            ShowFrame(0);
            SetProcess(true);
        }
        catch(Exception e)
        {
            GD.PushError("Original ESO animation lab failed closed: "+e);
            GetTree().Quit(1);
        }
    }

    private static string ProjectRoot=>ProjectSettings.GlobalizePath("res://");
    private static string CacheFile=>Path.Combine(ProjectRoot,
        ".research","npc-rigs",
        $"native-source-{SkeletonId:X8}-{AnimationId:X8}-world-v2-{SampleRate}fps.json");

    private static string EnsureNativePoseCache()
    {
        string output=CacheFile;
        if(File.Exists(output))return output;
        string helper=Path.Combine(ProjectRoot,"tools","ModelGeometry",
            "bin","Debug","net8.0","ModelGeometry.dll");
        string granny=System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
            Path.Combine("C:","ESO","The Elder Scrolls Online","game",
                "client","granny2_x64.dll");
        string manifest=System.Environment.GetEnvironmentVariable("ESO_MNF")??
            Path.Combine("C:","ESO","The Elder Scrolls Online","depot","eso.mnf");
        string oodle=Path.Combine(ProjectRoot,".research","ooz.dll");
        if(!File.Exists(helper)||!File.Exists(granny)||
            !File.Exists(manifest)||!File.Exists(oodle))
            throw new FileNotFoundException(
                "Original rig lab requires the locally installed ESO depot, shipped Granny DLL, " +
                "Oodle decoder and built ModelGeometry helper. No substitutes are used.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            UseShellExecute=false,
            WorkingDirectory=ProjectRoot,
            CreateNoWindow=true,
            RedirectStandardOutput=true,
            RedirectStandardError=true
        };
        foreach(string arg in new[]{
            helper,"--sample-native-pose",granny,SkeletonId.ToString("X8"),
            AnimationId.ToString("X8"),manifest,oodle,output,SampleRate.ToString()})
            process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())
            throw new IOException("Cannot run source Granny sampler");
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Native pose sampler exceeded 45 seconds");
        }
        string result=stdout.GetAwaiter().GetResult();
        string errors=stderr.GetAwaiter().GetResult();
        if(process.ExitCode!=0||!File.Exists(output))
            throw new InvalidDataException("Native ESO pose export failed: "+
                errors[..Math.Min(errors.Length,600)]);
        GD.Print(result.Trim());
        return output;
    }

    private static Vector3 ReadVector3(JsonElement vector)
    {
        if(vector.ValueKind!=JsonValueKind.Array||vector.GetArrayLength()!=3)
            throw new InvalidDataException("Original bone position must contain three coordinates");
        var value=new Vector3(vector[0].GetSingle(),
            vector[1].GetSingle(),vector[2].GetSingle());
        if(!value.IsFinite()||value.Length()>1000)
            throw new InvalidDataException("Nonfinite/implausible native Granny position");
        return value;
    }

    private void LoadNativeFrames(string file)
    {
        long bytes=new FileInfo(file).Length;
        if(bytes is <1000 or >24_000_000)
            throw new InvalidDataException("Native ESO pose cache size invalid");
        using var document=JsonDocument.Parse(File.ReadAllBytes(file));
        JsonElement root=document.RootElement;
        if(root.GetProperty("SkeletonId").GetString()!=SkeletonId.ToString("X8")||
           root.GetProperty("AnimationId").GetString()!=AnimationId.ToString("X8")||
           root.GetProperty("SkeletonName").GetString()!=ExpectedSkeletonName||
           root.GetProperty("AnimationName").GetString()!=ExpectedAnimationName||
           root.GetProperty("FrameRate").GetInt32()!=SampleRate)
            throw new InvalidDataException("Native animation/skeleton identity mismatch");
        _duration=root.GetProperty("DurationSeconds").GetSingle();
        if(!float.IsFinite(_duration)||_duration is <0.1f or >3f)
            throw new InvalidDataException("Source animation duration invalid");
        var skeleton=new List<NativeBone>();
        foreach(JsonElement bone in root.GetProperty("Bones").EnumerateArray())
        {
            int parent=bone.GetProperty("Parent").GetInt32();
            string name=bone.GetProperty("Name").GetString()??"";
            if(parent>=skeleton.Count||parent< -1||name.Length==0||
                skeleton.Any(b=>b.Name==name)||skeleton.Count>=400)
                throw new InvalidDataException("Original skeleton bone hierarchy invalid");
            skeleton.Add(new NativeBone(name,parent));
        }
        if(skeleton.Count!=138||!skeleton.Any(b=>b.Name=="mshBn_thigh_L")||
           !skeleton.Any(b=>b.Name=="mshBn_foot_R"))
            throw new InvalidDataException("Unexpected original source skeleton");
        _bones=skeleton.ToArray();
        var frames=new List<NativeFrame>();
        foreach(JsonElement frame in root.GetProperty("Frames").EnumerateArray())
        {
            float t=frame.GetProperty("Time").GetSingle();
            if(!float.IsFinite(t)||t<0||t>_duration+0.0001f||
               (frames.Count>0 && t<=frames[^1].Time))
                throw new InvalidDataException("Native frame times are invalid");
            var coords=frame.GetProperty("WorldPositions");
            if(coords.GetArrayLength()!=_bones.Length)
                throw new InvalidDataException("Native world pose does not match source bones");
            var positions=new Vector3[_bones.Length];
            for(int i=0;i<positions.Length;i++)
                positions[i]=ReadVector3(coords[i]);
            frames.Add(new NativeFrame(t,positions));
        }
        if(frames.Count is <45 or >75)
            throw new InvalidDataException("Expected original 60-Hz native motion samples");
        _frames=frames.ToArray();
        // Display only the actual 23 anatomical bone segments. All other
        // original tracks (clothing, cape, fingers, hair, face, weapon
        // sockets) remain in the fully sampled source pose, just hidden
        // from this readable debug view. Every displayed bone uses its
        // ORIGINAL parent and original world-space endpoints.
        var anatomy=new HashSet<string>(StringComparer.Ordinal){
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
        _segments=Enumerable.Range(0,_bones.Length)
            .Where(i=>_bones[i].Parent>=0 && anatomy.Contains(_bones[i].Name))
            .ToArray();
        if(_segments.Length!=anatomy.Count)
            throw new InvalidDataException("Native humanoid anatomical hierarchy changed");
        VerifyNativeMotion();
    }

    private int FindBone(string name)
    {
        for(int i=0;i<_bones.Length;i++)
            if(_bones[i].Name==name)return i;
        throw new InvalidDataException($"Expected original joint missing: {name}");
    }
    private void VerifyNativeMotion()
    {
        int foot=FindBone("mshBn_foot_L");
        int pelvis=FindBone("mshBn_pelvis");
        int right=FindBone("mshBn_foot_R");
        float footChange=(_frames[_frames.Length/3].World[foot]-_frames[0].World[foot]).Length();
        float otherChange=(_frames[_frames.Length/3].World[right]-_frames[0].World[right]).Length();
        float hipHeight=_frames[0].World[pelvis].Y;
        if(footChange<0.08f||otherChange<0.08f||
           hipHeight is <0.6f or >2.2f)
            throw new InvalidDataException("Original humanoid walk is not moving with expected source-scale");
        var root=FindBone("GrannyRootBone");
        if(_frames.Any(frame=>frame.World[root].Length()>0.0001f))
            throw new InvalidDataException("Original root bone unexpectedly moves");
    }

    private void BuildSourceSkeletonVisual()
    {
        // These are literal line segments between named, ORIGINAL bones.
        // No skinned character, proxy body mesh or artistic pose is generated.
        var mesh=new CylinderMesh
        {
            TopRadius=0.009f,
            BottomRadius=0.009f,
            Height=1f,
            RadialSegments=6
        };
        _lines=new MultiMesh
        {
            Mesh=mesh,
            TransformFormat=MultiMesh.TransformFormatEnum.Transform3D,
            UseColors=true,
            InstanceCount=_segments.Length
        };
        for(int i=0;i<_segments.Length;i++)
        {
            string name=_bones[_segments[i]].Name;
            Color color=name.Contains("thigh",StringComparison.OrdinalIgnoreCase)
                ||name.Contains("shin",StringComparison.OrdinalIgnoreCase)
                ||name.Contains("foot",StringComparison.OrdinalIgnoreCase)
                    ?new Color("#6bc1da"):
                name.Contains("Arm",StringComparison.OrdinalIgnoreCase)
                ||name.Contains("hand",StringComparison.OrdinalIgnoreCase)
                    ?new Color("#d8b16b"):new Color("#b5c5bb");
            _lines.SetInstanceColor(i,color);
        }
        AddChild(new MultiMeshInstance3D
        {
            Name="OriginalESOBoneSegments",
            Multimesh=_lines,
            MaterialOverride=new StandardMaterial3D
            {
                AlbedoColor=Colors.White,
                VertexColorUseAsAlbedo=true,
                ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded
            }
        });
    }

    private void ShowFrame(int frame)
    {
        if(_lines==null||_frames.Length==0)return;
        _currentFrame=Math.Clamp(frame,0,_frames.Length-1);
        var positions=_frames[_currentFrame].World;
        for(int i=0;i<_segments.Length;i++)
        {
            int child=_segments[i],parent=_bones[child].Parent;
            Vector3 a=positions[parent],b=positions[child],difference=b-a;
            float length=difference.Length();
            if(length<0.00001f)
            {
                _lines.SetInstanceTransform(i,new Transform3D(
                    Basis.Identity.Scaled(Vector3.Zero),a));
                continue;
            }
            var rotation=new Quaternion(Vector3.Up,difference/length);
            var basis=new Basis(rotation).Scaled(new Vector3(1f,length,1f));
            _lines.SetInstanceTransform(i,new Transform3D(basis,(a+b)*0.5f));
        }
        if(_hud!=null)
            _hud.Text=$"ORIGINAL ESO ANIMATION  |  {ExpectedAnimationName} | " +
                $"skeleton 0x{SkeletonId:X8} ({NativeBoneCount} bones)\n" +
                $"Granny native world pose, frame {_currentFrame+1}/{NativeFrameCount}, " +
                $"{_frames[_currentFrame].Time:F3}s  |  Space pause  |  " +
                $"← → frame  |  drag orbit  |  wheel zoom  |  Esc exit\n" +
                "SKELETON ONLY: original clothing/face meshes are NOT bound to this animation rig. No approximations.";
    }

    private void ConfigureScene()
    {
        var world=new Godot.Environment
        {
            BackgroundMode=Godot.Environment.BGMode.Color,
            BackgroundColor=new Color("#17252d")
        };
        AddChild(new WorldEnvironment{Environment=world});
        _camera=new Camera3D{Current=true,Fov=42f};
        AddChild(_camera);
        UpdateCamera();
        var canvas=new CanvasLayer{Name="SourceAnimationLabels"};
        AddChild(canvas);
        _hud=new Label{Name="AuthenticityNotice",Position=new Vector2(18,16)};
        _hud.AddThemeFontSizeOverride("font_size",17);
        canvas.AddChild(_hud);
        Input.MouseMode=Input.MouseModeEnum.Visible;
    }

    private void UpdateCamera()
    {
        if(_camera==null)return;
        var target=new Vector3(0,1.15f,0);
        var vector=new Vector3(
            Mathf.Sin(_yaw)*Mathf.Cos(_pitch),
            Mathf.Sin(_pitch),
            Mathf.Cos(_yaw)*Mathf.Cos(_pitch));
        _camera.Position=target+vector*_cameraDistance;
        _camera.LookAt(target,Vector3.Up);
    }

    public override void _Process(double delta)
    {
        if(SelfTest)return;
        if(_playing&&_frames.Length>0)
        {
            _elapsed+=Mathf.Min((float)delta,0.1f);
            _elapsed%=_duration;
            // Never interpolate handmade joint transforms: each frame is
            // evaluated through the installed native Granny runtime.
            int frame=(int)Math.Round(_elapsed*SampleRate);
            ShowFrame(Math.Min(frame,_frames.Length-1));
        }
        string? screenshot=System.Environment.GetEnvironmentVariable("ESO_NATIVE_PREVIEW_PATH");
        if(!_previewWritten&&!string.IsNullOrWhiteSpace(screenshot)&&
           DisplayServer.GetName()!="headless"&&++_previewFrames>=55)
        {
            _previewWritten=true;
            GD.Print($"ORIGINAL RIG PREVIEW {GetViewport().GetTexture().GetImage().SavePng(screenshot)} {screenshot}");
        }
    }

    public override void _Input(InputEvent evt)
    {
        if(SelfTest)return;
        if(evt is InputEventMouseButton mouse)
        {
            if(mouse.ButtonIndex==MouseButton.Left)_orbiting=mouse.Pressed;
            if(mouse.Pressed && mouse.ButtonIndex is
                MouseButton.WheelUp or MouseButton.WheelDown)
            {
                _cameraDistance=Mathf.Clamp(_cameraDistance*
                    (mouse.ButtonIndex==MouseButton.WheelUp?0.85f:1.17f),2f,12f);
                UpdateCamera();
            }
            return;
        }
        if(evt is InputEventMouseMotion move && _orbiting)
        {
            _yaw+=move.Relative.X*0.005f;
            _pitch=Mathf.Clamp(_pitch-move.Relative.Y*0.005f,-0.7f,0.7f);
            UpdateCamera();
        }
        if(evt is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch(key.Keycode)
            {
                case Key.Space:
                    _playing=!_playing;
                    break;
                case Key.Left:
                    _playing=false;
                    ShowFrame(_currentFrame==0?_frames.Length-1:_currentFrame-1);
                    _elapsed=_frames[_currentFrame].Time;
                    break;
                case Key.Right:
                    _playing=false;
                    ShowFrame((_currentFrame+1)%_frames.Length);
                    _elapsed=_frames[_currentFrame].Time;
                    break;
                case Key.Escape:
                    GetTree().Quit();
                    break;
            }
        }
    }
}
