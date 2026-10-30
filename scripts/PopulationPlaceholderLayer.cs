using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace EsoTest;

/// <summary>
/// Experimental human-sized stand-ins, NOT decoded ESO character meshes or
/// authoritative character coordinates. Local visual density only.
/// </summary>
public partial class PopulationPlaceholderLayer : Node3D
{
    private SourcePropLayer _props=null!;
    private PopulationTerrainSampler _surface=null!;
    private readonly List<PopulationSpawnPlanner.Candidate> _town=new();
    private Node3D? _models;
    private float _density=1f;
    private bool _shown=true;
    private int _placed,_requested;
    public int PlacedCount=>_placed;
    public int SourceNpcCount=>_town.Count;
    public Vector3? SuggestedCameraEye {get;private set;}
    public Vector3? SuggestedCameraTarget {get;private set;}
    public event Action<string>? Changed;

    public void Setup(EsoArchive archive,OodleDecoder decoder,TerrainBundle bundle,
        SourcePropLayer props,float lowestHeight,int tileX,int tileZ)
    {
        _props=props;
        _surface=new PopulationTerrainSampler(archive,decoder,bundle,lowestHeight,
            tileX,tileZ,props.HarbourWaterCandidate);
        string file=ProjectSettings.GlobalizePath("res://.research/auridon-population.json");
        if(!File.Exists(file))throw new FileNotFoundException(
            "Population preview data missing; run node tools/PopulationPreview/export.js",file);
        using var doc=JsonDocument.Parse(File.ReadAllText(file));
        if(doc.RootElement.GetProperty("schemaVersion").GetInt32()!=1)
            throw new InvalidDataException("Unknown Auridon population preview format");
        foreach(var person in doc.RootElement.GetProperty("people").EnumerateArray())
        {
            if(!person.TryGetProperty("town",out var town)||town.ValueKind!=JsonValueKind.Object)
                continue;
            string name=person.GetProperty("name").GetString()??"Unknown";
            string id=person.GetProperty("id").GetString()??name;
            string race=person.TryGetProperty("race",out var r)&&r.ValueKind==JsonValueKind.String
                ? r.GetString()??"" : "";
            float u=town.GetProperty("x").GetSingle(),v=town.GetProperty("y").GetSingle();
            // Provisional town-map-to-source-patch alignment. These are NOT ESO world coords.
            float x=props.Focus.X+(u-365000f)*0.00070f;
            float z=props.Focus.Z+(v-660000f)*0.00070f;
            string place=town.GetProperty("place").GetString()??"Vulkhel Guard";
            _town.Add(new PopulationSpawnPlanner.Candidate(id,name,race,place,x,z));
        }
        GD.Print($"AURIDON POPULATION: {_town.Count} source-linked Vulkhel NPCs, "+
            $"{_surface.SourceTiles} original ground tiles; provisional map anchors.");
    }

    public override void _Ready()
    {
        _props.GeometryPrepared+=Rebuild;
        Rebuild();
    }

    public void SetDensity(float multiplier)
    {
        _density=Mathf.Clamp(multiplier,0.25f,2f);
        if(IsNodeReady())Rebuild();
    }
    public void SetShown(bool shown)
    {
        _shown=shown;
        if(_models!=null)_models.Visible=shown;
        Notify();
    }
    public bool Shown=>_shown;
    public float Density=>_density;

    private void Rebuild()
    {
        if(!IsInsideTree())return;
        if(!_props.HasPreparedGeometry)
        {
            _placed=0;
            _requested=_town.Count;
            Notify();
            GD.Print("POPULATION WAITING for source mesh raycasts");
            return;
        }
        var count=(int)MathF.Round(_town.Count*_density);
        var source=_town.OrderBy(x=>PopulationSpawnPlanner.StableHash(x.Id)).ToArray();
        var candidates=new List<PopulationSpawnPlanner.Candidate>();
        candidates.AddRange(source.Take(count));
        if(count>source.Length)
        {
            for(int i=0;i<count-source.Length;i++)
            {
                // Density stress-test citizens, explicitly not source-backed named NPCs.
                var seed=PopulationSpawnPlanner.StableHash("ambient:"+i);
                var rand=new Random(seed);
                float x=_props.Focus.X+((float)rand.NextDouble()-0.5f)*300f;
                float z=_props.Focus.Z+((float)rand.NextDouble()-0.5f)*300f;
                candidates.Add(new PopulationSpawnPlanner.Candidate("ambient:"+i,
                    "Ambient Citizen",string.Empty,"Generated density test",x,z));
            }
        }
        var (spawns,stats)=PopulationSpawnPlanner.Generate(candidates,
            _surface.GroundAt,_props.UnresolvedFootprints,_props.Raycaster);
        _requested=stats.Requested;_placed=stats.Placed;
        ChooseCameraAmongCrowd(spawns);
        BuildModels(spawns);
        GD.Print($"POPULATION PREVIEW placed={stats.Placed}/{stats.Requested}, "+
            $"rejected={stats.Rejected}, shifted={stats.Shifted}, "+
            $"obstacleAttempts={stats.BlockedByScenery}, terrainAttempts={stats.BlockedByTerrain}, "+
            $"crowdAttempts={stats.BlockedByCrowd}, unresolvedProps={_props.UnresolvedFootprints.Count}, "+
            $"sourceTriangles={_props.Raycaster.TriangleCount}, sourceMeshesReady={_props.HasPreparedGeometry}");
        Notify();
    }
    private void Notify()=>Changed?.Invoke(
        $"NPC stand-ins: {_placed}/{_requested} | source town NPCs: {_town.Count} | "+
        $"density: {_density:0.##}x | "+(_props.HasPreparedGeometry?"vertical source-mesh raycasts verified":"waiting for source meshes")+
        " | positions approximate [N toggle, J density]");

