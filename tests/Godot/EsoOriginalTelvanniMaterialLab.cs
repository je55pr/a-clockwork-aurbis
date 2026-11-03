using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using Godot;
using Process=System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// Reproducible original ESO source-only Telvanni Light material experiment.
/// The asset and material names are genuine Granny data. A/B DDS quartets
/// remain unverified archive-neighbor hypotheses, not runtime bindings.
/// Draws only genuine source meshes and textures, never generated substitutes.
/// </summary>
public partial class EsoOriginalTelvanniMaterialLab:Node3D
{
    private sealed record Item(uint Id,string Mesh,int Vertices,
        uint Diffuse,uint Normal,uint Specular,uint Tint,string SourceArt);
    private static readonly Item[] Parts=
    {
        new(0x001086BD,"TelvanniLight_Helmet_B",510,
            0x001086C5u,0x001086C7u,0x001086C9u,0x001086CBu,"TelvanniLight_B_U"),
        new(0x001086D9,"TelvanniLight_Helmet_A",680,
            0x001086CDu,0x001086CFu,0x001086D1u,0x001086D3u,"TelvanniLight_A_U"),
        new(0x001086B5,"TelvanniLight_Belt_A",163,
            0x001086CDu,0x001086CFu,0x001086D1u,0x001086D3u,"TelvanniLight_A_U"),
        new(0x00108795,"TelvanniLight_Shoulders_A",784,
            0x001086CDu,0x001086CFu,0x001086D1u,0x001086D3u,"TelvanniLight_A_U")
    };
    [Export]public bool SelfTest{get;set;}
    public int VerifiedOriginalMeshes{get;private set;}
    public int SourceVertices{get;private set;}
    public int TintDebugChannel{get;private set;}=-1;
    public int OriginalMaterialBindings{get;private set;}
    public Color OriginalSourcePrimaryTint{get;private set;}
    public Color OriginalSourceSecondaryTint{get;private set;}
    public Color OriginalSourceAccentTint{get;private set;}
    private readonly List<ShaderMaterial> _materials=new();
    private Camera3D? _camera;
    private bool _dragging;
    private float _yaw=0.28f,_pitch=0.08f,_zoom=3.05f;
    private static string Root=>ProjectSettings.GlobalizePath("res://");
    private static string Cache=>Path.Combine(Root,".research","npc-rigs");
    private static string Helper=>Path.Combine(Root,"tools","ModelGeometry","bin",
        "Debug","net8.0","ModelGeometry.dll");
    private static string Granny=>System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
        @"C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll";
    private static string Manifest=>System.Environment.GetEnvironmentVariable("ESO_MNF")??
        @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
    private static string Oodle=>Path.Combine(Root,".research","ooz.dll");
    private static string AssetPath(Item source)=>Path.Combine(Cache,
        "telvanni-original-"+source.Id.ToString("X8",CultureInfo.InvariantCulture)+".esm");
    private static string RunOriginal(params string[] args)
    {
        foreach(string dep in new[]{Helper,Manifest,Granny,Oodle})
            if(!File.Exists(dep))
                throw new FileNotFoundException("Original ESO asset dependency missing: "+dep);
        using var proc=new Process();
        proc.StartInfo=new ProcessStartInfo("dotnet")
        {
            WorkingDirectory=Root,UseShellExecute=false,
            CreateNoWindow=true,RedirectStandardOutput=true,
            RedirectStandardError=true
        };
        proc.StartInfo.ArgumentList.Add(Helper);
        foreach(string arg in args)proc.StartInfo.ArgumentList.Add(arg);
        if(!proc.Start())throw new IOException("ESO source model helper did not start");
        var output=proc.StandardOutput.ReadToEndAsync();
        var error=proc.StandardError.ReadToEndAsync();
        if(!proc.WaitForExit(45000))
        {
            proc.Kill(entireProcessTree:true);
            throw new TimeoutException("Source model extraction timed out");
        }
        string stdout=output.GetAwaiter().GetResult();
        string stderr=error.GetAwaiter().GetResult();
        if(proc.ExitCode!=0)
            throw new InvalidDataException("ESO original Granny probe failed: "+
                stderr[..Math.Min(450,stderr.Length)]);
        return stdout;
    }
    private static Color ReadOriginalDye(string material,string field)
    {
        string token=field+" [kind=10,size=4,count=4] value=";
        int start=material.IndexOf(token,StringComparison.Ordinal);
        if(start<0)
            throw new InvalidDataException("Original Telvanni authored dye missing: "+field);
        start+=token.Length;
        int end=material.IndexOfAny(new[]{'\r','\n'},start);
        if(end<0)end=material.Length;
        string[] components=material[start..end].Trim().Split(',');
        if(components.Length!=4)
            throw new InvalidDataException("Original Telvanni authored dye not RGBA");
        var numbers=new float[4];
        for(int i=0;i<4;i++)
        {
            if(!float.TryParse(components[i],NumberStyles.Float,
                CultureInfo.InvariantCulture,out numbers[i])||
               !float.IsFinite(numbers[i])||numbers[i]<0f||numbers[i]>1f)
                throw new InvalidDataException("Original authored dye component invalid");
        }
        return new Color(numbers[0],numbers[1],numbers[2],numbers[3]);
    }

