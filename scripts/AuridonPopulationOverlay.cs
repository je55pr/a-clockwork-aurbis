using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace EsoTest;

/// <summary>2D context for the entire collected Auridon NPC population.
/// Place-anchored estimates, never actual NPC spawn coordinates.</summary>
public partial class AuridonPopulationOverlay : Control
{
    private readonly List<(Vector2 Position,bool DirectMarker)> _dots=new();
    public int MappedCount=>_dots.Count;
    public int TotalCount {get;private set;}
    public override void _Ready()
    {
        CustomMinimumSize=new Vector2(512,512);
        MouseFilter=MouseFilterEnum.Ignore;
        LoadData();
        QueueRedraw();
    }
    private void LoadData()
    {
        string file=ProjectSettings.GlobalizePath("res://.research/auridon-population.json");
        if(!File.Exists(file))return;
        using var doc=JsonDocument.Parse(File.ReadAllText(file));
        if(doc.RootElement.GetProperty("schemaVersion").GetInt32()!=1)return;
        TotalCount=doc.RootElement.GetProperty("population").GetInt32();
        foreach(var person in doc.RootElement.GetProperty("people").EnumerateArray())
        {
            if(!person.TryGetProperty("map",out var point)||
                point.ValueKind!=JsonValueKind.Object)continue;
            float x=point.GetProperty("x").GetSingle()/1000000f;
            float y=1f-point.GetProperty("y").GetSingle()/1000000f;
            if(x<0f||x>1f||y<0f||y>1f)continue;
            // Small repeatable scatter prevents overlapping source-place markers.
            int code=PopulationSpawnPlanner.StableHash(person.GetProperty("id").GetString()??"");
            float dx=((code%101)/100f-0.5f)*0.012f;
            float dy=(((code/101)%101)/100f-0.5f)*0.012f;
            var v=new Vector2(Mathf.Clamp(x+dx,0.01f,0.99f),
                Mathf.Clamp(y+dy,0.01f,0.99f));
            bool direct=point.GetProperty("kind").GetString()=="uesp_map_marker";
            _dots.Add((v,direct));
        }
        GD.Print($"AURIDON MAP POPULATION: {_dots.Count}/{TotalCount} source-anchored NPCs");
    }
    public override void _Draw()
    {
        foreach(var dot in _dots)
        {
            var p=new Vector2(dot.Position.X*512f,dot.Position.Y*512f);
            DrawCircle(p,3.5f,new Color(0.08f,0.10f,0.12f,0.72f));
            DrawCircle(p,2.35f,dot.DirectMarker
                ? new Color(1f,0.78f,0.20f,0.88f)
                : new Color(0.60f,0.82f,1f,0.85f));
        }
    }
}
