using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Process = System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// Read-only, source-only vegetation placement test for the southern Auridon
/// lighthouse cluster. All object identities and transforms originate in the
/// 96-byte fixture streams. Nothing is procedurally scattered or substituted.
/// GR2 conversion remains in an isolated helper process.
/// </summary>
public partial class SourceVegetationLayer : Node3D
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
    private EsoArchive _archive=null!;
    private OodleDecoder _decoder=null!;
    private TerrainBundle _bundle=null!;
    private string _root="";
    private int _loadedModels,_missingModels,_placements,_meshBatches;
    private volatile bool _running;
    public Vector3 Focus {get;private set;}
    public int SourceInstances=>_placements;
    public int SourceModels=>_transforms.Count;
    public event Action<string>? Progress;

    public void Setup(EsoArchive archive,OodleDecoder decoder,TerrainBundle bundle,
        float lowestHeight,float radius=600f)
    {
        _archive=archive;_decoder=decoder;_bundle=bundle;
        _root=ProjectSettings.GlobalizePath("res://");
        var index=EsoModelNameIndex.Load(Path.Combine(_root,".research","model-names.tsv"));
        var fixtures=new List<Fixture>(40000);
        for(int i=bundle.DescriptorOrdinal+1;i<archive.Count;i++)
        {
            var record=archive.GetRecord(i);
            var tag=archive.GetIdIndexFields(i);
            if(record.ArchiveIndex!=bundle.ArchiveIndex||
                (record.UncompressedSize==583&&tag.Flags==0x4400&&tag.Field2==0))
                break;
            if(tag.Flags!=0x4800 || (uint)(tag.Field2>>5)!=bundle.DescriptorId ||
                (tag.Field2&31) is not (1 or 2) ||
                record.UncompressedSize is <8 or >8_000_000)continue;
            byte[] data=archive.ReadDecoded(record,decoder);
            if(data.Length<8 || BinaryPrimitives.ReadUInt32LittleEndian(data)!=23)
                continue;
            int count=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
            if(count<0||(long)count*96+8>data.Length)
                throw new InvalidDataException("Invalid fixture count/record stride");
            for(int j=0;j<count;j++)
            {
                int at=8+j*96;
                uint id=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                uint model=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at+68));
                var rotation=new Vector3(BitConverter.ToSingle(data,at+16),
                    BitConverter.ToSingle(data,at+20),BitConverter.ToSingle(data,at+24));
                var position=new Vector3(BitConverter.ToSingle(data,at+28),
                    BitConverter.ToSingle(data,at+32),BitConverter.ToSingle(data,at+36));
                if(!rotation.IsFinite()||!position.IsFinite())continue;
                fixtures.Add(new Fixture(id,model,position,rotation));
            }
        }

        // The two southernmost lighthouse records are source-authored anchors.
        // This chooses a test camera/search centre, NOT a prop placement.
        var lighthouse=fixtures.Where(f=>index.TryGetValue(f.ModelId,out var m)&&
                EsoModelNameIndex.PreferredName(m).Contains("LightHouse",
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f=>f.SourcePosition.Z).Take(2).ToArray();
        if(lighthouse.Length!=2)
            throw new InvalidDataException(
                "Unable to identify two southern source lighthouse anchors");
        Vector3 centre=(lighthouse[0].SourcePosition+
            lighthouse[1].SourcePosition)*0.5f;
        float cx=(bundle.OriginX+bundle.MaximumX+1)*50f;
        float cz=(bundle.OriginY+bundle.MaximumY+1)*50f;
        var filtered=new List<Fixture>();
        var verified=new Dictionary<uint,string?>();
        foreach(var fixture in fixtures)
        {
            Vector3 difference=fixture.SourcePosition-centre;
            if(difference.X*difference.X+difference.Z*difference.Z>radius*radius)
                continue;
            if(!verified.TryGetValue(fixture.ModelId,out var name))
            {
                name=null;
                if(index.TryGetValue(fixture.ModelId,out var model)&&
                    model.Status=="OK")
                {
                    string candidate=EsoModelNameIndex.PreferredName(model);
                    if(candidate.StartsWith("VEG_",StringComparison.OrdinalIgnoreCase)||
                       candidate.StartsWith("TRE_",StringComparison.OrdinalIgnoreCase))
                    {
                        var record=archive.FindId(fixture.ModelId).FirstOrDefault(r=>
                            archive.GetIdIndexFields(r.Ordinal).Flags==0);
                        if(record.UncompressedSize!=0&&
                            EsoModelNameIndex.MatchesSource(model,record.ArchiveIndex,
                                record.Offset,record.CompressedSize,record.UncompressedSize))
                        {
                            name=candidate;
                            _cacheKeys[fixture.ModelId]=$"{fixture.ModelId:X8}-"+
                                $"a{record.ArchiveIndex}-o{record.Offset}-"+
                                $"c{record.CompressedSize}-h{record.Hash:X8}-"+
                                $"u{record.UncompressedSize}-v5.esm";
                        }
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
        _placements=filtered.Count;
        if(_placements==0)
            throw new InvalidDataException("No verified vegetation fixtures in source area");
        float[] ys=filtered.Select(f=>f.SourcePosition.Y).OrderBy(v=>v).ToArray();
        // Frame the source-authored civic hedges rather than arbitrary terrain.
        // Camera framing is derived; no instance is moved or fabricated.
        var civic=filtered.Where(f=>_names[f.ModelId].Contains("Hedge",
                StringComparison.OrdinalIgnoreCase)||
                _names[f.ModelId].Contains("topiary",
                StringComparison.OrdinalIgnoreCase)).ToArray();
        Vector3 viewPoint=centre;
        if(civic.Length>=10)
        {
            var xs=civic.Select(f=>f.SourcePosition.X).OrderBy(v=>v).ToArray();
            var zs=civic.Select(f=>f.SourcePosition.Z).OrderBy(v=>v).ToArray();
            viewPoint=new Vector3(xs[xs.Length/2],centre.Y,zs[zs.Length/2]);
        }
        Focus=new Vector3(viewPoint.X-cx,ys[ys.Length/2]-lowestHeight+8,
            viewPoint.Z-cz);
        GD.Print($"SOUTHERN AURIDON civic hedges={civic.Length}, camera from source "+
            $"fixtures=({viewPoint.X:F1},{viewPoint.Z:F1})");
        GD.Print($"SOUTHERN AURIDON vegetation source: centre=({centre.X:F1},"+
            $"{centre.Z:F1}), radius={radius:F0}, original instances={_placements}, "+
            $"model IDs={_transforms.Count}; all source positions/angles preserved");
    }

    public override void _Ready()
    {
        _running=true;
        var ordered=_transforms.OrderByDescending(g=>g.Value.Count).Select(g=>g.Key).ToArray();
        Progress?.Invoke($"Vegetation: {_placements:N0} source placements, "+
            $"{ordered.Length} GR2 models, preparing geometry...");
        _=Task.Run(()=>Prepare(ordered));
    }

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
        // Strictly time-slice mesh creation and GPU resource uploads.
        for(int n=0;n<1 && _prepared.TryDequeue(out var ready);n++)
        {
            try
            {
                if(ready.Error.Length>0)throw new InvalidDataException(ready.Error);
                InstallGeometry(ready.ModelId,ready.Path);
                _loadedModels++;
            }
            catch(Exception e)
            {
                _missingModels++;
                GD.PushWarning($"ESO vegetation model 0x{ready.ModelId:X8} not rendered: {e.Message}");
            }
            if((_loadedModels+_missingModels)%5==0||
                _loadedModels+_missingModels==_transforms.Count)
            {
                string message=$"Southern Auridon original vegetation: "+
                    $"{_placements:N0} source instances / "+
                    $"{_loadedModels}/{_transforms.Count} models loaded, "+
                    $"{_missingModels} failed, {_meshBatches} GPU instance batches";
                Progress?.Invoke(message);
                GD.Print("SOURCE VEGETATION "+message);
            }
        }
        if(_loadedModels+_missingModels==_transforms.Count)
            SetProcess(false);
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
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
                var mat=new StandardMaterial3D{
                    AlbedoColor=Colors.White,
                    Roughness=0.88f,
                    CullMode=BaseMaterial3D.CullModeEnum.Disabled,
                    Transparency=BaseMaterial3D.TransparencyEnum.AlphaScissor,
                    AlphaScissorThreshold=0.4f
                };
                var diffuse=LoadDiffuse(g.Diffuse);
                if(diffuse!=null)mat.AlbedoTexture=diffuse;
                else GD.Print($"Source diffuse 0x{g.Diffuse:X8} unavailable for "+name);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount()-1,mat);
            }
            var placements=_transforms[id];
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
            var record=_archive.FindId(id).FirstOrDefault(r=>
                _archive.GetIdIndexFields(r.Ordinal).Flags==0&&
                r.UncompressedSize is >=128 and <=20_000_000);
            if(record.UncompressedSize==0)return null;
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
