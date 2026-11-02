using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace EsoTest;

/// <summary>
/// Godot-side reader for verified, original ESO GR2 skeletal data exported
/// by ModelGeometry --inspect-rig. The proprietary asset data lives only
/// under .research. This class never loads Granny/Oodle DLLs into Godot.
/// A skin's vertex indices address the source mesh's BINDING TABLE, not
/// the skeleton's bone order. Inverse-world matrices provide true bind poses.
/// </summary>
internal sealed class EsoSourceRig
{
    internal sealed record Bone(string Name,int Parent,Transform3D InverseWorld);
    internal readonly string MeshName;
    internal readonly IReadOnlyList<Bone> Bones;
    internal readonly string[] BindingNames;
    internal readonly int[] Indices;
    internal readonly float[] Weights;
    internal int VertexCount => Indices.Length/4;

    private EsoSourceRig(string meshName,IReadOnlyList<Bone> bones,
        string[] bindings,int[] indices,float[] weights)
    {
        MeshName=meshName;Bones=bones;BindingNames=bindings;
        Indices=indices;Weights=weights;
    }

    private static Transform3D ReadMatrix(JsonElement element)
    {
        var m=element.EnumerateArray().Select(x=>x.GetSingle()).ToArray();
        if(m.Length!=16 || m.Any(x=>!float.IsFinite(x)))
            throw new InvalidDataException("Invalid source inverse-world matrix");
        if(Math.Abs(m[3])+Math.Abs(m[7])+Math.Abs(m[11])>0.0005f ||
            Math.Abs(m[15]-1)>0.0005f)
            throw new InvalidDataException("Source inverse-world matrix is not affine");
        var basis=new Basis(new Vector3(m[0],m[1],m[2]),
            new Vector3(m[4],m[5],m[6]),
            new Vector3(m[8],m[9],m[10]));
        if(Math.Abs(basis.Determinant())<0.000001f)
            throw new InvalidDataException("Singular source bind matrix");
        return new Transform3D(basis,new Vector3(m[12],m[13],m[14]));
    }

    internal static EsoSourceRig Read(string path)
    {
        if(new FileInfo(path).Length is <100 or >8_000_000)
            throw new InvalidDataException("Source rig cache size out of range");
        using var document=JsonDocument.Parse(File.ReadAllBytes(path));
        var root=document.RootElement;
        var rigMeshes=root.GetProperty("RigMeshes");
        var skeletons=root.GetProperty("RigSkeletons");
        if(rigMeshes.GetArrayLength()!=1 || skeletons.GetArrayLength()!=1)
            throw new InvalidDataException("Expected a single source mesh and skeleton");
        var mesh=rigMeshes[0];
        var bones=new List<Bone>();
        foreach(var bone in skeletons[0].GetProperty("Bones").EnumerateArray())
        {
            if(bones.Count>=256)throw new InvalidDataException("Too many bones");
            int parent=bone.GetProperty("Parent").GetInt32();
            if(parent>=bones.Count || parent< -1)
                throw new InvalidDataException("Invalid source bone parent ordering");
            string name=bone.GetProperty("Name").GetString()??"";
            if(name.Length==0 || bones.Any(b=>b.Name==name))
                throw new InvalidDataException("Duplicate/empty source bone");
            bones.Add(new Bone(name,parent,
                ReadMatrix(bone.GetProperty("InverseWorld"))));
        }
        var names=mesh.GetProperty("BindingNames")
            .EnumerateArray().Select(x=>x.GetString()??"").ToArray();
        if(names.Length is <1 or >256 ||
            names.Any(name=>!bones.Any(b=>b.Name==name)))
            throw new InvalidDataException("Source mesh binding not in skeleton");
        int vertices=mesh.GetProperty("Vertices").GetArrayLength();
        if(vertices is <3 or >100000)
            throw new InvalidDataException("Source vertex count outside supported range");
        var indices=new int[vertices*4];
        var weights=new float[vertices*4];
        int v=0;
        foreach(var vertex in mesh.GetProperty("Vertices").EnumerateArray())
        {
            var ids=vertex.GetProperty("Indices").EnumerateArray().Select(x=>x.GetInt32()).ToArray();
            var values=vertex.GetProperty("Weights").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
            if(ids.Length!=4 || values.Length!=4)
                throw new InvalidDataException("Expected 4 source bone influences");
            float sum=0;
            for(int k=0;k<4;k++)
            {
                if(ids[k]<0 || ids[k]>=names.Length ||
                    !float.IsFinite(values[k]) || values[k]<0 || values[k]>1.01f)
                    throw new InvalidDataException("Invalid source vertex bone influence");
                indices[v*4+k]=ids[k];weights[v*4+k]=values[k];
                sum+=values[k];
            }
            if(Math.Abs(sum-1)>0.03f)
                throw new InvalidDataException("Non-normalized vertex bone weights");
            v++;
        }
        return new EsoSourceRig(mesh.GetProperty("Name").GetString()??"",
            bones,names,indices,weights);
    }

    internal void AddInfluences(Godot.Collections.Array arrays,int vertices)
    {
        if(vertices!=VertexCount)
            throw new InvalidDataException($"Source rig {VertexCount} vertices != mesh {vertices}");
        arrays[(int)Mesh.ArrayType.Bones]=Indices;
        arrays[(int)Mesh.ArrayType.Weights]=Weights;
    }

    internal (Skeleton3D Skeleton,Skin Skin) BuildSkeleton()
    {
        var skeleton=new Skeleton3D{Name="ESOCustomizationRig"};
        // Godot computes bone rest locally. Granny supplies one inverse-world
        // transform per bone; compose local rest relative to its source parent.
        var world=Bones.Select(b=>b.InverseWorld.AffineInverse()).ToArray();
        for(int i=0;i<Bones.Count;i++)
        {
            skeleton.AddBone(Bones[i].Name);
            int parent=Bones[i].Parent;
            if(parent>=0)skeleton.SetBoneParent(i,parent);
            skeleton.SetBoneRest(i,parent<0
                ? world[i] : world[parent].AffineInverse()*world[i]);
        }
        // Setting bone rests alone does not initialize Godot's editable pose.
        // Without this reset, GPU skinning applies inverse binds against an
        // identity pose and collapses character geometry toward the origin.
        skeleton.ResetBonePoses();
        for(int i=0;i<Bones.Count;i++)
        {
            var bindTest=skeleton.GetBoneGlobalPose(i)*Bones[i].InverseWorld;
            if(bindTest.Origin.Length()>0.002f ||
               !bindTest.Basis.IsEqualApprox(Basis.Identity))
                throw new InvalidDataException($"Source rest/bind mismatch at {Bones[i].Name}");
        }
        var skin=new Skin();
        for(int i=0;i<BindingNames.Length;i++)
        {
            int boneIndex=-1;
            for(int b=0;b<Bones.Count;b++)
                if(Bones[b].Name==BindingNames[i]){boneIndex=b;break;}
            if(boneIndex<0)throw new InvalidDataException("Unmatched source binding");
            // Skin indices match local source GR2 binding indices.
            skin.AddBind(boneIndex,Bones[boneIndex].InverseWorld);
        }
        return (skeleton,skin);
    }
}
