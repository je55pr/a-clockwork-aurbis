using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Godot;
using Process=System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// Minimal, noninteractive source-material render test. Shows four unanimated
/// ORIGINAL ESO Granny meshes with source DDS candidate materials for UV
/// inspection. These are NOT verified runtime actor material associations.
/// No source graphics are copied into the repository.
/// </summary>
public partial class EsoOriginalCreatureStaticTexturePreview:Node3D
{
    [Export] public bool SelfTest {get;set;}
    private sealed record Spec(string Name,uint AssetId,string OriginalMeshName);
    private static readonly Spec[] Sources={
        new("Giant Spider",0x00082EABu,"GiantSpider_B_Boss"),
        new("Werewolf",0x0008739Bu,"Werewolf_A_Basic"),
        new("Troll",0x0008868Fu,"Troll_A_Basic"),
        new("Mantikora",0x0009EFA7u,"Mantikora_B_Boss")
    };
    private static string Root=>ProjectSettings.GlobalizePath("res://");
    private static string Manifest=>System.Environment.GetEnvironmentVariable("ESO_MNF")??
        @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
    private static string Granny=>System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
        @"C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll";
    private static string Oodle=>Path.Combine(Root,".research","ooz.dll");
    private static string Helper=>Path.Combine(Root,"tools","ModelGeometry","bin",
        "Debug","net8.0","ModelGeometry.dll");
    private static string Cache=>Path.Combine(Root,".research","npc-rigs");
    private static string SourceMesh(Spec s)=>Path.Combine(Cache,
        $"static-texture-{s.AssetId:X8}.esm");

    private static void EnsureGeometry(Spec spec)
    {
        Directory.CreateDirectory(Cache);
        string output=SourceMesh(spec);
        if(File.Exists(output))return;
        foreach(string dep in new[]{Helper,Manifest,Granny,Oodle})
            if(!File.Exists(dep))throw new FileNotFoundException(
                "Missing installed native ESO source dependency: "+dep);
        using var proc=new Process();
        proc.StartInfo=new ProcessStartInfo("dotnet")
        {
            WorkingDirectory=Root,UseShellExecute=false,
            CreateNoWindow=true,RedirectStandardOutput=true,
            RedirectStandardError=true
        };
        foreach(string arg in new[]{Helper,Granny,spec.AssetId.ToString("X8",
            CultureInfo.InvariantCulture),output,Manifest,Oodle})
            proc.StartInfo.ArgumentList.Add(arg);
        if(!proc.Start())throw new IOException("Native ESO geometry decoder failed to start");
        var stdout=proc.StandardOutput.ReadToEndAsync();
        var stderr=proc.StandardError.ReadToEndAsync();
        if(!proc.WaitForExit(45000))
        {
            proc.Kill(entireProcessTree:true);
            throw new TimeoutException("Original ESO static preview decoder stalled");
        }
        string outText=stdout.GetAwaiter().GetResult();
        string error=stderr.GetAwaiter().GetResult();
        if(proc.ExitCode!=0||!File.Exists(output))
            throw new InvalidDataException(
                $"Original ESO source 0x{spec.AssetId:X8} geometry failed: "+
                error[..Math.Min(error.Length,250)]+
                outText[..Math.Min(outText.Length,100)]);
    }

    public override void _Ready()
    {
        try
        {
            string index=Path.Combine(Root,"data",
                "eso_creature_texture_candidates.tsv");
            string local=Path.Combine(Cache,"creature-texture-candidates.tsv");
            using var library=new EsoOriginalCreatureTextures(
                Manifest,Oodle,index,local);
            if(library.MappingCount!=59)
                throw new InvalidDataException("Original creature source texture index incomplete");
            AddChild(new WorldEnvironment
            {
                Environment=new Godot.Environment
                {
                    BackgroundMode=Godot.Environment.BGMode.Color,
                    BackgroundColor=new Color("#132029"),
                    AmbientLightSource=Godot.Environment.AmbientSource.Color,
                    AmbientLightColor=new Color("#d0d8db"),
                    AmbientLightEnergy=0.9f
                }
            });
            var sun=new DirectionalLight3D
            {
                RotationDegrees=new Vector3(-50,-20,0),
                LightEnergy=1.35f,
                ShadowEnabled=false
            };
            AddChild(sun);
            var fill=new OmniLight3D
            {
                Position=new Vector3(2,8,8),LightEnergy=0.7f,
                OmniRange=30f
            };
            AddChild(fill);
            int loaded=0;
            for(int i=0;i<Sources.Length;i++)
            {
                var source=Sources[i];
                EnsureGeometry(source);
                var mesh=EsoHumanoidVisualLayer.LoadMesh(SourceMesh(source),
                    source.OriginalMeshName,rig:null,enforceHumanoidBounds:false);
                if(mesh.GetSurfaceCount()!=1||mesh.SurfaceGetArrayLen(0)<100)
                    throw new InvalidDataException($"Original ESO mesh failed: {source.Name}");
                var record=library.SourceMapping(source.AssetId);
                if(record==null||record.Evidence!="archive-triplet-authoring-provisional")
                    throw new InvalidDataException($"Source authoring evidence missing: {source.Name}");
                if(library.LoadMaterial(source.AssetId) is not ShaderMaterial material ||
                   !material.GetShaderParameter("has_original_normal").AsBool())
                    throw new InvalidDataException(
                        $"Original source diffuse/packed-normal/specular shader missing: {source.Name}");
                var bound=mesh.GetAabb();
                float center=(i-(Sources.Length-1)*0.5f)*8f;
                var instance=new MeshInstance3D
                {
                    Name="OriginalSource_"+source.OriginalMeshName,
                    Mesh=mesh,MaterialOverride=material,
                    Position=new Vector3(center-bound.Position.X-bound.Size.X/2,
                        -bound.Position.Y,
                        -bound.Position.Z-bound.Size.Z/2)
                };
                AddChild(instance);
                if(!SelfTest)
                    AddChild(new Label3D
                    {
                        Text=source.Name+" / SOURCE DDS CANDIDATE",
                        Position=new Vector3(center,bound.Size.Y+0.65f,0),
                        FontSize=56,PixelSize=0.0033f
                    });
                loaded++;
                GD.Print($"ORIGINAL_SOURCE_STATIC {source.Name}: "+
                    $"{mesh.SurfaceGetArrayLen(0)} native vertices; "+
                    $"bounds={bound.Size}; source DDS shader bound");
            }
            if(loaded!=Sources.Length)
                throw new InvalidDataException("Static creature source material coverage incomplete");
            if(SelfTest)
            {
                GD.Print($"PASS {loaded} additional original ESO static creature meshes "+
                    "with native UVs and archived diffuse/normal/specular candidate materials; "+
                    "no assumed animation, no invented runtime material links.");
                GetTree().Quit(0);
                return;
            }
            var camera=new Camera3D
            {
                Position=new Vector3(4,7,26),
                Fov=55f,
                Current=true
            };
            AddChild(camera);
            camera.LookAt(new Vector3(0,2,0),Vector3.Up);
        }
        catch(Exception ex)
        {
            GD.PushError("Original ESO source texture static preview failed: "+ex);
            GetTree().Quit(1);
        }
    }
}