    private static void Prepare(Item item)
    {
        Directory.CreateDirectory(Cache);
        if(!File.Exists(AssetPath(item)))
            RunOriginal(Granny,item.Id.ToString("X8",CultureInfo.InvariantCulture),
                AssetPath(item),Manifest,Oodle);
    }
    public override void _Ready()
    {
        try
        {
            string index=Path.Combine(Root,"data","eso_telvanni_light_material_candidates.tsv");
            using var assets=new EsoOriginalCreatureTextures(Manifest,Oodle,index);
            if(assets.MappingCount!=Parts.Length||
               assets.SourceMapping(0x001086B1u)!=null)
                throw new InvalidDataException(
                    "Telvanni atlas inventory must exclude rejected original tassets UV assignment");
            for(int i=0;i<Parts.Length;i++)
            {
                Item item=Parts[i];
                var map=assets.SourceMapping(item.Id);
                if(map==null || map.Diffuse!=item.Diffuse ||
                   map.Normal!=item.Normal||map.Specular!=item.Specular||
                   map.Tint!=item.Tint||
                   map.Evidence!="archive-triplet-authoring-provisional")
                    throw new InvalidDataException(
                        "Telvanni source artwork quartet association changed: "+item.Mesh);
                // Verify ORIGINAL asset authoring paths; the DDS ID association
                // is still provisional even when paths and UV islands agree.
                string materialReport=RunOriginal("--audit-character-material",Granny,
                    item.Id.ToString("X8",CultureInfo.InvariantCulture),Manifest,Oodle);
                foreach(string suffix in new[]{"_D.tga","_N.tga","_S.tga","_T.tga"})
                    if(!materialReport.Contains(item.SourceArt+suffix,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "Granny original model material source path disagrees: "+item.Mesh+suffix);
                if(!materialReport.Contains("f4DiffuseTintPrimary",StringComparison.Ordinal)||
                   !materialReport.Contains("f4DiffuseTintSecondary",StringComparison.Ordinal)||
                   !materialReport.Contains("f4DiffuseTintAccent",StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "Original Telvanni three authored tint colours missing");
                Color primary=ReadOriginalDye(materialReport,"f4DiffuseTintPrimary");
                Color secondary=ReadOriginalDye(materialReport,"f4DiffuseTintSecondary");
                Color accent=ReadOriginalDye(materialReport,"f4DiffuseTintAccent");
                if(i==0)
                {
                    OriginalSourcePrimaryTint=primary;
                    OriginalSourceSecondaryTint=secondary;
                    OriginalSourceAccentTint=accent;
                }
                else if(!primary.IsEqualApprox(OriginalSourcePrimaryTint)||
                        !secondary.IsEqualApprox(OriginalSourceSecondaryTint)||
                        !accent.IsEqualApprox(OriginalSourceAccentTint))
                    throw new InvalidDataException(
                        "ESO source Telvanni A/B original dye colours differ unexpectedly");
                Prepare(item);
                var mesh=EsoHumanoidVisualLayer.LoadMesh(
                    AssetPath(item),item.Mesh,rig:null,enforceHumanoidBounds:false);
                if(mesh.GetSurfaceCount()!=1||mesh.SurfaceGetArrayLen(0)!=item.Vertices)
                    throw new InvalidDataException(
                        "Original Telvanni geometry changed: "+item.Mesh);
                var shader=assets.LoadOriginalClothingDiagnostic(item.Id)??
                    throw new InvalidDataException(
                        "Original Telvanni four-channel source DDS missing: "+item.Mesh);
                foreach(string channel in new[]{
                    "original_diffuse","original_normal","original_specular","original_tint_mask"})
                {
                    if(shader.GetShaderParameter(channel).AsGodotObject() is not Texture2D tex||
                       tex.GetWidth()!=576||tex.GetHeight()!=256)
                        throw new InvalidDataException(
                            "Original 576x256 Telvanni "+channel+" source mismatched");
                }
                _materials.Add(shader);
                Aabb bounds=mesh.GetAabb();
                float center=(i-(Parts.Length-1)*0.5f)*0.95f;
                var instance=new MeshInstance3D
                {
                    Name="Source_"+item.Mesh,
                    Mesh=mesh,
                    MaterialOverride=shader,
                    Position=new Vector3(center-bounds.GetCenter().X,
                        -bounds.Position.Y,
                        -bounds.GetCenter().Z)
                };
                AddChild(instance);
                VerifiedOriginalMeshes++;
                SourceVertices+=item.Vertices;
                OriginalMaterialBindings+=4;
                GD.Print($"ORIGINAL TELVANNI MATERIAL {item.Mesh} {item.Vertices} vertices "+
                    $"native AABB={bounds.Size}, source paths D/N/S/T, candidate archived DDS");
            }
            if(VerifiedOriginalMeshes!=4||SourceVertices!=2137||
               OriginalMaterialBindings!=16||
               !OriginalSourcePrimaryTint.IsEqualApprox(new Color(128/255f,120/255f,112/255f,1))||
               !OriginalSourceSecondaryTint.IsEqualApprox(new Color(89/255f,89/255f,89/255f,1))||
               !OriginalSourceAccentTint.IsEqualApprox(new Color(61/255f,71/255f,76/255f,1)))
                throw new InvalidDataException("Original Telvanni source mesh count changed");
            GD.Print("PASS 4 real ESO Telvanni Light meshes / 2137 original vertices / "+
                "16 candidate DDS channel applications / source-authored A/B material path "+
                "verification / original three dye colours preserved as source metadata only.");
            if(SelfTest){GetTree().Quit(0);return;}
            AddChild(new WorldEnvironment
            {
                Environment=new Godot.Environment
                {
                    BackgroundMode=Godot.Environment.BGMode.Color,
                    BackgroundColor=new Color("#20262d"),
                    AmbientLightSource=Godot.Environment.AmbientSource.Color,
                    AmbientLightColor=new Color("#d9e0e9"),
                    AmbientLightEnergy=0.95f
                }
            });
            AddChild(new DirectionalLight3D
            {
                RotationDegrees=new Vector3(-42,-28,0),
                LightEnergy=1.45f
            });
            _camera=new Camera3D{Name="SourceTelvanniCamera",Current=true,Fov=48f};
            AddChild(_camera);
            AddChild(new OmniLight3D
            {
                Position=new Vector3(1.2f,2.0f,2.5f),
                OmniRange=8f,LightEnergy=0.5f
            });
            var hud=new CanvasLayer();
            AddChild(hud);
            var label=new Label
            {
                Name="SourceTelvanniInfo",
                Position=new Vector2(14,14),
                Text="ORIGINAL ESO TELVANNI LIGHT\n"+
                    "Left to right: B helmet / A helmet / A belt / A shoulders\n"+
                    "Original source meshes + provisional D/N/S/T images\n"+
                    "Source primary/secondary/accent dye colours, NOT applied\n"+
                    "M: inspect tint R/G/B/A | Drag: orbit | Wheel: zoom"
            };
            label.AddThemeFontSizeOverride("font_size",16);
            hud.AddChild(label);
            UpdateCamera();
        }
        catch(Exception e)
        {
            GD.PushError("Original Telvanni material lab failed: "+e);
            GetTree().Quit(1);
        }
    }
    public void CycleTintChannel()
    {
        TintDebugChannel=TintDebugChannel==3?-1:TintDebugChannel+1;
        foreach(var material in _materials)
            material.SetShaderParameter("source_tint_debug_channel",TintDebugChannel);
    }
    private void UpdateCamera()
    {
        if(_camera==null)return;
        var target=new Vector3(0,0.28f,0);
        var direction=new Vector3(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),
            Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch));
        _camera.Position=target+direction*_zoom;
        _camera.LookAt(target,Vector3.Up);
    }
    public override void _Input(InputEvent evt)
    {
        if(SelfTest)return;
        if(evt is InputEventMouseButton b)
        {
            if(b.ButtonIndex==MouseButton.Left)_dragging=b.Pressed;
            if(b.Pressed&&b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                _zoom=Mathf.Clamp(_zoom*(b.ButtonIndex==MouseButton.WheelUp?0.86f:1.16f),
                    0.9f,8f);
                UpdateCamera();
            }
        }
        else if(evt is InputEventMouseMotion mouse && _dragging)
        {
            _yaw+=mouse.Relative.X*0.005f;
            _pitch=Mathf.Clamp(_pitch-mouse.Relative.Y*0.005f,-0.85f,0.85f);
            UpdateCamera();
        }
        else if(evt is InputEventKey key && key.Pressed&&!key.Echo)
        {
            if(key.Keycode==Key.M)CycleTintChannel();
            else if(key.Keycode==Key.Escape)GetTree().Quit();
        }
    }
}
