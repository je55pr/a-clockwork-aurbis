using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Godot;
using Process=System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// One actual original ESO eight-piece humanoid at the ORIGINAL source rest
/// pose. No guessed joint remap, synthetic face, skin tones, material IDs,
/// hairstyle or motion: the animation/customization bridge is unresolved.
/// The eight independent source GR2 rigs are merged only if every common
/// inverse bind matrix and parent name is identical.
/// </summary>
public partial class EsoOriginalHumanoid601AssemblyLab:Node3D
{
    private sealed record Part(uint Id,string Name,int Vertices,int BindingCount);
    private static readonly Part[] Parts=
    {
        new(0x001230D9,"ArmsLower_601",90,4),
        new(0x001230DB,"ArmsUpper_601",218,10),
        new(0x001230DD,"Hands_601",554,14),
        new(0x001230DF,"Torso_601",1462,22),
        new(0x001230E1,"Head_601",1443,38),
        new(0x001230E3,"LegsLower_601",230,10),
        new(0x001230E5,"LegsUpper_601",2328,8),
        new(0x001230E7,"Feet_601",426,6)
    };
    // This original female hairstyle binds to Head_601 by all ten names
    // through native Granny and has exactly identical original source rests.
    private static readonly Part OriginalHair=
        new(0x0029A87F,"HAR_F_HU_0343",2491,10);
    [Export]public bool SelfTest{get;set;}
    public int OriginalHairVertexCount{get;private set;}
    public int OriginalHairBindCount{get;private set;}
    public bool HasOriginalHairDdsCandidate=>_hairCandidateMaterial!=null;
    public bool OriginalHairDdsVisible{get;private set;}=true;
    public bool OriginalHairTintMaskVisible{get;private set;}
    private MeshInstance3D? _hairInstance;
    private ShaderMaterial? _hairCandidateMaterial;
    private Material? _hairNeutralMaterial;
    public int SourcePartCount{get;private set;}
    public int SourceVertexCount{get;private set;}
    public int MergedBoneCount{get;private set;}
    public int ExactSharedBoneReferences{get;private set;}
    public bool IsOriginalCustomizationRest{get;private set;}
    private Camera3D? _camera;
    private float _yaw=0.17f,_pitch=0.09f,_distance=4.3f;
    private bool _dragging;
    private Vector3 _target=new(0,1.15f,0);
    private static string Root=>ProjectSettings.GlobalizePath("res://");
    private static string Cache=>Path.Combine(Root,".research","npc-rigs");
    private static string Helper=>Path.Combine(Root,"tools","ModelGeometry","bin",
        "Debug","net8.0","ModelGeometry.dll");
    private static string Granny=>System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
        @"C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll";
    private static string Manifest=>System.Environment.GetEnvironmentVariable("ESO_MNF")??
        @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
    private static string Oodle=>Path.Combine(Root,".research","ooz.dll");

