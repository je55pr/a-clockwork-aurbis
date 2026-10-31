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
    private readonly HashSet<string> _dockNpcIds=new();
    private readonly Dictionary<string,JsonElement> _sourcePeople=new();
    private NpcHoverInspector? _hoverInspector;
    private Func<bool>? _hoverAllowed;
    public void SetHoverGate(Func<bool> allowed) => _hoverAllowed=allowed;
    public void SetFirstPersonAim(bool enabled) => _hoverInspector?.SetFirstPersonAim(enabled);
    private bool _firstPersonAim;
    public void ConfigureFirstPersonAim(bool enabled) { _firstPersonAim=enabled; SetFirstPersonAim(enabled); }
    private readonly List<Vector2> _dockLandings=new();
    private Node3D? _models;
    private readonly List<PopulationSpawnPlanner.Placement> _visibleSpawns=new();
    private float _density=1f;
    private bool _shown=true;
    private int _placed,_requested;
    public int PlacedCount=>_placed;
    public int SourceNpcCount=>_town.Count;
    public Vector3? SuggestedCameraEye {get;private set;}
    public Vector3? SuggestedCameraTarget {get;private set;}
    public Vector3? SuggestedDockCameraEye {get;private set;}
    public Vector3? SuggestedDockCameraTarget {get;private set;}
    public Vector3? SuggestedGuildCameraEye {get;private set;}
    public Vector3? SuggestedGuildCameraTarget {get;private set;}
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
            var clonedPerson=person.Clone();
            string id=person.GetProperty("id").GetString()??name;
            _sourcePeople[id]=clonedPerson;
            string race=person.TryGetProperty("race",out var r)&&r.ValueKind==JsonValueKind.String
                ? r.GetString()??"" : "";
            float u=town.GetProperty("x").GetSingle(),v=town.GetProperty("y").GetSingle();
            // Provisional town-map-to-source-patch alignment. These are NOT ESO world coords.
            float x=props.Focus.X+(u-365000f)*0.00070f;
            float z=props.Focus.Z+(v-660000f)*0.00070f;
            string place=town.GetProperty("place").GetString()??"Vulkhel Guard";
            string kind=town.TryGetProperty("kind",out var sourceKind)
                ? sourceKind.GetString()??"map_estimate" : "map_estimate";
            float maxDisplacement=kind=="estimated_city_district"?70f:55f;
            _town.Add(new PopulationSpawnPlanner.Candidate(
                id,name,race,place,x,z,maxDisplacement,kind));
            if(place.Contains("Vulkhel Guard Docks",StringComparison.OrdinalIgnoreCase))
                _dockNpcIds.Add(id);
        }
        GD.Print($"AURIDON POPULATION: {_town.Count} source-linked Vulkhel NPCs, "+
            $"{_surface.SourceTiles} original ground tiles; provisional map anchors.");
    }

    public override void _Ready()
    {
        _props.GeometryPrepared+=Rebuild;
        _hoverInspector=new NpcHoverInspector {Name="NpcCursorInspector"};
        _hoverInspector.Setup(_props.Raycaster,
            ()=>_shown&&(_hoverAllowed?.Invoke()??true));
        _hoverInspector.SetFirstPersonAim(_firstPersonAim);
        AddChild(_hoverInspector);
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
        _hoverInspector?.SetEnabled(shown);
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
        if(_dockNpcIds.Count>0 && _dockLandings.Count==0)PrepareDockShoreLandings();
        var count=(int)MathF.Round(_town.Count*_density);
        var source=_town.Select(c=>
        {
            if(_dockNpcIds.Contains(c.Id)&&_dockLandings.Count>0)
            {
                int site=PopulationSpawnPlanner.StableHash(c.Id)%_dockLandings.Count;
                var point=_dockLandings[site];
                return c with {AnchorX=point.X,AnchorZ=point.Y,
                    MaxDisplacement=55f,AnchorKind="source_dock_fixture"};
            }
            if(_props.TrySourceLandmark(c.Place,out var fixture))
                return c with {AnchorX=fixture.X,AnchorZ=fixture.Y,
                    MaxDisplacement=55f,AnchorKind="source_named_fixture"};
            // Other named places retain the existing approximate map point.
            // The planner may not relocate them across town to make up numbers.
            return c;
        }).OrderBy(c=>PopulationSpawnPlanner.StableHash(c.Id)).ToArray();
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
        AuditPlacements(candidates,spawns,stats);
        _requested=stats.Requested;_placed=stats.Placed;
        int dockPlaced=spawns.Count(x=>_dockNpcIds.Contains(x.Id));
        ChooseCameraAmongCrowd(spawns);
        ChooseDockCamera(spawns);
        ChooseGuildCamera(spawns);
        BuildModels(spawns);
        GD.Print($"POPULATION PREVIEW placed={stats.Placed}/{stats.Requested}, "+
            $"rejected={stats.Rejected}, shifted={stats.Shifted}, "+
            $"obstacleAttempts={stats.BlockedByScenery}, terrainAttempts={stats.BlockedByTerrain}, "+
            $"crowdAttempts={stats.BlockedByCrowd}, unresolvedProps={_props.UnresolvedFootprints.Count}, "+
            $"sourceTriangles={_props.Raycaster.TriangleCount}, "+
            $"dockPlaced={dockPlaced}/{_dockNpcIds.Count}, dockLandingSites={_dockLandings.Count}, "+
            $"poolFallback={stats.FallbackPlacements}, openPool={stats.OpenGroundPool}, "+
            $"sourceMeshesReady={_props.HasPreparedGeometry}");
        Notify();
    }
    private void AuditPlacements(IReadOnlyList<PopulationSpawnPlanner.Candidate> candidates,
        IReadOnlyList<PopulationSpawnPlanner.Placement> spawns,
        PopulationSpawnPlanner.Summary stats)
    {
        var anchors=candidates.ToDictionary(c=>c.Id,c=>c);
        var centre=_props.DockStructures.Count>0 ? new Vector2(
            _props.DockStructures.Average(p=>p.X),_props.DockStructures.Average(p=>p.Y))
            : new Vector2(float.PositiveInfinity,float.PositiveInfinity);
        int nearDock=0,nonDockNear=0,sourceNamed=0,nonDockElsewhere=0;
        var rows=new List<object>();
        foreach(var p in spawns)
        {
            if(!anchors.TryGetValue(p.Id,out var a))continue;
            var ground=new Vector2(p.Position.X,p.Position.Z);
            float distance=ground.DistanceTo(new Vector2(a.AnchorX,a.AnchorZ));
            bool dock=_dockNpcIds.Contains(p.Id);
            bool harbour=ground.DistanceSquaredTo(centre)<135f*135f;
            if(harbour){nearDock++;if(!dock)nonDockNear++;}
            else if(!dock)nonDockElsewhere++;
            if(a.AnchorKind=="source_named_fixture")sourceNamed++;
            if(distance>a.MaxDisplacement+0.01f)
                throw new InvalidOperationException(
                    $"NPC {p.Name} displaced {distance:F1}m; limit {a.MaxDisplacement:F1}m");
            rows.Add(new {p.Id,p.Name,p.Place,a.AnchorKind,
                AnchorX=a.AnchorX,AnchorZ=a.AnchorZ,a.MaxDisplacement,
                SpawnX=p.Position.X,SpawnY=p.Position.Y,SpawnZ=p.Position.Z,
                Displacement=distance,DockAssigned=dock,NearDock=harbour});
        }
        GD.Print($"POPULATION DISTRIBUTION namedSourceFixtures={sourceNamed}, "+
            $"nearDocks={nearDock}, unrelatedNearDocks={nonDockNear}, "+
            $"nonDockElsewhere={nonDockElsewhere}, "+
            $"excluded={stats.Rejected}, total={stats.Requested}");
        string? path=System.Environment.GetEnvironmentVariable("ESO_POPULATION_AUDIT_PATH");
        if(!string.IsNullOrEmpty(path))
        {
            File.WriteAllText(path,JsonSerializer.Serialize(new {
                Requested=stats.Requested,Placed=stats.Placed,Excluded=stats.Rejected,
                NearDocks=nearDock,NonDockNearDocks=nonDockNear,
                NonDockElsewhere=nonDockElsewhere,SourceFixtureAnchors=sourceNamed,
                SourceLandmarkPositions=_props.SourceLandmarks.ToDictionary(
                    kv=>kv.Key,kv=>new {x=kv.Value.X,z=kv.Value.Y}),
                Placements=rows},new JsonSerializerOptions {WriteIndented=true}));
        }
    }

    private void PrepareDockShoreLandings()
    {
        // Only actual placed dock ramps/walkways identify the harbour. A landing
        // site is acceptable only after full source-mesh clearance and source
        // triangle terrain sampling; over-water pier positions will be rejected.
        var structures=_props.DockStructures.ToArray();
        if(structures.Length==0)
        {
            GD.PushWarning("No physical dock structures found; retaining map-estimated anchors.");
            return;
        }
        var rng=new Random(521870);
        foreach(var dock in structures)
        {
            if(_dockLandings.Count>=65)break;
            for(int i=0;i<40;i++)
            {
                float bearing=(float)(rng.NextDouble()*Math.Tau);
                float distance=3f+MathF.Sqrt((float)rng.NextDouble())*76f;
                float x=dock.X+MathF.Cos(bearing)*distance;
                float z=dock.Y+MathF.Sin(bearing)*distance;
                if(x< -350f||x>=450f||z< -350f||z>=450f)continue;
                var y=_surface.GroundAt(x,z);
                if(y is null||!_props.Raycaster.OpenSkyToTerrain(x,z,y.Value))continue;
                var point=new Vector2(x,z);
                if(_dockLandings.Any(p=>p.DistanceSquaredTo(point)<5.5f*5.5f))continue;
                _dockLandings.Add(point);
                break;
            }
        }
        GD.Print($"DOCK SPAWN LANDINGS {_dockLandings.Count} confirmed open-ground "+
            $"sites near {_props.DockStructures.Count} original dock structures; "+
            $"{_dockNpcIds.Count} dock-linked NPCs");
    }

    private void Notify()=>Changed?.Invoke(
        $"NPC stand-ins: {_placed}/{_requested} | source town NPCs: {_town.Count} | "+
        $"density: {_density:0.##}x | "+(_props.HasPreparedGeometry?"vertical source-mesh raycasts verified":"waiting for source meshes")+
        " | positions approximate [N toggle, J density]");

    private void ChooseCameraAmongCrowd(IReadOnlyList<PopulationSpawnPlanner.Placement> points)
    {
        (SuggestedCameraEye,SuggestedCameraTarget)=FindVisibleCamera(points);
    }
    private void ChooseDockCamera(IReadOnlyList<PopulationSpawnPlanner.Placement> points)
    {
        var docks=points.Where(p=>_dockNpcIds.Contains(p.Id)).ToArray();
        (SuggestedDockCameraEye,SuggestedDockCameraTarget)=FindVisibleCamera(docks);
        GD.Print($"DOCK CAMERA clear={SuggestedDockCameraEye.HasValue} dockNPCs={docks.Length}");
    }
    private void ChooseGuildCamera(IReadOnlyList<PopulationSpawnPlanner.Placement> points)
    {
        var guild=points.Where(p=>p.Place.Contains("Mages Guild",StringComparison.OrdinalIgnoreCase)
            ||p.Place.Contains("Fighters Guild",StringComparison.OrdinalIgnoreCase)).ToArray();
        (SuggestedGuildCameraEye,SuggestedGuildCameraTarget)=FindVisibleCamera(guild);
        GD.Print($"GUILD CAMERA clear={SuggestedGuildCameraEye.HasValue} guildNPCs={guild.Length}");
    }
    private (Vector3? Eye,Vector3? Target) FindVisibleCamera(
        IReadOnlyList<PopulationSpawnPlanner.Placement> points)
    {
        if(points.Count<2)return (null,null);
        var visible=new List<(int Count,float Distance,Vector3 Eye,Vector3 Target)>();
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
                float dist=candidate.Position.DistanceTo(other.Position);
                if(dist<5f||dist>30f)continue;
                var target=other.Position+new Vector3(0f,1.28f,0f);
                if(_props.Raycaster.Intersects(eye,target))continue;
                bool clear=true;
                for(int k=1;k<9;k++)
                {
                    var sample=eye.Lerp(target,k/9f);
                    var ground=_surface.GroundAt(sample.X,sample.Z);
                    if(ground is null||ground.Value>sample.Y-0.6f)
                    {clear=false;break;}
                }
                if(!clear)continue;
                visible.Add((crowd,dist,eye,target));
                break;
            }
        }
        if(visible.Count==0)return (null,null);
        var best=visible.OrderByDescending(v=>v.Count)
            .ThenBy(v=>MathF.Abs(v.Distance-13f)).First();
        return (best.Eye,best.Target);
    }
    public void PreviewPinForScreenshot() => _hoverInspector?.PreviewPinForScreenshot();

    public bool PreviewHoverForScreenshot(Camera3D camera, out string npcName)
    {
        npcName=string.Empty;
        if(_hoverInspector==null)return false;
        var viewport=GetViewport().GetVisibleRect().Size;
        foreach(var p in _visibleSpawns.OrderBy(p=>
            camera.GlobalPosition.DistanceSquaredTo(ToGlobal(p.Position))))
        {
            var head=ToGlobal(p.Position+new Vector3(0,1.35f,0));
            if(camera.IsPositionBehind(head))continue;
            Vector2 screen=camera.UnprojectPosition(head);
            if(screen.X<80||screen.Y<50||screen.X>viewport.X-80||screen.Y>viewport.Y-50)
                continue;
            // In a crowd the frontmost selectable mannequin may overlap
            // this projected world point. Selecting that nearer NPC is correct.
            if(_hoverInspector.PreviewHoverAt(screen)&&_hoverInspector.HoveredId is string selected)
            {
                npcName=_visibleSpawns.FirstOrDefault(x=>x.Id==selected).Name;
                GD.Print($"NPC HOVER GPU TARGET expected={p.Name}, selected={npcName}");
                return true;
            }
        }
        return false;
    }
    private void BuildModels(IReadOnlyList<PopulationSpawnPlanner.Placement> points)
    {
        _visibleSpawns.Clear();
        _visibleSpawns.AddRange(points);
        if(_models!=null){_models.QueueFree();_models=null;}
        var root=new Node3D{Name="ApproximateNPCStandIns",Visible=_shown};
        AddChild(root);_models=root;
        _hoverInspector?.SetPopulation(points.Select(p=>
            p with {Position=ToGlobal(p.Position)}).ToArray(),_sourcePeople);
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
