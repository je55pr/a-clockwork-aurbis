using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Godot;
using Process=System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// Three original ESO creature meshes with original, independently archived
/// skeletons and Walk clips evaluated by the installed native Granny runtime.
/// Every source artifact stays in ignored local .research. This is a
/// diagnostic animation gallery, not an authentic texture/shader reconstruction.
/// </summary>
public partial class EsoOriginalCreatureWalkGallery:Node3D
{
    [Export] public bool SelfTest {get;set;}
    [Export] public int CopiesPerSpecies {get;set;}=1;
    [Export] public int MaxAnimatedCopies {get;set;}=72;
    [Export] public bool IncludeCuratedVariants {get;set;}
    private const int Hz=15;
    private sealed record Creature(string Name,string MeshId,string RigId,string ClipId,
        string MeshName,int Vertices,int SkinBinds,int Bones);
    private static readonly Creature[] OriginalSources={
        new("Clannfear","000815D7","00082309","00000E14","Clannfear_A_Basic",2424,54,104),
        new("Daedroth","000A0A61","00046563","0000121C","Daedroth_Armored_Basic",7716,52,102),
        new("Guar","0029B14F","00024A33","00024A69","Guar_XitahranNymph_Pet",4876,52,113)
    };
    private Creature[] _sources=OriginalSources;
    private static Creature[] ReadCuratedVariants()
    {
        string file=Path.Combine(ProjectSettings.GlobalizePath("res://"),
            "data","eso_creature_variants.tsv");
        if(!File.Exists(file))
            throw new FileNotFoundException("Curated source creature catalogue missing",file);
        var variants=new List<Creature>();
        var seen=OriginalSources.Select(x=>x.MeshId).ToHashSet(StringComparer.Ordinal);
        foreach(string raw in File.ReadLines(file))
        {
            string row=raw.Trim();
            if(row.Length==0||row.StartsWith('#'))continue;
            string[] parts=row.Split('\t');
            if(parts.Length!=8||parts[0].Length is <2 or >80||
               parts[4].Length is <2 or >128 ||
               !parts[0].All(c=>char.IsAsciiLetterOrDigit(c))||
               !int.TryParse(parts[5],out int vertices)||
               !int.TryParse(parts[6],out int bindings)||
               !int.TryParse(parts[7],out int bones)||
               vertices is <100 or >100000||
               bindings is <10 or >256||bones is <20 or >256)
                throw new InvalidDataException("Invalid original creature catalogue row: "+row);
            foreach(int at in new[]{1,2,3})
                if(parts[at].Length!=8||
                   !uint.TryParse(parts[at],System.Globalization.NumberStyles.HexNumber,
                       CultureInfo.InvariantCulture,out _))
                    throw new InvalidDataException("Invalid original creature resource ID");
            if(!seen.Add(parts[1])||variants.Any(v=>v.Name==parts[0]))
                throw new InvalidDataException("Duplicate original creature model");
            variants.Add(new Creature(parts[0],parts[1],parts[2],parts[3],
                parts[4],vertices,bindings,bones));
            if(variants.Count>150)
                throw new InvalidDataException("Original creature catalogue exceeds limit");
        }
        return variants.ToArray();
    }