    private static string Extract(string[] args)
    {
        foreach(string path in new[]{Helper,Granny,Manifest,Oodle})
            if(!File.Exists(path))
                throw new FileNotFoundException("Original installed ESO source dependency missing: "+path);
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            WorkingDirectory=Root,
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        process.StartInfo.ArgumentList.Add(Helper);
        foreach(var arg in args)process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())
            throw new IOException("Original ESO GR2 extractor did not start");
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Original ESO source extraction exceeded timeout");
        }
        string output=stdout.GetAwaiter().GetResult();
        string error=stderr.GetAwaiter().GetResult();
        if(process.ExitCode!=0)
            throw new InvalidDataException(
                "Native original humanoid GR2 extraction failed: "+
                error[..Math.Min(error.Length,500)]);
        return output;
    }

    private static (string Rig,string Mesh) Prepare(Part part)
    {
        Directory.CreateDirectory(Cache);
        string hex=part.Id.ToString("X8");
        string prefix=Path.Combine(Cache,"original-humanoid601-"+hex);
        string rig=prefix+"-rig.json",mesh=prefix+"-mesh.esm";
        if(!File.Exists(rig))
            File.WriteAllText(rig,
                Extract(new[]{"--inspect-rig",Granny,hex,Manifest,Oodle}));
        if(!File.Exists(mesh))
            Extract(new[]{Granny,hex,mesh,Manifest,Oodle});
        return (rig,mesh);
    }

    public override void _Ready()
    {
        try
        {
            var rigs=new List<EsoSourceRig>();
            var meshPaths=new List<string>();
            foreach(Part part in Parts)
            {
                var (rigPath,meshPath)=Prepare(part);
                var source=EsoSourceRig.Read(rigPath);
                if(source.MeshName!=part.Name||
                   source.VertexCount!=part.Vertices||
                   source.BindingNames.Length!=part.BindingCount||
                   source.Bones.Count is <5 or >150)
                    throw new InvalidDataException(
                        $"Original humanoid 601 body part identity changed: {part.Name}");
                rigs.Add(source);
                meshPaths.Add(meshPath);
            }

            var merged=EsoSourceRig.BuildIdenticalRestAssembly(rigs);
            if(merged.UniqueBones!=110||merged.SharedReferences!=98)
                throw new InvalidDataException(
                    "Original ESO humanoid rest agreement was not reproduced");
            MergedBoneCount=merged.UniqueBones;
            ExactSharedBoneReferences=merged.SharedReferences;
            var skeleton=merged.Skeleton;
            AddChild(skeleton);
            var material=new StandardMaterial3D
            {
                AlbedoColor=new Color("#b7bec4"),
                Roughness=0.85f,
                CullMode=BaseMaterial3D.CullModeEnum.Disabled
            };
            Aabb? total=null;
            for(int i=0;i<Parts.Length;i++)
            {
                var part=Parts[i];
                ArrayMesh sourceMesh=EsoHumanoidVisualLayer.LoadMesh(
                    meshPaths[i],part.Name,rigs[i]);
                if(sourceMesh.GetSurfaceCount()!=1 ||
                   sourceMesh.SurfaceGetArrayLen(0)!=part.Vertices||
                   merged.Skins[i].GetBindCount()!=part.BindingCount)
                    throw new InvalidDataException(
                        "Original humanoid mesh / source skin mismatch: "+part.Name);
                var instance=new MeshInstance3D
                {
                    Name="Original601_"+part.Name,
                    Mesh=sourceMesh,Skin=merged.Skins[i],
                    Skeleton=new NodePath(".."),
                    MaterialOverride=material
                };
                skeleton.AddChild(instance);
                var aabb=sourceMesh.GetAabb();
                total=total is null?aabb:total.Value.Merge(aabb);
                SourcePartCount++;
                SourceVertexCount+=part.Vertices;
            }
            if(SourcePartCount!=8||SourceVertexCount!=6751||total is null)
                throw new InvalidDataException("Original eight-piece humanoid incomplete");

            var (hairRigPath,hairMeshPath)=Prepare(OriginalHair);
            string native=Extract(new[]{"--inspect-native-binding",Granny,
                OriginalHair.Id.ToString("X8"),"001230E1",Manifest,Oodle});
            if(!native.Contains("total=10, exact=10, name-differing=0, index-unmapped=0",
                    StringComparison.Ordinal)||
               !native.Contains("COMPATIBLE_SOURCE_RIG:",StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Original Granny rejected Head_601 and HAR_F_HU_0343 binding");
            var sourceHair=EsoSourceRig.Read(hairRigPath);
            if(sourceHair.MeshName!=OriginalHair.Name||
               sourceHair.VertexCount!=OriginalHair.Vertices||
               sourceHair.BindingNames.Length!=OriginalHair.BindingCount)
                throw new InvalidDataException("Original ESO hairstyle source identity changed");
            Skin hairSkin=sourceHair.BindToMergedSourceRest(skeleton);
            ArrayMesh hairMesh=EsoHumanoidVisualLayer.LoadMesh(
                hairMeshPath,OriginalHair.Name,sourceHair);
            if(hairMesh.SurfaceGetArrayLen(0)!=OriginalHair.Vertices ||
               hairSkin.GetBindCount()!=OriginalHair.BindingCount)
                throw new InvalidDataException("Original ESO hair source skin changed");
            string hairTextureIds=Path.Combine(Root,"data",
                "eso_humanoid_hair_texture_candidates.tsv");
            using(var textures=new EsoOriginalCreatureTextures(
                Manifest,Oodle,hairTextureIds))
            {
                var mapping=textures.SourceMapping(OriginalHair.Id);
                if(textures.MappingCount!=1||mapping==null||
                   mapping.Evidence!="archive-neighbor-provisional"||
                   mapping.Diffuse!=0x0029A877u||
                   mapping.Normal!=0x0029A879u||
                   mapping.Specular!=0x0029A87Bu||
                   mapping.Tint!=0x0029A87Du)
                    throw new InvalidDataException(
                        "Original hairstyle DDS candidate ID provenance changed");
                _hairCandidateMaterial=textures.LoadAlphaCutoutMaterial(
                    OriginalHair.Id)??
                    throw new InvalidDataException(
                        "Original female hair DXT5 diffuse/AG normal/specular could not be decoded");
            }
            _hairNeutralMaterial=new StandardMaterial3D
            {
                AlbedoColor=new Color("#8b919a"),
                Roughness=0.88f,
                CullMode=BaseMaterial3D.CullModeEnum.Disabled
            };
            _hairInstance=new MeshInstance3D
            {
                Name="OriginalHair_0029A87F",
                Mesh=hairMesh,Skin=hairSkin,Skeleton=new NodePath(".."),
                MaterialOverride=_hairCandidateMaterial
            };
            skeleton.AddChild(_hairInstance);
            OriginalHairVertexCount=OriginalHair.Vertices;
            OriginalHairBindCount=hairSkin.GetBindCount();
            IsOriginalCustomizationRest=true;
            GD.Print($"PASS original ESO humanoid 601 complete rest assembly: "+
                $"{SourcePartCount} untouched original meshes, "+
                $"{SourceVertexCount} original vertices, {MergedBoneCount} "+
                $"source bones, {ExactSharedBoneReferences} exact duplicate "+
                $"rest matrices/parent links; {OriginalHairVertexCount} original hair "+
                $"vertices with {OriginalHairBindCount}/{OriginalHairBindCount} native "+
                "head binds. No guessed skeleton conversion.");
            if(SelfTest)
            {
                // Regression against accidentally mixing a different original
                // humanoid face with this native body family. The original
                // Head_600 has incompatible facial inverse-world rests.
                string foreignPath=Path.Combine(Cache,
                    "original-humanoid601-negative-head600-rig.json");
                if(!File.Exists(foreignPath))
                    File.WriteAllText(foreignPath,Extract(new[]{
                        "--inspect-rig",Granny,"001076DD",Manifest,Oodle}));
                var incompatible=EsoSourceRig.Read(foreignPath);
                var mixed=new List<EsoSourceRig>(rigs);
                mixed[4]=incompatible;
                bool correctlyRejected=false;
                try
                {
                    EsoSourceRig.BuildIdenticalRestAssembly(mixed);
                }
                catch(InvalidDataException)
                {
                    correctlyRejected=true;
                }
                if(!correctlyRejected)
                    throw new InvalidDataException(
                        "Incompatible original Head_600 was silently accepted");
                GD.Print("PASS source-rest collision negative: "+
                    "original Head_600 cannot be substituted for Head_601");
                GetTree().Quit(0);
                return;
            }
            SetupDisplay(total.Value);
        }
        catch(Exception e)
        {
            GD.PushError("Original ESO humanoid source-rest assembly failed: "+e);
            GetTree().Quit(1);
        }
    }
    private void SetupDisplay(Aabb bounds)
    {
        AddChild(new WorldEnvironment
        {
            Environment=new Godot.Environment
            {
                BackgroundMode=Godot.Environment.BGMode.Color,
                BackgroundColor=new Color("#1b252d"),
                AmbientLightSource=Godot.Environment.AmbientSource.Color,
                AmbientLightColor=new Color("#ced6da"),
                AmbientLightEnergy=0.85f
            }
        });
        AddChild(new DirectionalLight3D
        {
            RotationDegrees=new Vector3(-35,-22,0),
            LightEnergy=1.4f
        });
        _target=bounds.GetCenter();
        _distance=Math.Max(2.3f,bounds.Size.Length()*1.1f);
        _camera=new Camera3D
        {
            Name="Original601Camera",Current=true,Fov=45
        };
        AddChild(_camera);
        var canvas=new CanvasLayer();
        AddChild(canvas);
        var label=new Label
        {
            Name="Original601Info",
            Position=new Vector2(15,15),
            Text="ORIGINAL ESO 601 / SOURCE REST\n"+
                "8 body parts / 110 source joints\n"+
                "Hair: 2,491 vertices / 10 native binds\n"+
                "Hair DDS candidate D/N/S/T (unverified)\n"+
                "Skin & animation still unresolved\n"+
                "T: neutral hair / Y: source tint-mask R (diagnostic)\n"+
                "Drag: orbit / Wheel: zoom"
        };
        label.AddThemeFontSizeOverride("font_size",17);
        canvas.AddChild(label);
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        if(_camera==null)return;
        var vec=new Vector3(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),
            Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch));
        _camera.Position=_target+vec*_distance;
        _camera.LookAt(_target,Vector3.Up);
    }
    public override void _Input(InputEvent evt)
    {
        if(SelfTest)return;
        if(evt is InputEventMouseButton button)
        {
            if(button.ButtonIndex==MouseButton.Left)
                _dragging=button.Pressed;
            if(button.Pressed&&button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                _distance=Mathf.Clamp(_distance*
                    (button.ButtonIndex==MouseButton.WheelUp?0.86f:1.15f),1f,20f);
                UpdateCamera();
            }
        }
        else if(evt is InputEventMouseMotion move && _dragging)
        {
            _yaw+=move.Relative.X*0.004f;
            _pitch=Mathf.Clamp(_pitch-move.Relative.Y*0.004f,-1.1f,1.1f);
            UpdateCamera();
        }
        else if(evt is InputEventKey key&&key.Pressed&&!key.Echo)
        {
            if(key.Keycode==Key.Escape)GetTree().Quit();
            else if(key.Keycode==Key.Y && _hairCandidateMaterial!=null)
            {
                OriginalHairTintMaskVisible=!OriginalHairTintMaskVisible;
                _hairCandidateMaterial.SetShaderParameter("tint_debug_view",
                    OriginalHairTintMaskVisible?1:0);
            }
            else if(key.Keycode==Key.T && _hairInstance!=null)
            {
                OriginalHairDdsVisible=!OriginalHairDdsVisible;
                _hairInstance.MaterialOverride=OriginalHairDdsVisible
                    ?_hairCandidateMaterial:_hairNeutralMaterial;
            }
        }
    }
}
