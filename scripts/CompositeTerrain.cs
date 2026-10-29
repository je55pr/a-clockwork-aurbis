using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace EsoTest;

/// <summary>
/// Runtime-only sparse terrain merge. Positioning is explicit and derived from
/// matching source edge heights, never archive ID arithmetic or guessed offsets.
/// Invalid nonzero overlaps fail the merge rather than smoothing through them.
/// </summary>
public static class CompositeTerrain
{
    public readonly record struct Patch(uint SectorId, int OriginTileX, int OriginTileZ,
        int FirstTileX, int LastTileX, int FirstTileZ, int LastTileZ);

    public readonly record struct Result(ArrayMesh Mesh, int OriginalTiles, int VerifiedOverlaps,
        int RepairedPadding, int ConflictingOverlaps, int RenderedTriangles, int MissingTriangles,
        float MinElevation, float MaxElevation);

    private const int Step=64;
    private const int Samples=65;

    public static Result Create(EsoArchive archive,OodleDecoder decoder,
        IReadOnlyList<TerrainBundle> bundles,IReadOnlyList<Patch> patches)
    {
        if(patches.Count is <2 or >12)throw new ArgumentException("Composite expects 2-12 patches");
        int lowX=patches.Min(p=>p.OriginTileX+p.FirstTileX);
        int lowZ=patches.Min(p=>p.OriginTileZ+p.FirstTileZ);
        int highX=patches.Max(p=>p.OriginTileX+p.LastTileX);
        int highZ=patches.Max(p=>p.OriginTileZ+p.LastTileZ);
        int tilesX=highX-lowX+1, tilesZ=highZ-lowZ+1;
        int width=tilesX*Step+1,height=tilesZ*Step+1;
        if((long)width*height>2_500_000)throw new InvalidOperationException("Composite exceeds render limit");

        var values=new float[width*height];
        var present=new bool[values.Length];
        var cells=new bool[(width-1)*(height-1)];
        int loaded=0,duplicates=0,repairs=0,conflicts=0;
        var sectors=bundles.ToDictionary(g=>g.DescriptorId);

        foreach(var patch in patches)
        {
            if(!sectors.TryGetValue(patch.SectorId,out var sector))
                throw new InvalidOperationException($"No sector {patch.SectorId}");
            int patchCount=0;
            foreach(var record in sector.Tiles)
            {
                int localX=(int)(record.FileId>>16)-sector.OriginX;
                int localZ=(int)(record.FileId&0xffff)-sector.OriginY;
                if(localX<patch.FirstTileX||localX>patch.LastTileX||
                   localZ<patch.FirstTileZ||localZ>patch.LastTileZ)continue;

                byte[] raw=archive.ReadDecoded(record,decoder);
                if(!LandTile.TryParse(raw,out var tile)||tile==null||tile.Width!=Samples||tile.Height!=Samples)
                    throw new InvalidOperationException($"Bad 65x65 land tile row {record.Ordinal}");
                int globalTileX=patch.OriginTileX+localX-lowX;
                int globalTileZ=patch.OriginTileZ+localZ-lowZ;
                int baseX=globalTileX*Step,baseZ=globalTileZ*Step;
                for(int z=0;z<Samples;z++)
                for(int x=0;x<Samples;x++)
                {
                    int at=(baseZ+z)*width+baseX+x;
                    float elevation=tile.Samples[z*Samples+x];
                    if(!present[at]){present[at]=true;values[at]=elevation;}
                    else if(Math.Abs(values[at]-elevation)<=0.0005f){duplicates++;}
                    else if(values[at]==0||elevation==0)
                    {
                        if(values[at]==0)values[at]=elevation;
                        repairs++;
                    }
                    else conflicts++;
                }
                for(int z=0;z<Step;z++)
                for(int x=0;x<Step;x++)
                    cells[(baseZ+z)*(width-1)+baseX+x]=true;
                patchCount++;loaded++;
            }
            int expected=(patch.LastTileX-patch.FirstTileX+1)*
                (patch.LastTileZ-patch.FirstTileZ+1);
            if(patchCount!=expected)
                throw new InvalidOperationException($"Sector {patch.SectorId}: expected {expected} tiles; loaded {patchCount}");
        }
        if(conflicts>0)
            throw new InvalidOperationException($"Rejected {conflicts} conflicting nonzero height vertices.");

        float min=float.PositiveInfinity,max=float.NegativeInfinity;
        foreach(float v in values)if(v!=0){min=Math.Min(min,v);max=Math.Max(max,v);}
        if(!float.IsFinite(min))throw new InvalidOperationException("No nonzero original terrain elevations");
        float stepSize=500f/(5*Step); // provisional metre-scale, not an independently verified transform
        var vertices=new Vector3[values.Length];
        var normals=new Vector3[values.Length];
        for(int z=0;z<height;z++)
        for(int x=0;x<width;x++)
        {
            int i=z*width+x;
            float center=values[i]==0?min:values[i];
            vertices[i]=new Vector3((x-(width-1)*0.5f)*stepSize,center-min,
                (z-(height-1)*0.5f)*stepSize);
            float w=Sample(z,x-1),e=Sample(z,x+1),n=Sample(z-1,x),s=Sample(z+1,x);
            normals[i]=new Vector3(-(e-w)/(2*stepSize),1,-(s-n)/(2*stepSize)).Normalized();
        }

        float Sample(int z,int x){
            z=Math.Clamp(z,0,height-1);x=Math.Clamp(x,0,width-1);
            int i=z*width+x;
            return values[i]==0?min:values[i];
        }

        var triangles=new List<int>(loaded*Step*Step*6);
        int omitted=0;
        for(int z=0;z<height-1;z++)
        for(int x=0;x<width-1;x++)
        {
            int i=z*width+x;
            if(!cells[z*(width-1)+x]||!present[i]||!present[i+1]||
               !present[i+width]||!present[i+width+1]||
               values[i]==0||values[i+1]==0||
               values[i+width]==0||values[i+width+1]==0)
            {omitted+=2;continue;}
            triangles.Add(i);triangles.Add(i+width);triangles.Add(i+1);
            triangles.Add(i+1);triangles.Add(i+width);triangles.Add(i+width+1);
        }
        var arrays=new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex]=vertices;
        arrays[(int)Mesh.ArrayType.Normal]=normals;
        arrays[(int)Mesh.ArrayType.Index]=triangles.ToArray();
        var mesh=new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
        return new Result(mesh,loaded,duplicates,repairs,conflicts,triangles.Count/3,
            omitted,min,max);
    }
}
