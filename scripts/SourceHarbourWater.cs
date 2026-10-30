using System;
using Godot;

namespace EsoTest;

/// <summary>
/// Visual-only harbour water experiment. The elevation is the median of a
/// coherent cluster of independently source-placed floating boats, NOT a
/// decoded water level. The simple animated shader is not ESO's water shader.
/// No position, level, shoreline mask or texture is presented as verified ESO
/// water data. The mesh is bounded to the expanded 8x8 terrain preview.
/// </summary>
public static class SourceHarbourWater
{
    public static Node3D Create(float sourceBoatElevation,
        float terrainLowestHeight,int anchorCount)
    {
        if(!float.IsFinite(sourceBoatElevation)||
           !float.IsFinite(terrainLowestHeight)||anchorCount<3)
            throw new ArgumentOutOfRangeException(
                nameof(sourceBoatElevation),"Insufficient source boat elevation evidence");

        var root=new Node3D{Name="HarbourWaterCandidate"};
        var shader=new Shader
        {
            Code=@"shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled;
uniform vec4 ocean_tint : source_color = vec4(0.045,0.235,0.31,0.83);
void fragment() {
    vec2 p = UV * 150.0;
    float broad = sin(p.x * 0.31 + TIME * 0.63) *
                  sin(p.y * 0.27 - TIME * 0.46);
    float fine = sin(p.x * 0.87 + p.y * 0.39 - TIME * 1.19) *
                 sin(p.y * 0.83 - p.x * 0.21 + TIME * 0.81);
    float shimmer = broad * 0.022 + fine * 0.010;
    ALBEDO = ocean_tint.rgb + vec3(shimmer);
    ROUGHNESS = 0.18;
    METALLIC = 0.02;
    ALPHA = ocean_tint.a;
}"
        };
        var material=new ShaderMaterial{Shader=shader};
        var mesh=new PlaneMesh{Size=new Vector2(900f,900f)};
        root.AddChild(new MeshInstance3D
        {
            Name="CandidateSeaSurface",
            Mesh=mesh,
            MaterialOverride=material,
            Position=new Vector3(0f,sourceBoatElevation-terrainLowestHeight,0f),
            CastShadow=GeometryInstance3D.ShadowCastingSetting.Off
        });
        GD.Print($"HARBOUR WATER EXPERIMENT: boat-origin median={sourceBoatElevation:F3}, "+
            $"boats={anchorCount}, local Y={sourceBoatElevation-terrainLowestHeight:F3}. "+
            "Water level and shader are provisional; no ESO ocean-level record verified.");
        return root;
    }
}
