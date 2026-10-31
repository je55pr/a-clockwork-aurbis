using System;
using Godot;

namespace EsoTest;

/// <summary>
/// Source-driven walk-in-place test for ESO customization parts.
/// Real PC_Walk thigh and shin curve values determine timing and movement,
/// but 'mshBn' rotation axes must be transferred to the anatomical
/// 'defBn' hinges instead of being used as raw customization quaternions.
/// Not ESO's native, fully retargeted animation or locomotion state graph.
/// </summary>
internal static class EsoRigLabGait
{
    internal const float DurationSeconds=0.9666667f;

    // Apply a world-space anatomical bend, converting it back into the
    // individual source part's local bone frame. Preserve its bind rotation.
    private static void Bend(Skeleton3D skeleton,string boneName,Vector3 axis,float radians)
    {
        int bone=skeleton.FindBone(boneName);
        if(bone<0)return;
        int parent=skeleton.GetBoneParent(bone);
        Quaternion parentWorld=parent>=0
            ? skeleton.GetBoneGlobalPose(parent).Basis.GetRotationQuaternion().Normalized()
            : Quaternion.Identity;
        Quaternion bend=new Quaternion(axis,radians);
        Quaternion local=(parentWorld.Inverse()*bend*parentWorld).Normalized();
        Quaternion rest=skeleton.GetBoneRest(bone).Basis.GetRotationQuaternion().Normalized();
        skeleton.SetBonePoseRotation(bone,(local*rest).Normalized());
    }

    internal static void Apply(Skeleton3D skeleton,
        EsoSourceAnimationClip originalClip,float time,float motion=1f)
    {
        motion=Mathf.Clamp(motion,0,1);
        skeleton.ResetBonePoses();
        if(motion<=0)return;
        // The source animation's thigh movement is primarily around its
        // rig's local Y. Map its measured signed swing onto the customization
        // model's world-space sagittal hinge (X), with conservative gains.
        float left=Mathf.Clamp(
            originalClip.OriginalJointSwing("mshBn_thigh_L",time)*0.32f,
            -0.39f,0.39f)*motion;
        float right=Mathf.Clamp(
            originalClip.OriginalJointSwing("mshBn_thigh_R",time)*0.32f,
            -0.39f,0.39f)*motion;
        float leftKnee=Mathf.Clamp(Mathf.Abs(
            originalClip.OriginalJointSwing("mshBn_shin_L",time))*0.18f,
            0f,0.16f)*motion;
        float rightKnee=Mathf.Clamp(Mathf.Abs(
            originalClip.OriginalJointSwing("mshBn_shin_R",time))*0.18f,
            0f,0.16f)*motion;
        // All source parts are separate skinned meshes. Applying the same
        // bounded anatomical movements to each part's own skeleton keeps
        // the body assembled instead of pulling its limbs in different axes.
        Bend(skeleton,"defBn_thigh_L",Vector3.Right,left);
        Bend(skeleton,"defBn_thigh_R",Vector3.Right,right);
        Bend(skeleton,"Leg_null_L",Vector3.Right,-leftKnee);
        Bend(skeleton,"Leg_null_R",Vector3.Right,-rightKnee);
        Bend(skeleton,"defBn_upperArm_L",Vector3.Right,-right*0.46f);
        Bend(skeleton,"defBn_upperArm_R",Vector3.Right,-left*0.46f);
        Bend(skeleton,"defBn_spine02",Vector3.Up,0.015f*(left-right));
    }
}
