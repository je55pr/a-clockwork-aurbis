using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace EsoTest;

/// <summary>
/// Validated native Granny animation samples. The original PC/NPC animation
/// rig uses 'mshBn_*'; ESO customization garments use 'defBn_*'.
/// Only matching bone suffixes are provisionally retargeted by LOCAL
/// rotation DELTAS relative to the clip's first frame. This is not yet the
/// game's authoritative character animation retargeter or blended state graph.
/// Source animation bytes live under ignored .research, never in Git.
/// </summary>
internal sealed class EsoSourceAnimationClip
{
    internal sealed record Frame(float Time,Quaternion Orientation);
    internal sealed record Track(string Target,int BoneIndex,Quaternion RestRotation,Frame[] Frames);
    internal readonly string Name;
    internal readonly float Duration;
    internal readonly int FrameRate;
    internal readonly IReadOnlyList<Track> Tracks;
    internal int MappedBoneCount=>Tracks.Count;

    private EsoSourceAnimationClip(string name,float duration,int fps,IReadOnlyList<Track> tracks)
    {
        Name=name;Duration=duration;FrameRate=fps;Tracks=tracks;
    }
    private static Quaternion ReadQ(JsonElement array)
    {
        if(array.GetArrayLength()!=4)
            throw new InvalidDataException("Expected 4D animation quaternion");
        var q=new Quaternion(array[0].GetSingle(),array[1].GetSingle(),
            array[2].GetSingle(),array[3].GetSingle());
        if(!q.IsFinite() || Math.Abs(q.Length()-1)>0.1f)
            throw new InvalidDataException("Invalid original quaternion");
        return q.Normalized();
    }
    internal static EsoSourceAnimationClip Read(string file,Skeleton3D skeleton)
    {
        if(new FileInfo(file).Length is <400 or >12_000_000)
            throw new InvalidDataException("Native animation sample cache invalid size");
        using var doc=JsonDocument.Parse(File.ReadAllBytes(file));
        var root=doc.RootElement;
        float duration=root.GetProperty("DurationSeconds").GetSingle();
        int fps=root.GetProperty("FramesPerSecond").GetInt32();
        string name=root.GetProperty("Name").GetString()??"";
        if(duration is <0.1f or >60f || fps is <2 or >60 || name.Length==0)
            throw new InvalidDataException("Native source clip missing metadata");
        var tracks=new List<Track>();
        foreach(var record in root.GetProperty("Tracks").EnumerateArray())
        {
            string rawName=record.GetProperty("Name").GetString()??"";
            if(!rawName.StartsWith("mshBn_",StringComparison.Ordinal))continue;
            string canonical="defBn_"+rawName["mshBn_".Length..];
            int index=skeleton.FindBone(canonical);
            if(index<0)continue;
            var frames=new List<Frame>();
            foreach(var frame in record.GetProperty("Frames").EnumerateArray())
            {
                float time=frame.GetProperty("Time").GetSingle();
                if(!float.IsFinite(time)||time<0 || time>duration+0.0001f)
                    throw new InvalidDataException("Invalid frame timestamp");
                frames.Add(new Frame(time,ReadQ(frame.GetProperty("Orientation"))));
            }
            if(frames.Count<2 || frames.Count>4000)continue;
            // Source track rotations live on the 'mshBn' animation skeleton.
            // Deltas are explicit experimental retargeting, not absolute rests.
            tracks.Add(new Track(canonical,index,
                skeleton.GetBoneRest(index).Basis.GetRotationQuaternion().Normalized(),
                frames.ToArray()));
        }
        if(tracks.Count==0)
            throw new InvalidDataException("No source bone names match the customization rig");
        return new EsoSourceAnimationClip(name,duration,fps,tracks);
    }
    internal void Apply(Skeleton3D skeleton,float seconds)
    {
        float sample=seconds%Duration;
        if(sample<0)sample+=Duration;
        foreach(var track in Tracks)
        {
            var frames=track.Frames;
            int at=Math.Clamp((int)(sample*FrameRate),0,frames.Length-2);
            var start=frames[at];var next=frames[at+1];
            float blend=Mathf.Clamp((sample-start.Time)/
                Math.Max(0.00001f,next.Time-start.Time),0,1);
            var current=start.Orientation.Slerp(next.Orientation,blend);
            var first=frames[0].Orientation;
            var delta=(current*first.Inverse()).Normalized();
            // These are real ESO clip rotations, but this is not ESO's
            // retargeter. Restrict the unverified coordinate-basis transfer
            // to small, visibly testable limb motion, rather than rotating
            // the entire torso or root into impossible poses.
            float strength=track.Target.Contains("upperArm",StringComparison.OrdinalIgnoreCase)
                || track.Target.Contains("thigh",StringComparison.OrdinalIgnoreCase)
                || track.Target.Contains("shin",StringComparison.OrdinalIgnoreCase)
                ? 0.32f : track.Target.Contains("clavicle",StringComparison.OrdinalIgnoreCase)
                ? 0.18f : 0f;
            // Godot's bone pose rotation is a LOCAL absolute rotation, not
            // a delta from its rest. Preserve the source rest orientation.
            var additive=Quaternion.Identity.Slerp(delta,strength).Normalized();
            skeleton.SetBonePoseRotation(track.BoneIndex,
                (track.RestRotation*additive).Normalized());
        }
    }
}
