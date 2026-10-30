using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace EsoTest;

/// <summary>Samples the original local 65x65 ESO terrain, without physics colliders.</summary>
public sealed class PopulationTerrainSampler
{
    private readonly Dictionary<(int x,int z),LandTile> _tiles=new();
    private readonly float _lowestHeight;
    private readonly int _tileX,_tileZ;
    private readonly float? _sourceWaterHeight;

    public PopulationTerrainSampler(EsoArchive archive,OodleDecoder decoder,TerrainBundle bundle,
        float lowestHeight,int tileX,int tileZ,float? sourceWaterHeight)
    {
        _tileX=tileX;_tileZ=tileZ;_lowestHeight=lowestHeight;
        _sourceWaterHeight=sourceWaterHeight;
        foreach(var record in bundle.Tiles)
        {
            int x=(int)(record.FileId>>16),z=(int)(record.FileId&0xffff);
            if(x<tileX-3||x>tileX+4||z<tileZ-3||z>tileZ+4)continue;
            if(LandTile.TryParse(archive.ReadDecoded(record,decoder),out var terrain)&&
                terrain is { Width:65,Height:65 })
                _tiles[(x,z)]=terrain;
        }
    }

    private float? SampleSource(float x,float z)
    {
        float globalX=(_tileX+0.5f)*100f+x;
        float globalZ=(_tileZ+0.5f)*100f+z;
        int tx=Mathf.FloorToInt(globalX/100f);
        int tz=Mathf.FloorToInt(globalZ/100f);
        if(!_tiles.TryGetValue((tx,tz),out var tile))return null;
        float u=Mathf.Clamp((globalX-tx*100f)/100f,0f,1f)*64f;
        float v=Mathf.Clamp((globalZ-tz*100f)/100f,0f,1f)*64f;
        int ix=Math.Min(63,Mathf.FloorToInt(u));
        int iz=Math.Min(63,Mathf.FloorToInt(v));
        float fracU=u-ix,fracV=v-iz;
        float a=tile.Samples[iz*65+ix],b=tile.Samples[iz*65+ix+1];
        float c=tile.Samples[(iz+1)*65+ix],d=tile.Samples[(iz+1)*65+ix+1];
        if(a==0||b==0||c==0||d==0)return null; // source padding
        // Match LandTile.BuildMesh: diagonal from (x+1,z) to (x,z+1).
                // Bilinear height is not the surface of the actual rendered triangles.
                if(fracU+fracV<=1f)
                    return a+(b-a)*fracU+(c-a)*fracV;
                return d+(c-d)*(1f-fracU)+(b-d)*(1f-fracV);
    }

    public float? GroundAt(float x,float z)
    {
        var center=SampleSource(x,z);
        if(center is null)return null;
        if(_sourceWaterHeight is float water && center.Value<=water+0.9f)return null;
        float low=center.Value,high=center.Value;
        foreach(var delta in new[]{new Vector2(-1.2f,0f),new Vector2(1.2f,0f),
            new Vector2(0f,-1.2f),new Vector2(0f,1.2f)})
        {
            var sample=SampleSource(x+delta.X,z+delta.Y);
            if(sample is null)return null;
            low=Math.Min(low,sample.Value);high=Math.Max(high,sample.Value);
        }
        if(high-low>1.15f)return null; // steep/irregular source ground
        return center.Value-_lowestHeight;
    }

    public int SourceTiles=>_tiles.Count;
}
