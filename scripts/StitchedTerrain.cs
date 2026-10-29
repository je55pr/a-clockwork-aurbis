using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace EsoTest;

/// <summary>
/// Construct one indexed Godot mesh from original ESO tiles in two archive-adjacent
/// sectors. No intermediate asset file, invented heights or visual seam smoothing.
/// Shared nonzero vertices MUST agree. 868+869 have 252/252 identical nonzero seam
/// samples in the five edge strips; the remaining strip has a zero-padded edge.
/// </summary>
public static class StitchedTerrain
{
    public readonly record struct Result(ArrayMesh Mesh, int SourceTiles, int DuplicateChecks,
        int MatchingOverlaps, int ZeroPaddingRepairs, int ConflictingOverlaps, float MinHeight, float MaxHeight,
        float SectorWidth, int PaddingTrianglesSkipped);

    private const int TilesPerSector = 5;
    private const int SamplesPerTile = 65;
    private const int Step = SamplesPerTile - 1;
    private const float DefaultSectorWidth = 500f;

    public static Result Create(EsoArchive archive, OodleDecoder decoder,
        IReadOnlyList<TerrainBundle> sectors, params uint[] sectorIds)
    {
        if (sectorIds.Length is < 2 or > 16)
            throw new ArgumentException("Choose between 2 and 16 sectors for stitching");
        var source = sectorIds.Select(id => sectors.FirstOrDefault(s => s.DescriptorId == id)
            ?? throw new InvalidOperationException($"No indexed terrain section {id}")).ToArray();
        int minX = source.Min(s => (int)(s.DescriptorId % TerrainAtlas.WidthInSectors));
        int maxX = source.Max(s => (int)(s.DescriptorId % TerrainAtlas.WidthInSectors));
        int minZ = source.Min(s => (int)(s.DescriptorId / TerrainAtlas.WidthInSectors));
        int maxZ = source.Max(s => (int)(s.DescriptorId / TerrainAtlas.WidthInSectors));
        int width = (maxX-minX+1)*TilesPerSector*Step+1;
        int height = (maxZ-minZ+1)*TilesPerSector*Step+1;
        if ((long)width * height > 6_000_000)
            throw new InvalidOperationException("Requested merged terrain is too large");

        float[] values = new float[width*height];
        bool[] present = new bool[values.Length];
        int duplicates=0, matched=0, repaired=0, conflicts=0, loaded=0;
        foreach (var section in source)
        {
            int sectorX = (int)(section.DescriptorId % TerrainAtlas.WidthInSectors)-minX;
            int sectorZ = (int)(section.DescriptorId / TerrainAtlas.WidthInSectors)-minZ;
            foreach (EsoRecord record in section.Tiles)
            {
                int selectedX=(int)(record.FileId>>16)-section.OriginX;
                int selectedZ=(int)(record.FileId&0xFFFF)-section.OriginY;
                if (selectedX is < 0 or >= TilesPerSector ||
                    selectedZ is < 0 or >= TilesPerSector)
                    continue;
                byte[] raw = archive.ReadDecoded(record, decoder);
                if (!LandTile.TryParse(raw, out LandTile? tile) || tile == null ||
                    tile.Width != SamplesPerTile || tile.Height != SamplesPerTile)
                    throw new InvalidOperationException($"Invalid terrain-height record {record.Ordinal}");

                int tileX=(int)(record.FileId>>16)-section.OriginX;
                int tileZ=(int)(record.FileId&0xFFFF)-section.OriginY;
                if (tileX is < 0 or >= TilesPerSector || tileZ is < 0 or >= TilesPerSector)
                    throw new InvalidOperationException($"Unexpected local tile coordinate {tileX},{tileZ}");

                int offsetX=(sectorX*TilesPerSector+tileX)*Step;
                int offsetZ=(sectorZ*TilesPerSector+tileZ)*Step;
                for(int z=0;z<SamplesPerTile;z++)
                for(int x=0;x<SamplesPerTile;x++)
                {
                    int global=(offsetZ+z)*width+(offsetX+x);
                    float sample=tile.Samples[z*SamplesPerTile+x];
                    if(!present[global])
                    {
                        values[global]=sample;
                        present[global]=true;
                        continue;
                    }
                    duplicates++;
                    float previous=values[global];
                    if(previous==sample || Math.Abs(previous-sample)<=0.0005f)
                    {
                        matched++;
                    }
                    else if(previous==0 || sample==0)
                    {
                        // Zero on only one overlapping edge is an archive padding sentinel.
                        // Use the non-zero, already-present source value without interpolation.
                        if(previous==0) values[global]=sample;
                        repaired++;
                    }
                    else
                    {
                        conflicts++;
                    }
                }
                loaded++;
            }
        }

        if (loaded < TilesPerSector*TilesPerSector*sectorIds.Length)
            throw new InvalidOperationException($"Incomplete terrain: {loaded} source tiles");
        if (conflicts>0)
            throw new InvalidOperationException(
                $"Cannot stitch with {conflicts} conflicting nonzero shared vertices");

        float minHeight=float.PositiveInfinity, maxHeight=float.NegativeInfinity;
        float minNonzero=float.PositiveInfinity;
        int missing=0, zeros=0;
        for(int i=0;i<values.Length;i++){
            if(!present[i]){missing++;continue;}
            if(values[i]==0)zeros++;
            else minNonzero=Math.Min(minNonzero,values[i]);
            minHeight=Math.Min(minHeight,values[i]);
            maxHeight=Math.Max(maxHeight,values[i]);
        }
        if(missing>0)throw new InvalidOperationException($"{missing} missing heightfield vertices");

        // This specific source pair has a few zero-padded cells and its genuine
        // terrain elevation starts far above zero. Skip only triangles which
        // touch that sentinel, leaving original non-zero heights unchanged.
        bool sparsePadding=zeros>0 && zeros<values.Length/20 && minNonzero>100;
        if(sparsePadding)minHeight=minNonzero;

        // An indexed mesh shares the actual vertices along *all* internal tile and
        // sector borders. This eliminates floating-point cracks and mesh seams.
        float vertexSpacing=DefaultSectorWidth/(TilesPerSector*Step);
        var vertices=new Vector3[values.Length];
        var normals=new Vector3[values.Length];
        var uvs=new Vector2[values.Length];
        for(int z=0;z<height;z++)
        for(int x=0;x<width;x++)
        {
            int i=z*width+x;
            float y=values[i]-minHeight;
            vertices[i]=new Vector3(
                (x-(width-1)/2f)*vertexSpacing,y,(z-(height-1)/2f)*vertexSpacing);
            uvs[i]=new Vector2((float)x/Step,(float)z/Step);
            float hW=values[z*width+Math.Max(0,x-1)];
            float hE=values[z*width+Math.Min(width-1,x+1)];
            float hN=values[Math.Max(0,z-1)*width+x];
            float hS=values[Math.Min(height-1,z+1)*width+x];
            if(sparsePadding){
                if(hW==0)hW=values[i]; if(hE==0)hE=values[i];
                if(hN==0)hN=values[i]; if(hS==0)hS=values[i];
            }
            normals[i]=new Vector3(
                -(hE-hW)/(x is 0 || x==width-1?vertexSpacing:2*vertexSpacing),
                1,
                -(hS-hN)/(z is 0 || z==height-1?vertexSpacing:2*vertexSpacing)).Normalized();
        }

        var indices=new List<int>((width-1)*(height-1)*6);
        int skipped=0;
        for(int z=0;z<height-1;z++)
        for(int x=0;x<width-1;x++)
        {
            int p=z*width+x;
            if(sparsePadding && (values[p]==0||values[p+1]==0||
                values[p+width]==0||values[p+width+1]==0))
            {
                skipped+=2;
                continue;
            }
            indices.Add(p);indices.Add(p+width);indices.Add(p+1);
            indices.Add(p+1);indices.Add(p+width);indices.Add(p+width+1);
        }
        var arrays=new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex]=vertices;
        arrays[(int)Mesh.ArrayType.Normal]=normals;
        arrays[(int)Mesh.ArrayType.TexUV]=uvs;
        arrays[(int)Mesh.ArrayType.Index]=indices.ToArray();
        var mesh=new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
        return new Result(mesh,loaded,duplicates,matched,repaired,conflicts,minHeight,maxHeight,DefaultSectorWidth,skipped);
    }
}
