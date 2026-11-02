using System;
using System.IO;
using Godot;

namespace EsoTest;

/// <summary>
/// Source-only Godot smoke test of original DDS candidate material decoding
/// across additional creature families. Not a new UI or runtime material proof.
/// All graphics are loaded directly from the installed ESO archive.
/// </summary>
public partial class EsoOriginalCreatureTextureCoverageSmoke:Node
{
    private const int ExpectedMappings=59;
    private static readonly uint[] Samples={
        0x00082EABu, // Giant Spider
        0x0008739Bu, // Werewolf
        0x0008868Fu, // Troll
        0x0009EFA7u, // Mantikora
        0x0009FD53u, // Fell Sentry
        0x000A157Fu, // Thorn Gecko
        0x000A1A9Du, // Scorpion Spider Daedra
        0x000C6931u, // Dreugh
        0x000C5C6Bu, // Giant Wasp
        0x0025D5B1u, // Large Senche
        0x0029B137u, // Guar mount
        0x003229F9u  // Moonstone bear cub
    };
    public override void _Ready()
    {
        try
        {
            string root=ProjectSettings.GlobalizePath("res://");
            string manifest=System.Environment.GetEnvironmentVariable("ESO_MNF")??
                @"C:\ESO\The Elder Scrolls Online\depot\eso.mnf";
            string oodle=Path.Combine(root,".research","ooz.dll");
            string candidates=Path.Combine(root,"data",
                "eso_creature_texture_candidates.tsv");
            string local=Path.Combine(root,".research","npc-rigs",
                "creature-texture-candidates.tsv");
            using var source=new EsoOriginalCreatureTextures(
                manifest,oodle,candidates,local);
            if(source.MappingCount!=ExpectedMappings)
                throw new InvalidDataException(
                    $"Original creature candidate index has {source.MappingCount} entries");
            int tested=0;
            foreach(uint id in Samples)
            {
                var entry=source.SourceMapping(id)??
                    throw new InvalidDataException($"Original source creature 0x{id:X8} lacks candidate");
                if(entry.Evidence!="archive-triplet-authoring-provisional")
                    throw new InvalidDataException($"Original source creature 0x{id:X8} was incorrectly verified");
                if(entry.Diffuse==0||entry.Normal==0||entry.Specular==0)
                    throw new InvalidDataException($"Original source creature 0x{id:X8} lacks complete source DDS triplet");
                if(source.LoadMaterial(id) is not ShaderMaterial mat)
                    throw new InvalidDataException($"Original source creature 0x{id:X8} has no diagnostic DDS shader");
                if(mat.GetShaderParameter("original_diffuse").AsGodotObject() is not Texture2D d||
                   mat.GetShaderParameter("original_normal").AsGodotObject() is not Texture2D n||
                   mat.GetShaderParameter("original_specular").AsGodotObject() is not Texture2D s||
                   !mat.GetShaderParameter("has_original_normal").AsBool())
                    throw new InvalidDataException($"Original source creature 0x{id:X8} lacks decoded DDS channels");
                if(d.GetWidth()!=n.GetWidth()||d.GetHeight()!=n.GetHeight()||
                   d.GetWidth()!=s.GetWidth()||d.GetHeight()!=s.GetHeight())
                    throw new InvalidDataException($"Original source creature 0x{id:X8} DDS channels mismatch dimensions");
                tested++;
            }
            if(tested!=Samples.Length)
                throw new InvalidDataException("Original source DDS sample coverage incomplete");
            GD.Print($"PASS {tested} expanded original ESO creature material candidates: "+
                "native archive diffuse/packed-normal/specular DDS decoded, shader-bound, "+
                "identical texture dimensions, all associations explicitly provisional.");
            GetTree().Quit(0);
        }
        catch(Exception e)
        {
            GD.PushError("Original ESO texture coverage failed closed: "+e);
            GetTree().Quit(1);
        }
    }
}