    private readonly record struct SourceBone(Vector3 Position,Quaternion Orientation);
    private sealed record Pose(float Time,SourceBone[] Bones,Vector3[] WorldPositions);
    private sealed class AnimatedCreature
    {
        public required Creature Source;
        public required Node3D Container;
        public required Skeleton3D Skeleton;
        public required MeshInstance3D Mesh;
        public required Material DiagnosticMaterial;
        public Material? CandidateMaterial;
        public required Pose[] Frames;
        public required float Duration;
        public int Frame;
    }
    private readonly List<AnimatedCreature> _creatures=new();
    private readonly List<AnimatedCreature> _crowd=new();
    public int TotalCreatureInstances=>_creatures.Count+_crowd.Count;
    public int UniqueCreatureMeshes=>_creatures.Select(c=>c.Mesh.Mesh).Distinct().Count();
    public int SharedOriginalMeshCopies=>_crowd.Count(c=>_creatures.Any(s=>
        s.Source.MeshId==c.Source.MeshId &&
        ReferenceEquals(s.Mesh.Mesh,c.Mesh.Mesh)));
    private float _lastPoseUpdate=-1;
    private EsoOriginalCreatureTextures? _textureLibrary;
    private bool _showTextureCandidates=true;
    public int CandidateMaterialCount=>_creatures.Count(c=>c.CandidateMaterial!=null);
    public bool TextureCandidatesVisible=>_showTextureCandidates;
    public bool HasCandidateMaterial(int index)=>_creatures[index].CandidateMaterial!=null;
    private Camera3D? _camera;
    private Label? _hud;
    private bool _playing=true,_dragging;
    private int _focus=-1;
    private float _elapsed,_yaw=0.24f,_pitch=0.12f,_distance=19f;
    private float _maxDeviation;
    public int OriginalCreatureCount=>_creatures.Count;
    public float MaxNativePoseDeviation=>_maxDeviation;
    public string GetCreatureName(int index)=>_creatures[index].Source.Name;
    public int GetFrame(int index)=>_creatures[index].Frame;
    public Vector3 GetWorldBone(int index,string name)
    {
        var rig=_creatures[index].Skeleton;
        int bone=rig.FindBone(name);
        if(bone<0)throw new ArgumentException("Original source bone missing: "+name);
        return rig.GetBoneGlobalPose(bone).Origin;
    }

    private Vector3 PositionFor(int slot)
    {
        if(CopiesPerSpecies==1)return new Vector3((slot-1)*8f,0,0);
        int total=CopiesPerSpecies*_sources.Length;
        int columns=(int)Math.Ceiling(Math.Sqrt(total*1.4));
        int rows=(total+columns-1)/columns;
        return new Vector3(
            ((slot%columns)-(columns-1)*0.5f)*6.5f,
            0,
            ((slot/columns)-(rows-1)*0.5f)*7.2f);
    }

    private void CreateCopies(AnimatedCreature original,EsoSourceRig source)
    {
        int speciesIndex=_creatures.Count-1;
        for(int copy=1;copy<CopiesPerSpecies;copy++)
        {
            var (skeleton,skin)=source.BuildSkeleton();
            if(skin.GetBindCount()!=original.Mesh.Skin.GetBindCount())
                throw new InvalidDataException("Source crowd copy native skin changed");
            var container=new Node3D
            {
                Name=$"OriginalCrowd_{original.Source.Name}_{copy:D3}",
                Position=PositionFor(speciesIndex*CopiesPerSpecies+copy)
            };
            AddChild(container);
            skeleton.Name="NativeCreatureSkeleton";
            container.AddChild(skeleton);
            var body=new MeshInstance3D
            {
                Name="OriginalCreatureMesh", Mesh=original.Mesh.Mesh,
                Skin=original.Mesh.Skin, Skeleton=new NodePath(".."),
                MaterialOverride=original.Mesh.MaterialOverride
            };
            skeleton.AddChild(body);
            var member=new AnimatedCreature
            {
                Source=original.Source,Container=container,Skeleton=skeleton,
                Mesh=body,Frames=original.Frames,Duration=original.Duration,
                DiagnosticMaterial=original.DiagnosticMaterial,
                CandidateMaterial=original.CandidateMaterial
            };
            _crowd.Add(member);
            ApplyPose(member,0);
        }
    }

