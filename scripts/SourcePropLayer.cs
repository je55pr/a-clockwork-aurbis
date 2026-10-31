using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Process = System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// Source-only placed props within one original 8x8 terrain patch.
/// Model IDs, positions, and angles come from the original 96-byte source
/// records within channel-21 chunks. Placements are never invented or substituted.
/// Native GR2 conversion remains in an isolated helper process.
/// </summary>
public partial class SourcePropLayer : Node3D
{
    private readonly record struct Fixture(uint InstanceId, uint ModelId,
        Vector3 SourcePosition, Vector3 SourceAngles);
    private readonly record struct Prepared(uint ModelId,string Path,string Error);
    private readonly record struct MaterialGroup(int First,int Triangles,uint Diffuse,
        bool TwoSided);

    private readonly ConcurrentQueue<Prepared> _prepared=new();
    private readonly Dictionary<uint,List<Transform3D>> _transforms=new();
    private readonly Dictionary<uint,string> _names=new();
    private readonly Dictionary<uint,string> _cacheKeys=new();
    private readonly Dictionary<uint,Texture2D?> _textures=new();
    // A single source-manifest scan avoids 1.29M-row FindId searches
    // for every unique DDS referenced by the scenery meshes.
    private readonly Dictionary<uint,int> _sourceOrdinals=new();
    private EsoArchive _archive=null!;
    private OodleDecoder _decoder=null!;
    private TerrainBundle _bundle=null!;
    private string _root="";
    private int _loadedModels,_missingModels,_placements,_meshBatches;
    private long _installTicks,_rayTicks,_surfaceTicks,_textureTicks,_facesTicks,_instanceTicks;
    private int _uploadCount;
    private readonly ConcurrentQueue<(uint Id, Vector3[] Faces, Transform3D[] Placements)> _rayJobs=new();
    private readonly ConcurrentQueue<(uint Id,string Error)> _rayFailures=new();
    private int _rayPending,_rayWorkerRunning;
    private volatile bool _running;
    public Vector3 Focus {get;private set;}
    public int SourceInstances=>_placements;
    public int SourceModels=>_transforms.Count;
    public float? HarbourWaterCandidate {get;private set;}
    public int HarbourAnchorCount {get;private set;}
    private readonly List<Vector2> _dockStructures=new();
    public IReadOnlyList<Vector2> DockStructures=>_dockStructures;
    // Source-model identification, NOT a guessed UESP image-map transform.
    private readonly Dictionary<string,Vector2> _sourceLandmarks=
        new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string,Vector2> SourceLandmarks=>_sourceLandmarks;
    public bool TrySourceLandmark(string place,out Vector2 localAnchor)
    {
        if(_sourceLandmarks.TryGetValue(place,out localAnchor))return true;
        const string prefix="Vulkhel Guard (near ";
        if(place.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&place.EndsWith(")",StringComparison.Ordinal))
            return _sourceLandmarks.TryGetValue(place.Substring(prefix.Length,
                place.Length-prefix.Length-1),out localAnchor);
        localAnchor=default;
        return false;
    }
    public event Action<string>? Progress;
    public event Action? GeometryPrepared;
    public readonly record struct PropFootprint(float MinX, float MinZ, float MaxX, float MaxZ);
    private readonly List<PropFootprint> _propFootprints = new();
    public IReadOnlyList<PropFootprint> PropFootprints => _propFootprints;
    public bool HasPreparedGeometry { get; private set; }
    public SourceGeometryRaycaster Raycaster { get; } = new();
    public bool EnableFirstPersonPhysics { get; set; }
    public SourcePropCollisions? NearbyCollisions { get; private set; }
    public void TrackPlayerCollision(Node3D player) => NearbyCollisions?.Track(player);
    public static bool BlocksNpcSpawn(string sourceModelName)
        => !string.Equals(sourceModelName,"ActiveRunes",StringComparison.OrdinalIgnoreCase);
    private readonly Dictionary<uint,List<PropFootprint>> _coarsePerModel = new();
    private readonly List<PropFootprint> _unresolvedFootprints = new();
    public IReadOnlyList<PropFootprint> UnresolvedFootprints => _unresolvedFootprints;