    private void ChooseCameraAmongCrowd(IReadOnlyList<PopulationSpawnPlanner.Placement> points)
    {
        SuggestedCameraEye=null;SuggestedCameraTarget=null;
        if(points.Count<2)return;
        var visible=new List<(int Count, float Distance, Vector3 Eye, Vector3 Target)>();
        foreach(var candidate in points)
        {
            var eye=candidate.Position+new Vector3(0f,1.78f,0f);
            if(!_props.Raycaster.OpenSkyToTerrain(candidate.Position.X,
                candidate.Position.Z,candidate.Position.Y,0.8f))continue;
            int crowd=0;
            foreach(var other in points)
            {
                float dx=other.Position.X-candidate.Position.X;
                float dz=other.Position.Z-candidate.Position.Z;
                if(dx*dx+dz*dz<30f*30f)crowd++;
            }
            foreach(var other in points)
            {
                if(other.Id==candidate.Id)continue;
                var dist=candidate.Position.DistanceTo(other.Position);
                if(dist<8f||dist>22f)continue;
                var target=other.Position+new Vector3(0f,1.28f,0f);
                if(_props.Raycaster.Intersects(eye,target))continue;
                // A clear prop ray can still cross a terrain ridge. Reject it.
                bool clear=true;
                for(int k=1;k<9;k++)
                {
                    float progress=k/9f;
                    var sample=eye.Lerp(target,progress);
                    var ground=_surface.GroundAt(sample.X,sample.Z);
                    if(ground is null||ground.Value>sample.Y-0.6f)
                    {clear=false;break;}
                }
                if(!clear)continue;
                visible.Add((crowd,dist,eye,target));
                break;
            }
        }
        if(visible.Count==0)return;
        // Prefer busy, source-clear squares with an unobstructed 8-22m view.
        var best=visible.OrderByDescending(v=>v.Count)
            .ThenBy(v=>MathF.Abs(v.Distance-13f)).First();
        SuggestedCameraEye=best.Eye;
        SuggestedCameraTarget=best.Target;
    }
    private void BuildModels(IReadOnlyList<PopulationSpawnPlanner.Placement> points)
    {
        if(_models!=null){_models.QueueFree();_models=null;}
        var root=new Node3D{Name="ApproximateNPCStandIns",Visible=_shown};
        AddChild(root);_models=root;
        var body=new CylinderMesh {TopRadius=0.27f,BottomRadius=0.31f,
            Height=0.88f,RadialSegments=7};
        var head=new SphereMesh {Radius=0.21f,Height=0.42f,RadialSegments=8,Rings=4};
        var leg=new CylinderMesh {TopRadius=0.10f,BottomRadius=0.11f,
            Height=0.68f,RadialSegments=6};
        AddBatch(root,"ClothedBodies",body,points,new Vector3(0f,1.10f,0f),true,0);
        AddBatch(root,"Heads",head,points,new Vector3(0f,1.75f,0f),false,1);
        AddBatch(root,"LeftLegs",leg,points,new Vector3(-0.15f,0.36f,0f),true,2);
        AddBatch(root,"RightLegs",leg,points,new Vector3(0.15f,0.36f,0f),true,2);
    }

    private static void AddBatch(Node3D root,string name,Mesh mesh,
        IReadOnlyList<PopulationSpawnPlanner.Placement> spawns,
        Vector3 offset,bool coloured,int part)
    {
        var mm=new MultiMesh {Mesh=mesh,TransformFormat=MultiMesh.TransformFormatEnum.Transform3D,
            UseColors=coloured,InstanceCount=spawns.Count};
        for(int i=0;i<spawns.Count;i++)
        {
            var spawn=spawns[i];
            float scale=0.92f+(PopulationSpawnPlanner.StableHash(spawn.Id)%19)*0.009f;
            var basis=Basis.FromEuler(new Vector3(0f,spawn.Facing,0f)).Scaled(
                new Vector3(scale,scale,scale));
            var pos=spawn.Position+basis*offset;
            mm.SetInstanceTransform(i,new Transform3D(basis,pos));
            if(coloured)
            {
                int shade=PopulationSpawnPlanner.StableHash(spawn.Id+":"+part);
                var color=part==2?new Color(0.20f,0.23f,0.29f):
                    Color.FromHsv((shade%360)/360f,0.48f,0.72f);
                mm.SetInstanceColor(i,color);
            }
        }
        var material=new StandardMaterial3D {
            ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor=part==1?new Color(0.85f,0.73f,0.59f):Colors.White,
            VertexColorUseAsAlbedo=coloured
        };
        root.AddChild(new MultiMeshInstance3D {
            Name=name,Multimesh=mm,MaterialOverride=material
        });
    }
    public override void _ExitTree()
    {
        if(_props!=null)_props.GeometryPrepared-=Rebuild;
    }
}
