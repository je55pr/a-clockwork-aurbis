using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace EsoTest;

/// <summary>
/// Godot's existing 2D Auridon map can replay the headless simulation's
/// actual courier assignments. Straight-line marker interpolation is only
/// a map preview, NOT a claim about ESO road or navigation geometry.
/// Export with SettlementRunner --courier-preview .research/auridon-courier-preview.json.
/// </summary>
public partial class AuridonCourierOverlay : Control
{
    private readonly record struct Route(
        Vector2 From,Vector2 To,long Pickup,long Arrival,
        string Courier,string Commodity,bool Completed);
    private readonly List<Route> _routes = new();
    private double _playhead,_start,_end;

    public int RouteCount=>_routes.Count;

    public override void _Ready()
    {
        CustomMinimumSize=new Vector2(512,512);
        MouseFilter=MouseFilterEnum.Ignore;
        LoadPreview();
        if(_routes.Count>0)
            GD.Print($"AURIDON COURIERS: replaying {_routes.Count} source-anchored shipments");
        QueueRedraw();
    }

    private void LoadPreview()
    {
        var file=ProjectSettings.GlobalizePath(
            "res://.research/auridon-courier-preview.json");
        if(!File.Exists(file))return;
        using var document=JsonDocument.Parse(File.ReadAllText(file));
        var root=document.RootElement;
        if(root.GetProperty("schemaVersion").GetInt32()!=1)
            throw new InvalidDataException("Unknown courier preview schema");
        foreach(var j in root.GetProperty("journeys").EnumerateArray())
        {
            if(!Coordinate(j,"fromMapX",out var fx) ||
               !Coordinate(j,"fromMapY",out var fy) ||
               !Coordinate(j,"toMapX",out var tx) ||
               !Coordinate(j,"toMapY",out var ty) ||
               !Timestamp(j,"pickupMinute",out long pickup))continue;
            bool complete=Timestamp(j,"deliveredMinute",out long arrival);
            if(!complete && !Timestamp(j,"expectedDeliveryMinute",out arrival))
                continue;
            if(arrival<=pickup)continue;
            var from=new Vector2(fx,1f-fy)*512f;
            var to=new Vector2(tx,1f-ty)*512f;
            if(!Inside(from)||!Inside(to))continue;
            _routes.Add(new Route(from,to,pickup,arrival,
                j.GetProperty("courierName").GetString()??"Courier",
                j.GetProperty("commodity").GetString()??"goods",complete));
        }
        if(_routes.Count==0)return;
        _start=_routes.Min(x=>x.Pickup);
        _end=_routes.Max(x=>x.Arrival);
        _playhead=_start;
    }

    private static bool Timestamp(JsonElement value,string property,out long result)
    {
        if(value.TryGetProperty(property,out var e) &&
           e.ValueKind==JsonValueKind.Number && e.TryGetInt64(out result))
            return true;
        result=0;
        return false;
    }
    private static bool Coordinate(JsonElement value,string property,out float result)
    {
        result=0;
        if(!value.TryGetProperty(property,out var v) ||
           v.ValueKind!=JsonValueKind.Number)return false;
        float raw=v.GetSingle();
        if(!float.IsFinite(raw)||raw<0f||raw>1000000f)return false;
        result=raw/1000000f;
        return true;
    }
    private static bool Inside(Vector2 p) =>
        p.X is >=0f and <=512f && p.Y is >=0f and <=512f;

    public override void _Process(double delta)
    {
        if(_routes.Count==0)return;
        // Replay the recorded interval over about 45 real seconds.
        _playhead+=delta*Math.Max(1d,(_end-_start)/45d);
        if(_playhead>_end+15d)_playhead=_start;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if(_routes.Count==0)return;
        foreach(var route in _routes)
        {
            var line=new Color(0.19f,0.86f,0.85f,0.27f);
            DrawLine(route.From,route.To,line,1.2f,true);
            if(_playhead<route.Pickup || _playhead>route.Arrival)continue;
            float fraction=Mathf.Clamp(
                (float)((_playhead-route.Pickup)/(route.Arrival-route.Pickup)),
                0f,1f);
            var pos=route.From.Lerp(route.To,fraction);
            DrawCircle(pos,5.5f,new Color(0.04f,0.12f,0.15f,0.90f));
            DrawCircle(pos,3.6f,route.Completed
                ?new Color(0.36f,1f,0.85f,1f)
                :new Color(1f,0.80f,0.38f,1f));
        }
        var elapsed=TimeSpan.FromMinutes(_playhead);
        DrawString(ThemeDB.FallbackFont,new Vector2(8,501),
            $"Courier routes: {_routes.Count}  |  day {(int)(_playhead/1440)+1}, {elapsed.Hours:00}:{elapsed.Minutes:00}",
            HorizontalAlignment.Left,-1,12,new Color(0.86f,0.98f,0.96f));
    }
}