    public void Setup(EsoArchive archive,OodleDecoder decoder,TerrainBundle bundle,
        float lowestHeight,int tileX,int tileZ)
    {
        _archive=archive;_decoder=decoder;_bundle=bundle;
        _root=ProjectSettings.GlobalizePath("res://");
        var index=EsoModelNameIndex.Load(Path.Combine(_root,".research","model-names.tsv"));
        // Channel 21 stores the full per-chunk placed-object catalogue,
        // including the vegetation that also appears in descriptor streams.
        // It must replace (not supplement) channels 1/2 to avoid duplicates.
        // Its file IDs index 250-unit chunks; per-instance X/Z are chunk local.
        float minX=(tileX-3)*100f,maxX=(tileX+5)*100f;
        float minZ=(tileZ-3)*100f,maxZ=(tileZ+5)*100f;
        float cx=(tileX+0.5f)*100f;
        float cz=(tileZ+0.5f)*100f;
        var fixtures=new List<Fixture>(12000);
        int sourceChunks=0;
        for(int i=bundle.DescriptorOrdinal+1;i<archive.Count;i++)
        {
            var record=archive.GetRecord(i);
            var tag=archive.GetIdIndexFields(i);
            if(record.ArchiveIndex!=bundle.ArchiveIndex||
                (record.UncompressedSize==583&&tag.Flags==0x4400&&tag.Field2==0))
                break;
            if(tag.Flags!=0x4000 ||
                tag.Field2!=(bundle.DescriptorId*32+21) ||
                record.UncompressedSize is <8 or >2_000_000)continue;
            int chunkX=(int)(record.FileId>>16);
            int chunkZ=(int)(record.FileId&0xffff);
            // Only decode chunks overlapping the high-resolution 8x8 view.
            if(chunkX*250>=maxX||(chunkX+1)*250<=minX||
                chunkZ*250>=maxZ||(chunkZ+1)*250<=minZ)
                continue;
            byte[] data=archive.ReadDecoded(record,decoder);
            if(data.Length<8||BinaryPrimitives.ReadUInt32LittleEndian(data)!=23)
                throw new InvalidDataException("Unexpected placed-object chunk format");
            int count=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
            if(count<0||(long)count*96+8>data.Length)
                throw new InvalidDataException("Invalid placed-object count/stride");
            sourceChunks++;
            for(int j=0;j<count;j++)
            {
                int at=8+j*96;
                uint id=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                uint model=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at+68));
                var rotation=new Vector3(BitConverter.ToSingle(data,at+16),
                    BitConverter.ToSingle(data,at+20),BitConverter.ToSingle(data,at+24));
                float localX=BitConverter.ToSingle(data,at+28);
                float y=BitConverter.ToSingle(data,at+32);
                float localZ=BitConverter.ToSingle(data,at+36);
                if(!rotation.IsFinite()||!float.IsFinite(localX)||
                    !float.IsFinite(localZ)||!float.IsFinite(y)||
                    localX<0||localX>=250||localZ<0||localZ>=250)
                    continue;
                var position=new Vector3(chunkX*250f+localX,y,
                    chunkZ*250f+localZ);
                if(position.X>=minX&&position.X<maxX&&
                    position.Z>=minZ&&position.Z<maxZ)
                    fixtures.Add(new Fixture(id,model,position,rotation));
            }
        }
        if(sourceChunks==0)
            throw new InvalidDataException("No placed-object chunks in source terrain area");
        // Build a lookup in one archive scan. Repeated FindId() over the 1.3M
        // resource manifest would make hundreds of models prohibitively slow.
        var modelIds=fixtures.Select(f=>f.ModelId).ToHashSet();
        var candidateRecords=new Dictionary<uint,List<EsoRecord>>();
        for(int i=0;i<archive.Count;i++)
        {
            var record=archive.GetRecord(i);
            if(archive.GetIdIndexFields(i).Flags!=0)continue;
            if(record.UncompressedSize is >=128 and <=20_000_000)
                _sourceOrdinals.TryAdd(record.FileId,i);
            if(!modelIds.Contains(record.FileId))continue;
            if(!candidateRecords.TryGetValue(record.FileId,out var records))
                candidateRecords[record.FileId]=records=new List<EsoRecord>();
            records.Add(record);
        }
        var filtered=new List<Fixture>();
        var verified=new Dictionary<uint,string?>();
        foreach(var fixture in fixtures)
        {
            if(fixture.SourcePosition.X<minX||fixture.SourcePosition.X>=maxX||
                fixture.SourcePosition.Z<minZ||fixture.SourcePosition.Z>=maxZ)
                continue;
            if(!verified.TryGetValue(fixture.ModelId,out var name))
            {
                name=null;
                if(index.TryGetValue(fixture.ModelId,out var model)&&
                    model.Status=="OK")
                {
                    string candidate=EsoModelNameIndex.PreferredName(model);
                    // PFX denotes effects, while CLN_INV denotes invisible helpers.
                    // Their extracted meshes are not normal visible scene props.
                    if(candidate.StartsWith("PFX_",StringComparison.OrdinalIgnoreCase)||
                       candidate.StartsWith("CLN_INV_",StringComparison.OrdinalIgnoreCase))
                    {
                        verified[fixture.ModelId]=null;
                        continue;
                    }
                    // Accept every original, verified placed model, not just vegetation.
                    var record=candidateRecords.TryGetValue(fixture.ModelId,out var candidates)
                        ? candidates.FirstOrDefault(r=>
                            EsoModelNameIndex.MatchesSource(model,r.ArchiveIndex,
                                r.Offset,r.CompressedSize,r.UncompressedSize))
                        : default;
                    if(record.UncompressedSize!=0&&
                        EsoModelNameIndex.MatchesSource(model,record.ArchiveIndex,
                            record.Offset,record.CompressedSize,record.UncompressedSize))
                    {
                        name=candidate;
                        _cacheKeys[fixture.ModelId]=string.Format(
                            "{0:X8}-a{1}-o{2}-c{3}-h{4:X8}-u{5}-v5.esm",
                            fixture.ModelId,record.ArchiveIndex,record.Offset,
                            record.CompressedSize,record.Hash,record.UncompressedSize);
                    }
                }
                verified[fixture.ModelId]=name;
            }
            if(name==null)continue;
            filtered.Add(fixture);
            _names[fixture.ModelId]=name;
            if(!_transforms.TryGetValue(fixture.ModelId,out var list))
                _transforms[fixture.ModelId]=list=new List<Transform3D>();
            // Source rotation angles are unmodified. Euler order still needs
            // visual validation against ESO; no arbitrary scale is applied.
            var basis=Basis.FromEuler(fixture.SourceAngles);
            list.Add(new Transform3D(basis,
                fixture.SourcePosition-new Vector3(cx,lowestHeight,cz)));
        }
        // Exact original scenery fixture IDs give the correct approximate
        // building anchors; a map-image marker often does not.
        var namedSourceBuildings=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mages Guild (Vulkhel Guard)"]="ALT_STR_GuildMage001",
            ["Fighters Guild (Vulkhel Guard)"]="GEN_LSB_BannerFighterGuildSmall001",
            ["Vulkhel Guard Manor & Treasury"]="ALT_STR_Manor001",
            ["Vulkhel Guard Warehouse"]="ALT_STR_Warehouse001",
            ["Harborside Market"]="GEN_LSB_BannerGuildSellerAreaSmall001",
            ["Guard's Forge"]="ALT_STR_Forge001"
        };
        foreach(var mapping in namedSourceBuildings)
        {
            var matches=filtered.Where(f=>string.Equals(
                _names[f.ModelId],mapping.Value,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length==0)continue;
            // A category may have multiple copies. Keep a stable,
            // deterministic position; never guess a missing building.
            var landmark=matches.OrderBy(f=>f.SourcePosition.X)
                .ThenBy(f=>f.SourcePosition.Z).First();
            _sourceLandmarks[mapping.Key]=new Vector2(
                landmark.SourcePosition.X-cx,landmark.SourcePosition.Z-cz);
        }
        GD.Print($"SOURCE LANDMARKS {_sourceLandmarks.Count}: "+
            string.Join(", ",_sourceLandmarks.Keys.OrderBy(x=>x)));
        // Fast provisional clearance while genuine mesh bounds load asynchronously.
        // Large architectural models receive conservative exclusion radii.
        foreach(var fixture in filtered)
        {
            string label=_names[fixture.ModelId];
            float padding=IsArchitecture(label)?10f:
                label.Contains("Tree",StringComparison.OrdinalIgnoreCase)?3.4f:
                label.Contains("Boat",StringComparison.OrdinalIgnoreCase)?5.5f:1.5f;
            float x=fixture.SourcePosition.X-cx,z=fixture.SourcePosition.Z-cz;
            var coarse=new PropFootprint(x-padding,z-padding,x+padding,z+padding);
            _propFootprints.Add(coarse);
            if(!_coarsePerModel.TryGetValue(fixture.ModelId,out var group))
                _coarsePerModel[fixture.ModelId]=group=new();
            group.Add(coarse);
        }
        // These are actual ESO source fixture origins, unlike uncalibrated
        // UESP map image coordinates. Use real dock walkway/ramp structures
        // as evidence for the harbour vicinity, not a fictional centre point.
        var dockPilings=filtered.Where(f=>_names[f.ModelId].Contains(
            "DockPilling",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(dockPilings.Length>4)
        {
            var xs=dockPilings.Select(f=>f.SourcePosition.X).OrderBy(x=>x).ToArray();
            var zs=dockPilings.Select(f=>f.SourcePosition.Z).OrderBy(z=>z).ToArray();
            var harbourCentre=new Vector2(xs[xs.Length/2],zs[zs.Length/2]);
            foreach(var fixture in filtered)
            {
                var label=_names[fixture.ModelId];
                bool structure=label.StartsWith("GEN_DCK_Dock",StringComparison.OrdinalIgnoreCase)
                    && (label.Contains("Ramp",StringComparison.OrdinalIgnoreCase)
                       || label.Contains("Narrow",StringComparison.OrdinalIgnoreCase)
                       || label.Contains("Wide",StringComparison.OrdinalIgnoreCase)
                       || label.Contains("Low",StringComparison.OrdinalIgnoreCase));
                if(!structure)continue;
                var pos=new Vector2(fixture.SourcePosition.X,fixture.SourcePosition.Z);
                if(pos.DistanceSquaredTo(harbourCentre)>175f*175f)continue;
                _dockStructures.Add(pos-new Vector2(cx,cz));
            }
            GD.Print($"SOURCE DOCK STRUCTURES {_dockStructures.Count} source ramps/walkways, "+
                $"harbour piling centre {harbourCentre.X:F1},{harbourCentre.Y:F1}");
        }
        _placements=filtered.Count;
        // Independently authored floating boats identify a likely common
        // harbour-water elevation. This is NOT a decoded ESO water-level
        // record and must remain labelled experimental in the viewer.
        var boatHeights=filtered
            .Where(f=>_names[f.ModelId].StartsWith("ALT_CSB_Boat",
                StringComparison.OrdinalIgnoreCase))
            .Select(f=>f.SourcePosition.Y).OrderBy(h=>h).ToArray();
        int bestStart=0,bestCount=0;
        for(int i=0;i<boatHeights.Length;i++)
        {
            int n=0;
            while(i+n<boatHeights.Length&&
                boatHeights[i+n]-boatHeights[i]<=1.5f)n++;
            if(n>bestCount){bestStart=i;bestCount=n;}
        }
        if(bestCount>=3)
        {
            HarbourWaterCandidate=boatHeights[bestStart+bestCount/2];
            HarbourAnchorCount=bestCount;
            GD.Print($"SOURCE WATER CANDIDATE from {bestCount} boat origins: "+
                $"elevation={HarbourWaterCandidate:F3} (not verified sea level)");
        }
        if(_placements==0)
            throw new InvalidDataException("No verified prop fixtures within the selected terrain patch");
        float[] ys=filtered.Select(f=>f.SourcePosition.Y).OrderBy(v=>v).ToArray();
        // Use a source-authored hedge cluster to centre the first-person view.
        var civic=filtered.Where(f=>_names[f.ModelId].Contains("Hedge",
                StringComparison.OrdinalIgnoreCase)||
            _names[f.ModelId].Contains("topiary",
                StringComparison.OrdinalIgnoreCase)).ToArray();
        Vector3 target=new((tileX+0.5f)*100f,0f,(tileZ+0.5f)*100f);
        if(civic.Length>=2)
        {
            var xs=civic.Select(f=>f.SourcePosition.X).OrderBy(v=>v).ToArray();
            var zs=civic.Select(f=>f.SourcePosition.Z).OrderBy(v=>v).ToArray();
            target=new Vector3(xs[xs.Length/2],0f,zs[zs.Length/2]);
        }
        Focus=new Vector3(target.X-cx,0f,target.Z-cz);
        GD.Print(string.Format(
            "SOURCE PROPS tiles {0}..{1} / {2}..{3}: {4} placements, {5} models, {6} hedges, camera {7:F1},{8:F1}",
            tileX-3,tileX+4,tileZ-3,tileZ+4,_placements,
            _transforms.Count,civic.Length,target.X,target.Z));
    }

    public override void _Ready()
    {
        if (EnableFirstPersonPhysics)
        {
            NearbyCollisions = new SourcePropCollisions { Name = "StreamingSourceCollisions" };
            AddChild(NearbyCollisions);
        }
        _running=true;
        var ordered=_transforms.OrderByDescending(g=>IsArchitecture(_names[g.Key]))
            .ThenByDescending(g=>g.Value.Count).Select(g=>g.Key).ToArray();
        Progress?.Invoke($"Source props: {_placements:N0} source placements, "+
            $"{ordered.Length} GR2 models, preparing geometry...");
        _=Task.Run(()=>Prepare(ordered));
    }

    private static bool IsArchitecture(string name) =>
        name.StartsWith("ALT_STR_",StringComparison.OrdinalIgnoreCase)||
        name.StartsWith("ALT_WAF_",StringComparison.OrdinalIgnoreCase)||
        name.StartsWith("GEN_DCK_",StringComparison.OrdinalIgnoreCase)||
        name.StartsWith("ROK_",StringComparison.OrdinalIgnoreCase);

    private void Prepare(uint[] models)
    {
        foreach(uint id in models)
        {
            if(!_running)return;
            try{_prepared.Enqueue(new Prepared(id,EnsureGeometry(id),""));}
            catch(Exception e){_prepared.Enqueue(new Prepared(id,"",e.Message));}
        }
    }

    private string EnsureGeometry(uint id)
    {
        string cache=Path.Combine(_root,".research","vulkhel-vegetation-geometry");
        Directory.CreateDirectory(cache);
        string output=Path.Combine(cache,_cacheKeys[id]);
        if(File.Exists(output)&&new FileInfo(output).Length>32)return output;
        string helper=Path.Combine(_root,"tools","ModelGeometry",
            "bin","Debug","net8.0","ModelGeometry.dll");
        string granny=System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
            Path.Combine(_root,".research","uesp-esoapps","EsoExtractData",
                "EsoExtractData","granny2_x64.dll");
        if(!File.Exists(helper)||!File.Exists(granny))
            throw new FileNotFoundException("Isolated GR2 geometry helper or local decoder unavailable");
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet"){
            WorkingDirectory=_root,CreateNoWindow=true,UseShellExecute=false,
            RedirectStandardError=true
        };
        foreach(var arg in new[]{helper,granny,id.ToString("X8"),output,
            _archive.SourcePath,_decoder.LibraryPath})
            process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start()||!process.WaitForExit(60000))
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            throw new TimeoutException($"GR2 geometry helper timed out for 0x{id:X8}");
        }
        if(process.ExitCode!=0)
            throw new InvalidDataException($"GR2 conversion 0x{id:X8}: "+
                process.StandardError.ReadToEnd());
        if(!File.Exists(output))throw new IOException("No derived mesh returned");
        return output;
    }

    public override void _Process(double delta)
    {
        // The original one-mesh-per-frame cap imposed a ~18s minimum on a
        // 1,072-model Auridon, even when every source mesh was already cached.
        // Upload several prepared meshes while respecting a per-frame time budget:
        // slow complex architecture costs more, small props fill the remainder.
        long batchStart=Stopwatch.GetTimestamp();
        const int maxModelsPerFrame=32;
        const double uploadBudgetMs=7.0;
        for(int n=0;n<maxModelsPerFrame &&
            (n==0 || Stopwatch.GetElapsedTime(batchStart).TotalMilliseconds<uploadBudgetMs) &&
            _prepared.TryDequeue(out var ready);n++)
        {
            try
            {
                if(ready.Error.Length>0)throw new InvalidDataException(ready.Error);
                long t0=Stopwatch.GetTimestamp();
                InstallGeometry(ready.ModelId,ready.Path);
                _installTicks+=Stopwatch.GetTimestamp()-t0;
                _uploadCount++;
                _loadedModels++;
            }
            catch(Exception e)
            {
                _missingModels++;
                if(_coarsePerModel.TryGetValue(ready.ModelId,out var footprintGroup))
                    _unresolvedFootprints.AddRange(footprintGroup);
                GD.PushWarning($"ESO prop model 0x{ready.ModelId:X8} not rendered: {e.Message}");
            }
            if((_loadedModels+_missingModels)%5==0||
                _loadedModels+_missingModels==_transforms.Count)
            {
                string message=$"Southern Auridon original props: "+
                    $"{_placements:N0} source instances / "+
                    $"{_loadedModels}/{_transforms.Count} models loaded, "+
                    $"{_missingModels} failed, {_meshBatches} GPU instance batches";
                Progress?.Invoke(message);
                GD.Print("SOURCE PROPS "+message);
            }
        }
        while(_rayFailures.TryDequeue(out var failure))
        {
            if(_coarsePerModel.TryGetValue(failure.Id,out var affected))
                _unresolvedFootprints.AddRange(affected);
            GD.PushWarning($"ESO raycaster 0x{failure.Id:X8}: {failure.Error}");
        }
        if(_loadedModels+_missingModels==_transforms.Count &&
            Volatile.Read(ref _rayPending)==0 && _rayJobs.IsEmpty &&
            _rayFailures.IsEmpty && !HasPreparedGeometry)
        {
            HasPreparedGeometry=true;
            // No further DDS lookups are needed after all source models are
            // installed. Return the large temporary index memory to the GC.
            _sourceOrdinals.Clear();
            _sourceOrdinals.TrimExcess();
            GD.Print($"SOURCE PROFILE: {Stopwatch.GetElapsedTime(0,_installTicks).TotalSeconds:F2}s main-thread upload CPU, " +
                $"{Stopwatch.GetElapsedTime(0,Interlocked.Read(ref _rayTicks)).TotalSeconds:F2}s background raycaster CPU, " +
                $"{_uploadCount} models (surfaces " +
                $"{Stopwatch.GetElapsedTime(0,_surfaceTicks).TotalSeconds:F2}s, " +
                $"DDS {Stopwatch.GetElapsedTime(0,_textureTicks).TotalSeconds:F2}s, " +
                $"faces {Stopwatch.GetElapsedTime(0,_facesTicks).TotalSeconds:F2}s, " +
                $"instances {Stopwatch.GetElapsedTime(0,_instanceTicks).TotalSeconds:F2}s)");
            GD.Print($"SOURCE RAYCAST READY: {Raycaster.MeshCount} source meshes, "+
                $"{Raycaster.InstanceCount} transformed instances, "+
                $"{Raycaster.TriangleCount} source triangles, "+
                $"{_unresolvedFootprints.Count} undecoded placements blocked.");
            GeometryPrepared?.Invoke();
            SetProcess(false);
        }
    }

    // Raycaster BVHs are pure CPU structures. Building the 1.3M triangle
    // spatial index on the main thread used ~11s during each warm start.
    // A single worker preserves source order and avoids concurrent mutation.
    // No gameplay lookup is allowed until pending reaches zero.
    private void ScheduleRaycast(uint id,Vector3[] faces,
        IReadOnlyList<Transform3D> placements)
    {
        Interlocked.Increment(ref _rayPending);
        _rayJobs.Enqueue((id,faces,placements.ToArray()));
        if(Interlocked.CompareExchange(ref _rayWorkerRunning,1,0)==0)
            _=Task.Run(DrainRayJobs);
    }

    private void DrainRayJobs()
    {
        while(true)
        {
            while(_rayJobs.TryDequeue(out var job))
            {
                try
                {
                    if(_running)
                    {
                        long begin=Stopwatch.GetTimestamp();
                        Raycaster.AddMesh(job.Faces,job.Placements);
                        Interlocked.Add(ref _rayTicks,Stopwatch.GetTimestamp()-begin);
                    }
                }
                catch(Exception e)
                {
                    _rayFailures.Enqueue((job.Id,e.Message));
                }
                finally
                {
                    Interlocked.Decrement(ref _rayPending);
                }
            }
            Interlocked.Exchange(ref _rayWorkerRunning,0);
            if(_rayJobs.IsEmpty ||
                Interlocked.CompareExchange(ref _rayWorkerRunning,1,0)!=0)
                return;
        }
    }

    private void InstallGeometry(uint id,string path)
    {
        if(new FileInfo(path).Length>72_000_000)throw new InvalidDataException("Mesh too large");
        using var reader=new BinaryReader(File.OpenRead(path));
        if(new string(reader.ReadChars(4))!="ESM5")
            throw new InvalidDataException("Invalid derived source mesh");
        int meshCount=reader.ReadInt32();
        if(meshCount is <1 or >128)throw new InvalidDataException("Invalid mesh count");
        foreach(int meshIndex in Enumerable.Range(0,meshCount))
        {
            string name=reader.ReadString();
            int nv=reader.ReadInt32(),ni=reader.ReadInt32(),ng=reader.ReadInt32();
            bool frame=reader.ReadBoolean(),uv2Present=reader.ReadBoolean();
            if(nv is <3 or >400000||ni is <3 or >1200000||
                ni%3!=0||ng is <1 or >256)throw new InvalidDataException("Mesh bounds");
            var vertices=new Vector3[nv];
            var uvs=new Vector2[nv];
            var normals=new Vector3[nv];
            for(int i=0;i<nv;i++)vertices[i]=new Vector3(
                reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            for(int i=0;i<nv;i++)uvs[i]=new Vector2(reader.ReadSingle(),reader.ReadSingle());
            var uv2=new Vector2[nv];
            for(int i=0;i<nv;i++)uv2[i]=new Vector2(reader.ReadSingle(),reader.ReadSingle());
            for(int i=0;i<nv;i++)normals[i]=new Vector3(
                reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            var tangents=new float[nv*4];
            for(int i=0;i<tangents.Length;i++)tangents[i]=reader.ReadSingle();
            var indices=new int[ni];
            for(int i=0;i<ni;i++){
                indices[i]=reader.ReadInt32();
                if(indices[i]<0||indices[i]>=nv)
                    throw new InvalidDataException("Triangle index outside source mesh");
            }
            var materials=new List<MaterialGroup>();
            for(int g=0;g<ng;g++)
            {
                _=reader.ReadString();
                int first=reader.ReadInt32(),triangles=reader.ReadInt32();
                uint diffuse=reader.ReadUInt32();
                for(int k=0;k<4;k++)_=reader.ReadUInt32();
                bool known=reader.ReadBoolean();
                for(int k=0;k<4;k++)_=reader.ReadBoolean();
                bool twoSided=reader.ReadBoolean();
                _=reader.ReadInt32();_=reader.ReadInt32();
                for(int k=0;k<3;k++)_=reader.ReadSingle();
                if(first<0||triangles<1||(long)(first+triangles)*3>ni)
                    throw new InvalidDataException("Bad material group");
                materials.Add(new MaterialGroup(first,triangles,diffuse,!known||twoSided));
            }
            var mesh=new ArrayMesh();
            // Preserve the original per-material partition and source UVs.
            foreach(var g in materials)
            {
                var part=new int[g.Triangles*3];
                Array.Copy(indices,g.First*3,part,0,part.Length);
                var arrays=new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex]=vertices;
                arrays[(int)Mesh.ArrayType.TexUV]=uvs;
                if(uv2Present)arrays[(int)Mesh.ArrayType.TexUV2]=uv2;
                if(frame)
                {
                    arrays[(int)Mesh.ArrayType.Normal]=normals;
                    arrays[(int)Mesh.ArrayType.Tangent]=tangents;
                }
                arrays[(int)Mesh.ArrayType.Index]=part;
                long surfaceStart=Stopwatch.GetTimestamp();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
                _surfaceTicks+=Stopwatch.GetTimestamp()-surfaceStart;
                var mat=new StandardMaterial3D{
                    AlbedoColor=Colors.White,
                    Roughness=0.88f,
                    CullMode=BaseMaterial3D.CullModeEnum.Disabled,
                    Transparency=BaseMaterial3D.TransparencyEnum.AlphaScissor,
                    AlphaScissorThreshold=0.4f
                };
                long textureStart=Stopwatch.GetTimestamp();
                var diffuse=LoadDiffuse(g.Diffuse);
                _textureTicks+=Stopwatch.GetTimestamp()-textureStart;
                if(diffuse!=null)mat.AlbedoTexture=diffuse;
                else GD.Print($"Source diffuse 0x{g.Diffuse:X8} unavailable for "+name);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount()-1,mat);
            }
            var placements=_transforms[id];
            NearbyCollisions?.Register(mesh, placements, _names[id]);
            // MultiMeshInstance3D has NO physics collider: register its exact source
            // triangles and its transforms with the CPU spatial/BVH raycaster.
            long faceStart=Stopwatch.GetTimestamp();
            var faceVertices=mesh.GetFaces();
            _facesTicks+=Stopwatch.GetTimestamp()-faceStart;
            if(faceVertices.Length==0)throw new InvalidDataException("Empty source prop mesh");
            // Some placed models are rendered visual effects, not physical
            // scenery. Their large flat effect meshes can falsely reject
            // otherwise valid town streets. Keep rendering them but do not
            // turn them into impenetrable collision geometry.
            if(BlocksNpcSpawn(_names[id]))
                ScheduleRaycast(id,faceVertices,placements);
            // Project each rendered prop bounding box onto the ground plane.
            var bounds=mesh.GetAabb();
            foreach(var transform in placements)
            {
                float minX=float.PositiveInfinity,minZ=float.PositiveInfinity;
                float maxX=float.NegativeInfinity,maxZ=float.NegativeInfinity;
                for(int corner=0;corner<8;corner++)
                {
                    var local=bounds.Position+new Vector3(
                        (corner&1)==0?0f:bounds.Size.X,
                        (corner&2)==0?0f:bounds.Size.Y,
                        (corner&4)==0?0f:bounds.Size.Z);
                    var world=transform*local;
                    minX=Math.Min(minX,world.X);minZ=Math.Min(minZ,world.Z);
                    maxX=Math.Max(maxX,world.X);maxZ=Math.Max(maxZ,world.Z);
                }
                if(float.IsFinite(minX)&&float.IsFinite(maxX)&&
                    float.IsFinite(minZ)&&float.IsFinite(maxZ))
                    _propFootprints.Add(new PropFootprint(minX,minZ,maxX,maxZ));
            }
            long instanceStart=Stopwatch.GetTimestamp();
            var multimesh=new MultiMesh{
                TransformFormat=MultiMesh.TransformFormatEnum.Transform3D,
                Mesh=mesh,
                InstanceCount=placements.Count
            };
            for(int i=0;i<placements.Count;i++)
                multimesh.SetInstanceTransform(i,placements[i]);
            AddChild(new MultiMeshInstance3D{
                Name=$"Source_{id:X8}_{meshIndex}",Multimesh=multimesh
            });
            _meshBatches++;
            _instanceTicks+=Stopwatch.GetTimestamp()-instanceStart;
        }
        if(reader.BaseStream.Position!=reader.BaseStream.Length)
            throw new InvalidDataException("Unexpected trailing bytes in source mesh");
    }

    private Texture2D? LoadDiffuse(uint id)
    {
        if(id==0)return null;
        if(_textures.TryGetValue(id,out var texture))return texture;
        Texture2D? result=null;
        try
        {
            if(!_sourceOrdinals.TryGetValue(id,out int ordinal))
            {
                _textures[id]=null;
                return null;
            }
            var record=_archive.GetRecord(ordinal);
            var bytes=_archive.ReadDecoded(record,_decoder);
            if(bytes.Length<128||BinaryPrimitives.ReadUInt32LittleEndian(bytes)!=0x20534444)
                return null;
            uint width=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
            uint height=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            uint fourcc=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(84));
            uint block=fourcc==0x31545844?8u:
                fourcc is 0x33545844 or 0x35545844?16u:0u;
            if(block==0||width is 0 or >4096||height is 0 or >4096)
                return null;
            uint flags=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),(flags|0x80000u)&~0x8u);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20),
                ((width+3u)/4u)*((height+3u)/4u)*block);
            using var image=new Image();
            if(image.LoadDdsFromBuffer(bytes)!=Error.Ok||image.IsEmpty())return null;
            result=ImageTexture.CreateFromImage(image);
        }
        catch(Exception e){GD.Print($"Source DDS 0x{id:X8}: {e.Message}");}
        _textures[id]=result;
        return result;
    }

    public override void _ExitTree(){_running=false;}
}
