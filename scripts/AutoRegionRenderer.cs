using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace EsoTest;

/// <summary>
/// Provisional, strictly source-based overview of a placement component.
/// Identifies terrain tile positions from shared fixture-ID transforms.
/// Conflicting source tiles are omitted, not blended or arbitrarily picked.
/// The overview uses original height samples at 1/4 resolution for speed.
/// </summary>
public static class AutoRegionRenderer
{
    public readonly record struct Result(
        ArrayMesh Mesh, int SectionCount, int CellsAvailable,
        int CellsRendered, int AmbiguousCells, int UndecodableCells,
        int MismatchedNeighbourSeams, int Triangles,
        float LowestHeight, float HighestHeight, Vector3 ViewCenter,
        float SuggestedDistance);

    private const int TileSamples = 65;
    private const float TileWorldWidth = 100f; // 500 fixture units = five tiles

    public static Result Create(
        EsoArchive archive, OodleDecoder decoder,
        IReadOnlyList<TerrainBundle> bundles,
        FixturePlacementGraph.Component group,
        int sampleStep=4, Vector2I? detailCenter=null, int detailRadius=2)
    {
        if (sampleStep<=0||64%sampleStep!=0)
            throw new ArgumentOutOfRangeException(nameof(sampleStep));
        if (group.Count < 1 || group.ConsistencyErrors != 0)
            throw new InvalidOperationException(
                "Region must contain at least two sections and have consistent fixture origins.");
        var index = bundles.ToDictionary(b => b.DescriptorId);
        var cells = new Dictionary<(int X, int Z), List<EsoRecord>>();
        foreach (var (sectionId, position) in group.Origins)
        {
            if (!index.TryGetValue(sectionId, out var section))
                continue;

            // Each fixture-origin increment of 500 maps to a whole 5x5 tile
            // section in the prior independently verified terrain boundary tests.
            foreach (var record in section.Tiles)
            {
                // Height IDs already contain their original local 100-unit tile
                // coordinates. Never rebase the minimum tile to (0,0):
                // partially populated sections can begin at e.g. (0,4).
                int localX = (int)(record.FileId >> 16);
                int localZ = (int)(record.FileId & 0xFFFF);
                var key = (position.X * 5 + localX, position.Z * 5 + localZ);
                if (!cells.TryGetValue(key, out var list))
                    cells[key] = list = new();
                list.Add(record);
            }
        }
        if (cells.Count == 0)
            throw new InvalidOperationException("No terrain cells found for this region.");
        if (cells.Count > 12000)
            throw new InvalidOperationException("Region preview exceeds 12,000 terrain tiles; use streaming.");

        var parsed = new Dictionary<int, LandTile?>();
        LandTile? Read(EsoRecord record)
        {
            if (parsed.TryGetValue(record.Ordinal, out var cached))
                return cached;
            LandTile? tile = null;
            try
            {
                byte[] data = archive.ReadDecoded(record, decoder);
                if (LandTile.TryParse(data, out var candidate) &&
                    candidate != null &&
                    candidate.Width == TileSamples &&
                    candidate.Height == TileSamples)
                    tile = candidate;
            }
            catch (Exception) { /* Bad source record: report it, do not fake a tile. */ }
            parsed[record.Ordinal] = tile;
            return tile;
        }

        var included = new Dictionary<(int X, int Z), LandTile>();
        int ambiguous=0, undecodable=0;
        foreach (var (position, records) in cells)
        {
            LandTile? reference=null;
            bool invalid=false;
            foreach (var record in records)
            {
                var tile=Read(record);
                if (tile==null) { invalid=true; break; }
                if (reference == null) { reference=tile; continue; }

                for (int i=0;i<tile.Samples.Length;i++)
                {
                    float a=reference.Samples[i],b=tile.Samples[i];
                    if (a!=0f && b!=0f && Math.Abs(a-b)>0.0005f)
                    {
                        invalid=true;
                        break;
                    }
                }
                if (invalid)break;
                // Prefer the source containing a nonzero sample where another
                // version only has padding. Never interpolate a missing height.
                if (tile.Samples.Count(v=>v!=0f) >
                    reference.Samples.Count(v=>v!=0f))
                    reference=tile;
            }
            if (invalid)
            {
                if (records.Count>1 && reference!=null)ambiguous++;
                else undecodable++;
                continue;
            }
            if (reference!=null)
                included[position]=reference;
            else undecodable++;
        }
        if(included.Count==0)
            throw new InvalidOperationException("Every candidate terrain cell was ambiguous.");
        if(detailCenter.HasValue)
        {
            var c=detailCenter.Value;
            included=included.Where(e=>
                Math.Abs(e.Key.X-c.X)<=detailRadius &&
                Math.Abs(e.Key.Z-c.Y)<=detailRadius)
                .ToDictionary(e=>e.Key,e=>e.Value);
            if(included.Count==0)
                throw new InvalidOperationException(
                    $"No terrain in 5x5 source patch around tile ({c.X},{c.Y})");
        }

        int mismatchedSeams=0;
        foreach(var (coord,tile) in included)
        {
            foreach(var (next,alongX) in new[]{
                ((coord.X+1,coord.Z),true),((coord.X,coord.Z+1),false)})
            {
                if(!included.TryGetValue(next,out var neighbor))
                    continue;
                for(int i=0;i<TileSamples;i++)
                {
                    float a=alongX?tile.Samples[i*TileSamples+64]:
                        tile.Samples[64*TileSamples+i];
                    float b=alongX?neighbor.Samples[i*TileSamples]:
                        neighbor.Samples[i];
                    if (a!=0f && b!=0f && Math.Abs(a-b)>0.0005f)
                    {mismatchedSeams++;break;}
                }
            }
        }

        float low=float.PositiveInfinity,high=float.NegativeInfinity;
        foreach(var tile in included.Values)
        foreach(float h in tile.Samples)
            if(h!=0f && float.IsFinite(h))
            {
                low=Math.Min(low,h);
                high=Math.Max(high,h);
            }
        if(!float.IsFinite(low)||!float.IsFinite(high))
            throw new InvalidOperationException("Region contains no usable elevation samples.");

        int lowX=included.Keys.Min(p=>p.X),highX=included.Keys.Max(p=>p.X);
        int lowZ=included.Keys.Min(p=>p.Z),highZ=included.Keys.Max(p=>p.Z);
        float centerX=(lowX+highX+1)*TileWorldWidth*0.5f;
        float centerZ=(lowZ+highZ+1)*TileWorldWidth*0.5f;

        int Side=(TileSamples-1)/sampleStep+1;
        var vertices=new List<Vector3>(included.Count*Side*Side);
        // Mesh UV carries the original position in four-heightfield LOD
        // chunks, preserving the game's authored DDS alignment.
        var diffuseUvs=new List<Vector2>(included.Count*Side*Side);
        var originalTileUvs=new List<Vector2>(included.Count*Side*Side);
        var surfaceNormals=new List<Vector3>(included.Count*Side*Side);
        var tangentData=new List<float>(included.Count*Side*Side*4);
        var triangles=new List<int>(included.Count*(Side-1)*(Side-1)*6);
        foreach(var (coord,tile) in included.OrderBy(e=>e.Key.Z).ThenBy(e=>e.Key.X))
        {
            int offset=vertices.Count;
            for(int iz=0;iz<Side;iz++)
            for(int ix=0;ix<Side;ix++)
            {
                int sampleX=ix*sampleStep,sampleZ=iz*sampleStep;
                float elevation=tile.Samples[sampleZ*TileSamples+sampleX];
                float u=sampleX/(float)(TileSamples-1);
                float v=sampleZ/(float)(TileSamples-1);
                diffuseUvs.Add(new Vector2((coord.X+u)/4f,(coord.Z+v)/4f));
                originalTileUvs.Add(new Vector2(coord.X+u,coord.Z+v));
                // True mesh normals from the ORIGINAL full-resolution height
                // samples, not from low-resolution preview triangles.
                int left=Math.Max(0,sampleX-1),right=Math.Min(64,sampleX+1);
                int north=Math.Max(0,sampleZ-1),south=Math.Min(64,sampleZ+1);
                float h=tile.Samples[sampleZ*65+sampleX];
                float hLeft=tile.Samples[sampleZ*65+left];
                float hRight=tile.Samples[sampleZ*65+right];
                float hNorth=tile.Samples[north*65+sampleX];
                float hSouth=tile.Samples[south*65+sampleX];
                if(hLeft==0f)hLeft=h;
                if(hRight==0f)hRight=h;
                if(hNorth==0f)hNorth=h;
                if(hSouth==0f)hSouth=h;
                float dx=(hRight-hLeft)/Math.Max((right-left)*TileWorldWidth/64f,0.0001f);
                float dz=(hSouth-hNorth)/Math.Max((south-north)*TileWorldWidth/64f,0.0001f);
                surfaceNormals.Add(new Vector3(-dx,1f,-dz).Normalized());
                Vector3 tangent=new Vector3(1f,dx,0f).Normalized();
                tangentData.Add(tangent.X);
                tangentData.Add(tangent.Y);
                tangentData.Add(tangent.Z);
                tangentData.Add(-1f); // increasing texture V points toward +Z
                vertices.Add(new Vector3(
                    coord.X*TileWorldWidth +
                        sampleX*TileWorldWidth/(TileSamples-1)-centerX,
                    (elevation==0f?low:elevation)-low,
                    coord.Z*TileWorldWidth +
                        sampleZ*TileWorldWidth/(TileSamples-1)-centerZ));
            }
            for(int iz=0;iz<Side-1;iz++)
            for(int ix=0;ix<Side-1;ix++)
            {
                int sx=ix*sampleStep,sz=iz*sampleStep;
                float a=tile.Samples[sz*TileSamples+sx];
                float b=tile.Samples[sz*TileSamples+sx+sampleStep];
                float c=tile.Samples[(sz+sampleStep)*TileSamples+sx];
                float d=tile.Samples[(sz+sampleStep)*TileSamples+sx+sampleStep];
                if(a==0f||b==0f||c==0f||d==0f)
                    continue;

                int baseIndex=offset+iz*Side+ix;
                triangles.Add(baseIndex);
                triangles.Add(baseIndex+Side);
                triangles.Add(baseIndex+1);
                triangles.Add(baseIndex+1);
                triangles.Add(baseIndex+Side);
                triangles.Add(baseIndex+Side+1);
            }
        }

        var arrays=new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex]=vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal]=surfaceNormals.ToArray();
        arrays[(int)Mesh.ArrayType.Tangent]=tangentData.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV]=diffuseUvs.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2]=originalTileUvs.ToArray();
        arrays[(int)Mesh.ArrayType.Index]=triangles.ToArray();
        var mesh=new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
        var extentX=(highX-lowX+1)*TileWorldWidth;
        var extentZ=(highZ-lowZ+1)*TileWorldWidth;
        float distance=Math.Max(Math.Max(extentX,extentZ)*1.35f,
            Math.Max(1200f,(high-low)*2.5f));
        return new Result(mesh,group.Count,cells.Count,included.Count,
            ambiguous,undecodable,mismatchedSeams,triangles.Count/3,low,high,
            new Vector3(0,(high-low)*0.4f,0),distance);
    }
}