    private static string Root=>ProjectSettings.GlobalizePath("res://");
    private static string Cache=>Path.Combine(Root,".research","npc-rigs");
    private static string Helper=>Path.Combine(Root,"tools","ModelGeometry","bin","Debug","net8.0","ModelGeometry.dll");
    private static string Granny=>System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
        @"C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll";
    private static string Manifest=>System.Environment.GetEnvironmentVariable("ESO_MNF")??
        @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
    private static string Oodle=>Path.Combine(Root,".research","ooz.dll");
    private static string MeshRig(Creature c)=>Path.Combine(Cache,"creature-"+c.MeshId+".json");
    private static string MeshGeo(Creature c)=>Path.Combine(Cache,"creature-"+c.MeshId+".esm");
    private static string RigInfo(Creature c)=>Path.Combine(Cache,"creature-skeleton-"+c.RigId+".json");
    private static string PoseInfo(Creature c)=>Path.Combine(Cache,
        "native-walk-"+c.RigId+"-"+c.ClipId+"-"+Hz+"fps.json");

    private static string RunHelper(params string[] arguments)
    {
        foreach(string dependency in new[]{Helper,Granny,Manifest,Oodle})
            if(!File.Exists(dependency))
                throw new FileNotFoundException("Required installed ESO source missing: "+dependency);
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Root,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        process.StartInfo.ArgumentList.Add(Helper);
        foreach(string arg in arguments)process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())throw new IOException("Could not start isolated original asset reader");
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Original ESO source reader exceeded 45 seconds");
        }
        string output=stdout.GetAwaiter().GetResult();
        string errors=stderr.GetAwaiter().GetResult();
        if(process.ExitCode!=0)
            throw new InvalidDataException("Original ESO decoder refused "+
                string.Join(" ",arguments.Take(3))+": "+
                errors[..Math.Min(240,errors.Length)]+
                output[..Math.Min(140,output.Length)]);
        return output;
    }

    private static void EnsureSources(Creature source)
    {
        Directory.CreateDirectory(Cache);
        if(!File.Exists(MeshRig(source)))
            File.WriteAllText(MeshRig(source),RunHelper(
                "--inspect-rig",Granny,source.MeshId,Manifest,Oodle));
        if(!File.Exists(MeshGeo(source)))
            RunHelper(Granny,source.MeshId,MeshGeo(source),Manifest,Oodle);
        if(!File.Exists(RigInfo(source)))
            File.WriteAllText(RigInfo(source),RunHelper(
                "--inspect-rig",Granny,source.RigId,Manifest,Oodle));
        if(!File.Exists(PoseInfo(source)))
            RunHelper("--sample-native-pose",Granny,source.RigId,source.ClipId,
                Manifest,Oodle,PoseInfo(source),Hz.ToString());
    }

    private static (Pose[] Frames,float Duration) ReadClip(Creature source,EsoSourceRig rig)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(PoseInfo(source)));
        var root=document.RootElement;
        if(root.GetProperty("SkeletonId").GetString()!=source.RigId||
           root.GetProperty("AnimationId").GetString()!=source.ClipId||
           root.GetProperty("SkeletonName").GetString()!="GrannyRootBone"||
           root.GetProperty("AnimationName").GetString()!="Walk"||
           root.GetProperty("FrameRate").GetInt32()!=Hz)
            throw new InvalidDataException("Original native creature animation identity mismatch");
        float duration=root.GetProperty("DurationSeconds").GetSingle();
        if(!float.IsFinite(duration)||duration<0.2f||duration>20f)
            throw new InvalidDataException("Unexpected original walk duration");
        var bones=root.GetProperty("Bones").EnumerateArray().ToArray();
        if(bones.Length!=source.Bones)
            throw new InvalidDataException("Original animation skeleton bone count mismatch");
        for(int bone=0;bone<bones.Length;bone++)
            if(bones[bone].GetProperty("Name").GetString()!=rig.Bones[bone].Name||
               bones[bone].GetProperty("Parent").GetInt32()!=rig.Bones[bone].Parent)
                throw new InvalidDataException("Native animation bone order/hierarchy does not match source mesh rig");
        var result=new List<Pose>();
        foreach(var frame in root.GetProperty("Frames").EnumerateArray())
        {
            float time=frame.GetProperty("Time").GetSingle();
            var local=frame.GetProperty("Bones");
            var world=frame.GetProperty("WorldPositions");
            if(!float.IsFinite(time)||time<0||time>duration+0.0001f||
               (result.Count>0&&time<=result[^1].Time)||
               local.GetArrayLength()!=source.Bones||world.GetArrayLength()!=source.Bones)
                throw new InvalidDataException("Original native creature sample malformed");
            var frameBones=new SourceBone[source.Bones];
            var worldPositions=new Vector3[source.Bones];
            for(int bone=0;bone<source.Bones;bone++)
            {
                var pos=local[bone].GetProperty("Position");
                var rot=local[bone].GetProperty("Orientation");
                var shear=local[bone].GetProperty("ScaleShear");
                var expected=world[bone];
                if(pos.GetArrayLength()!=3||rot.GetArrayLength()!=4||
                   shear.GetArrayLength()!=9||expected.GetArrayLength()!=3)
                    throw new InvalidDataException("Malformed original Granny bone matrix");
                var p=new Vector3(pos[0].GetSingle(),pos[1].GetSingle(),pos[2].GetSingle());
                var q=new Quaternion(rot[0].GetSingle(),rot[1].GetSingle(),
                    rot[2].GetSingle(),rot[3].GetSingle());
                var w=new Vector3(expected[0].GetSingle(),expected[1].GetSingle(),
                    expected[2].GetSingle());
                if(!p.IsFinite()||!w.IsFinite()||!q.IsNormalized())
                    throw new InvalidDataException("Original source contains invalid native pose");
                for(int n=0;n<9;n++)
                {
                    float value=shear[n].GetSingle();
                    if(!float.IsFinite(value)||Math.Abs(value-(n%4==0?1f:0f))>0.0005f)
                        throw new InvalidDataException("Unsupported original creature bone shear");
                }
                frameBones[bone]=new SourceBone(p,q);
                worldPositions[bone]=w;
            }
            result.Add(new Pose(time,frameBones,worldPositions));
        }
        if(result.Count<4||result.Count>1200||
           Math.Abs(result[^1].Time-duration)>0.0003f)
            throw new InvalidDataException("Incomplete original creature animation samples");
        return (result.ToArray(),duration);
    }

    private static void ApplyPose(AnimatedCreature model,int frame)
    {
        int selected=Math.Clamp(frame,0,model.Frames.Length-1);
        var data=model.Frames[selected].Bones;
        for(int i=0;i<data.Length;i++)
        {
            model.Skeleton.SetBonePosePosition(i,data[i].Position);
            model.Skeleton.SetBonePoseRotation(i,data[i].Orientation);
        }
        model.Frame=selected;
    }

    public override void _Ready()
    {
        try
        {
            if(CopiesPerSpecies is <1 or >160||MaxAnimatedCopies is <0 or >500)
                throw new InvalidDataException("Creature crowd size or active animation cap invalid");
            _sources=IncludeCuratedVariants
                ?OriginalSources.Concat(ReadCuratedVariants()).ToArray()
                :OriginalSources;
            string localTextures=Path.Combine(Cache,"creature-texture-candidates.tsv");
            string sourceCandidateIndex=Path.Combine(Root,"data",
                "eso_creature_texture_candidates.tsv");
            string selectedTextureIndex=File.Exists(localTextures)
                ?localTextures:sourceCandidateIndex;
            if(File.Exists(selectedTextureIndex))
                _textureLibrary=new EsoOriginalCreatureTextures(
                    Manifest,Oodle,selectedTextureIndex);
            foreach(var source in _sources)
            {
                EnsureSources(source);
                string binding=RunHelper("--inspect-native-external-binding",
                    Granny,source.MeshId,source.RigId,Manifest,Oodle);
                string correct="total="+source.SkinBinds+", exact="+source.SkinBinds+
                    ", name-differing=0, index-unmapped=0";
                if(!binding.Contains(correct,StringComparison.Ordinal)||
                   !binding.Contains("COMPATIBLE_SOURCE_RIG:",StringComparison.Ordinal))
                    throw new InvalidDataException(source.Name+" native skin binding incomplete");
                var rig=EsoSourceRig.ReadExternal(MeshRig(source),RigInfo(source));
                if(rig.MeshName!=source.MeshName||
                   rig.Bones.Count!=source.Bones||
                   rig.VertexCount!=source.Vertices||
                   rig.BindingNames.Length!=source.SkinBinds)
                    throw new InvalidDataException(source.Name+" original mesh/rig identity mismatch");
                var mesh=EsoHumanoidVisualLayer.LoadMesh(
                    MeshGeo(source),source.MeshName,rig,enforceHumanoidBounds:false);
                if(mesh.GetSurfaceCount()!=1||
                   mesh.SurfaceGetArrayLen(0)!=source.Vertices)
                    throw new InvalidDataException(source.Name+" source geometry incomplete");
                var (skeleton,skin)=rig.BuildSkeleton();
                if(skin.GetBindCount()!=source.SkinBinds||
                   skeleton.GetBoneCount()!=source.Bones)
                    throw new InvalidDataException(source.Name+" source skin incomplete");
                var (frames,duration)=ReadClip(source,rig);
                var container=new Node3D
                {
                    Name="SourceCreature_"+source.Name,
                    Position=PositionFor(_creatures.Count*CopiesPerSpecies)
                };
                AddChild(container);
                skeleton.Name="NativeCreatureSkeleton";
                container.AddChild(skeleton);
                Material neutralMaterial=new StandardMaterial3D
                {
                    AlbedoColor=new Color("#b9c4c7"),
                    Roughness=0.78f,
                    CullMode=BaseMaterial3D.CullModeEnum.Disabled
                };
                uint id=uint.Parse(source.MeshId,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
                Material? candidateMaterial=_textureLibrary?.LoadMaterial(id);
                var body=new MeshInstance3D
                {
                    Name="OriginalCreatureMesh",Mesh=mesh,Skin=skin,
                    Skeleton=new NodePath(".."),
                    // An explicitly local source DDS *candidate* is never
                    // silently treated as an authoritative ESO runtime map.
                    MaterialOverride=_showTextureCandidates&&candidateMaterial!=null
                        ?candidateMaterial:neutralMaterial
                };
                skeleton.AddChild(body);
                container.AddChild(new Label3D
                {
                    Text=source.Name+" / ESO Walk",
                    Position=new Vector3(0,4.8f,0),
                    FontSize=60,PixelSize=0.004f
                });
                var model=new AnimatedCreature
                {
                    Source=source,Container=container,Skeleton=skeleton,
                    Mesh=body,Frames=frames,Duration=duration,DiagnosticMaterial=neutralMaterial,CandidateMaterial=candidateMaterial
                };
                _creatures.Add(model);
                ApplyPose(model,0);
                if(CopiesPerSpecies>1)CreateCopies(model,rig);
            }
            if(SelfTest)
            {
                VerifyNativeFrames();
                if(_crowd.Count!=_sources.Length*(CopiesPerSpecies-1)||
                   SharedOriginalMeshCopies!=_crowd.Count)
                    throw new InvalidDataException("Crowd source mesh sharing invalid");
                GD.Print($"PASS {_sources.Length} original ESO creature meshes, native "+
                    $"source Walk at {Hz}fps; maximum independent Granny/Godot bone deviation "+
                    _maxDeviation.ToString("F7")+" m. "+
                    $"instances={TotalCreatureInstances}, unique-meshes={UniqueCreatureMeshes}, "+
                    $"shared-geometry-copies={SharedOriginalMeshCopies}.");
                GetTree().Quit(0);
                return;
            }
            BuildView();
            SetProcess(true);
        }
        catch(Exception ex)
        {
            GD.PushError("Original ESO creature walk lab failed closed: "+ex);
            GetTree().Quit(1);
        }
    }

    private void VerifyNativeFrames()
    {
        foreach(var creature in _creatures)
        {
            var bone=creature.Skeleton.FindBone("mshBn_head");
            if(bone<0)throw new InvalidDataException("Native creature head bone missing");
            float movement=0f;
            Vector3 initial=creature.Frames[0].WorldPositions[bone];
            for(int frame=0;frame<creature.Frames.Length;frame++)
            {
                ApplyPose(creature,frame);
                movement=Math.Max(movement,
                    initial.DistanceTo(creature.Frames[frame].WorldPositions[bone]));
                for(int i=0;i<creature.Source.Bones;i++)
                {
                    float error=creature.Skeleton.GetBoneGlobalPose(i).Origin.
                        DistanceTo(creature.Frames[frame].WorldPositions[i]);
                    if(!float.IsFinite(error)||error>0.003f)
                        throw new InvalidDataException("Original Granny world position mismatch: "+
                            creature.Source.Name+" frame "+frame+" bone "+i+
                            " deviation="+error);
                    _maxDeviation=Math.Max(_maxDeviation,error);
                }
            }
            if(movement<0.03f)
                throw new InvalidDataException("Original "+creature.Source.Name+
                    " clip does not animate its head");
            ApplyPose(creature,0);
        }
    }

    private void BuildView()
    {
        AddChild(new WorldEnvironment{Environment=new Godot.Environment
        {
            BackgroundMode=Godot.Environment.BGMode.Color,
            BackgroundColor=new Color("#14242a"),
            AmbientLightSource=Godot.Environment.AmbientSource.Color,
            AmbientLightColor=new Color("#b1c0c8"),
            AmbientLightEnergy=0.84f
        }});
        AddChild(new DirectionalLight3D{
            RotationDegrees=new Vector3(-40f,24f,0f),LightEnergy=1.7f});
        if(CopiesPerSpecies>1)_distance=Math.Max(19f,MathF.Sqrt(
            CopiesPerSpecies*_sources.Length)*9.5f);
        _camera=new Camera3D{
            Name="CreatureGalleryCamera",Current=true,Fov=46f,Near=0.03f,Far=950f};
        AddChild(_camera);
        var canvas=new CanvasLayer{Name="CreatureGalleryHud"};
        AddChild(canvas);
        _hud=new Label{Name="CreatureGalleryLabels",Position=new Vector2(16,13)};
        _hud.AddThemeFontSizeOverride("font_size",16);
        canvas.AddChild(_hud);
        Input.MouseMode=Input.MouseModeEnum.Visible;
        UpdateCamera();
        UpdateAnimations();
    }

    private void UpdateCamera()
    {
        if(_camera==null)return;
        Vector3 target;
        if(_focus<0)target=new Vector3(0,1.9f,0);
        else if(_crowd.Count==0)
            target=_creatures[_focus].Container.Position+new Vector3(0,1.9f,0);
        else
        {
            var selection=_crowd.Where(c=>c.Source.MeshId==_creatures[_focus].Source.MeshId)
                .Select(c=>c.Container.Position).Append(_creatures[_focus].Container.Position).ToArray();
            target=selection.Aggregate(Vector3.Zero,(a,b)=>a+b)/selection.Length+
                new Vector3(0,1.9f,0);
        }
        float distance=_focus<0?_distance:Math.Clamp(_distance*0.49f,4f,16f);
        var axis=new Vector3(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),
            Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch));
        _camera.Position=target+axis*distance;
        _camera.LookAt(target,Vector3.Up);
        for(int i=0;i<_creatures.Count;i++)
            _creatures[i].Container.Visible=_focus<0||_focus==i;
        foreach(var creature in _crowd)
            creature.Container.Visible=_focus<0||
                creature.Source.MeshId==_creatures[_focus].Source.MeshId;
    }

    private void UpdateAnimations()
    {
        foreach(var creature in _creatures)
        {
            float time=_elapsed%creature.Duration;
            if(time<0)time+=creature.Duration;
            int frame=(int)Math.Round(time*Hz);
            if(frame!=creature.Frame)ApplyPose(creature,frame);
        }
        if(_crowd.Count>0&&MaxAnimatedCopies>0)
        {
            var origin=_camera?.Position??Vector3.Zero;
            foreach(var member in _crowd.Where(x=>x.Container.Visible)
                .OrderBy(x=>x.Container.Position.DistanceSquaredTo(origin))
                .Take(MaxAnimatedCopies))
            {
                float phase=((member.Container.GetIndex()*0.618034f)%1f)*member.Duration;
                float t=(_elapsed+phase)%member.Duration;
                if(t<0)t+=member.Duration;
                int frame=(int)Math.Round(t*Hz);
                if(frame!=member.Frame)ApplyPose(member,frame);
            }
        }
        if(_hud!=null)
            _hud.Text="ORIGINAL ESO CREATURE WALK LAB\n"+
                "Clannfear / Daedroth / Guar\n"+
                $"Instances: {TotalCreatureInstances}, shared original meshes: {UniqueCreatureMeshes}\n"+
                (_crowd.Count>0?$"Animation cap: {MaxAnimatedCopies} nearby copies\n":"")+
                "Source geometry, skeleton and native Walk\n"+
                (_playing?"PLAYING":"PAUSED")+"\n"+
                "1-3 focus first three | PageUp/Down next species | 0 all\n"+
                "Space pause | Left/Right frame step\n"+
                "Drag orbit | Wheel zoom | Esc exit\n"+
                $"DDS candidates: {CandidateMaterialCount}/{_creatures.Count} (UNVERIFIED ASSOCIATIONS)\n"+
                "T toggle original DDS candidates | assignments NOT game-verified";
    }
    public override void _Process(double delta)
    {
        if(SelfTest)return;
        if(_playing)
        {
            _elapsed+=Mathf.Min((float)delta,0.1f);
            if(_elapsed-_lastPoseUpdate>=1f/Hz)
            {
                _lastPoseUpdate=_elapsed;
                UpdateAnimations();
            }
        }
    }
    public override void _ExitTree()
    {
        _textureLibrary?.Dispose();
        _textureLibrary=null;
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
                    (mouse.ButtonIndex==MouseButton.WheelUp?0.86f:1.16f),5f,35f);
                UpdateCamera();
            }
            return;
        }
        if(evt is InputEventMouseMotion motion&&_dragging)
        {
            _yaw+=motion.Relative.X*0.005f;
            _pitch=Mathf.Clamp(_pitch-motion.Relative.Y*0.005f,-0.7f,0.7f);
            UpdateCamera();
            return;
        }
        if(evt is not InputEventKey key||!key.Pressed||key.Echo)return;
        switch(key.Keycode)
        {
            case Key.T:
                _showTextureCandidates=!_showTextureCandidates;
                foreach(var creature in _creatures.Concat(_crowd))
                    creature.Mesh.MaterialOverride=_showTextureCandidates&&
                        creature.CandidateMaterial!=null
                        ?creature.CandidateMaterial:creature.DiagnosticMaterial;
                UpdateAnimations();
                break;
            case Key.Space:
                _playing=!_playing;UpdateAnimations();break;
            case Key.Key0:
                _focus=-1;UpdateCamera();break;
            case Key.Key1 or Key.Key2 or Key.Key3:
                _focus=(int)key.Keycode-(int)Key.Key1;UpdateCamera();break;
            case Key.Left:
                _playing=false;_elapsed=Math.Max(0,_elapsed-1f/Hz);
                UpdateAnimations();break;
            case Key.Right:
                _playing=false;_elapsed+=1f/Hz;UpdateAnimations();break;
            case Key.Pagedown:
                _focus=(_focus+1)%_creatures.Count;UpdateCamera();break;
            case Key.Pageup:
                _focus=(_focus<1?_creatures.Count:_focus)-1;UpdateCamera();break;
            case Key.Escape:
                GetTree().Quit();break;
        }
    }
}
