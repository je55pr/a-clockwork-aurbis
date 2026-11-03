using System;
using System.IO;
using Godot;

namespace EsoTest;

/// <summary>
/// Offline source-only validation of two archival ESO hair D/N/S/T
/// material quartets. No fictional dye colours or runtime bindings.
/// The second GR2 comes from the controlled ESO selection captures.
/// </summary>
public partial class EsoOriginalCapturedHairTintSmoke : Node3D
{
    private static readonly (string File,uint MeshId,uint Diffuse,uint Normal,
        uint Specular,uint Tint,string Evidence)[] Sources=
    {
        ("eso_humanoid_hair_texture_candidates.tsv",
            0x0029A87Fu,0x0029A877u,0x0029A879u,0x0029A87Bu,
            0x0029A87Du,"archive-neighbor-provisional"),
        ("eso_original_face_hair_texture_candidates.tsv",
            0x0029B2AFu,0x0029A877u,0x0029A879u,0x0029A87Bu,
            0x0029A87Du,"archive-triplet-authoring-provisional"),
        ("eso_captured_hair_material_candidates.tsv",
            0x0002E5E3u,0x0002E5DBu,0x0002E5DDu,0x0002E5DFu,
            0x0002E5E1u,"archive-triplet-authoring-provisional")
    };

    public override void _Ready()
    {
        try
        {
            string root=ProjectSettings.GlobalizePath("res://");
            string manifest=System.Environment.GetEnvironmentVariable("ESO_MNF")??
                @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
            string decoder=Path.Combine(root,".research","ooz.dll");
            if(!File.Exists(decoder)||!File.Exists(manifest))
                throw new FileNotFoundException("Original installed ESO archives unavailable");
            foreach(var source in Sources)
            {
                using var textures=new EsoOriginalCreatureTextures(
                    manifest,decoder,Path.Combine(root,"data",source.File));
                var mapping=textures.SourceMapping(source.MeshId)??
                    throw new InvalidDataException("Missing source hair candidate mapping");
                if(textures.MappingCount!=1||mapping.Diffuse!=source.Diffuse||
                   mapping.Normal!=source.Normal||
                   mapping.Specular!=source.Specular||
                   mapping.Tint!=source.Tint||
                   mapping.Evidence!=source.Evidence)
                    throw new InvalidDataException(
                        "ESO original source hair quartet provenance has changed");
                var material=textures.LoadAlphaCutoutMaterial(source.MeshId)??
                    throw new InvalidDataException(
                        "Original hair four-map DDS decoding failed: "+
                        source.MeshId.ToString("X8"));
                if(material.Shader==null||
                   !material.Shader.Code.Contains("tint_debug_view")||
                   !material.Shader.Code.Contains("ALPHA_SCISSOR_THRESHOLD")||
                   !material.GetShaderParameter("has_original_tint").AsBool())
                    throw new InvalidDataException(
                        "Original hair alpha and tint diagnostic shader are missing");
                foreach(string channel in new[]{
                    "original_diffuse","original_normal","original_specular",
                    "original_tint_mask"})
                {
                    if(material.GetShaderParameter(channel).AsGodotObject()
                        is not Texture2D texture||
                       texture.GetWidth()!=576||texture.GetHeight()!=256)
                        throw new InvalidDataException(
                            $"Original hair {source.MeshId:X8} lacks 576x256 {channel}");
                }
                if(material.GetShaderParameter("tint_debug_view").AsInt32()!=0)
                    throw new InvalidDataException(
                        "Original hair materials must not invent dye by default");
                // Observe the ORIGINAL decoded red mask, never derive a dye
                // colour. Its alpha differs across original source quartets.
                var originalTint=(Texture2D)material
                    .GetShaderParameter("original_tint_mask").AsGodotObject();
                using var mask=originalTint.GetImage();
                mask.Convert(Image.Format.Rgba8);
                byte[] pixels=mask.GetData();
                int redMin=255,redMax=0,greenMax=0,blueMax=0;
                int sampleCount=0;
                long redSum=0;
                for(int offset=0;offset<pixels.Length;offset+=4*11)
                {
                    byte red=pixels[offset],green=pixels[offset+1],
                        blue=pixels[offset+2];
                    redMin=Math.Min(redMin,red);
                    redMax=Math.Max(redMax,red);
                    greenMax=Math.Max(greenMax,green);
                    blueMax=Math.Max(blueMax,blue);
                    redSum+=red;
                    sampleCount++;
                }
                if(redMin>16||redMax<238||greenMax>24||blueMax>24||
                   sampleCount<1000)
                    throw new InvalidDataException(
                        "Original hair source tint is not a predominantly red mask");
                material.SetShaderParameter("tint_debug_view",1);
                if(material.GetShaderParameter("tint_debug_view").AsInt32()!=1)
                    throw new InvalidDataException("Source tint diagnostic toggle broken");
                material.SetShaderParameter("tint_debug_view",0);
                GD.Print($"PASS original ESO hair 0x{source.MeshId:X8}: genuine "+
                    "576x256 DXT5 diffuse/packed-normal/specular and 32-bit BGRA "+
                    $"red-channel tint candidate; sampled red {redMin}..{redMax}, "+
                    $"mean {redSum/(255.0*sampleCount):F3}; no inferred dye.");
            }
            GetTree().Quit(0);
        }
        catch(Exception ex)
        {
            GD.PushError("Original ESO hair quartet smoke failed closed: "+ex);
            GetTree().Quit(1);
        }
    }
}
