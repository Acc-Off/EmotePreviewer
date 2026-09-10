using System.Numerics;
using EmotePreviewer.Core.Model;

namespace EmotePreviewer.Core.Anim;

/// <summary>
/// Turns a <see cref="ClipSample"/> (local bone transforms) into world-space bone transforms
/// by walking the skeleton hierarchy. Bones not present in the sample keep their bind pose.
/// </summary>
public sealed class PoseSolver
{
    public SkeletonDef Skeleton { get; }
    public Matrix4x4[] World { get; }
    public Vector3[] LocalTranslation { get; }
    public Quaternion[] LocalRotation { get; }
    public Vector3[] LocalScale { get; }

    /// <summary>Apply root-motion tracks (5/6) to the root bone. Off by default so the figure stays in place.</summary>
    public bool ApplyRootMotion { get; set; }

    /// <summary>
    /// Leave the facial rig (<c>FB_*</c> / <c>FACIAL_*</c>) in its bind pose. In-game the facial animation layer drives those bones and
    /// overrides body clips; community clips converted from other tools often carry garbage for them (mirrored, offset
    /// lip and brow positions) that would otherwise deform the face.
    /// </summary>
    public bool IgnoreFacialBones { get; set; } = true;

    /// <summary>Whether a track of the given kind on a bone changes the pose (bone DOF flags and the facial rule).</summary>
    public bool Drives(int boneIndex, AnimTrack track)
    {
        var b = Skeleton.Bones[boneIndex];
        if (IgnoreFacialBones && b.IsFacial) return false;
        return track switch
        {
            AnimTrack.BonePosition => (b.Dofs & BoneDofs.Translation) != 0,
            AnimTrack.BoneRotation => (b.Dofs & BoneDofs.Rotation) != 0,
            AnimTrack.BoneScale => (b.Dofs & BoneDofs.Scale) != 0,
            _ => true,
        };
    }

    /// <summary>
    /// The freemode skeletons carry "roll" helper bones (RB_L_ThighRoll, RB_R_ThighRoll) that are not animated
    /// by most clips; copying the corresponding thigh rotation keeps them from sticking out. Purely cosmetic.
    /// </summary>
    public bool FixThighRollBones { get; set; } = true;

    static readonly (ushort roll, ushort source)[] ThighRollPairs = { (23639, 58271), (6442, 51826) };

    public PoseSolver(SkeletonDef skeleton)
    {
        Skeleton = skeleton;
        var n = skeleton.Bones.Count;
        World = new Matrix4x4[n];
        LocalTranslation = new Vector3[n];
        LocalRotation = new Quaternion[n];
        LocalScale = new Vector3[n];
        Reset();
        Solve();
    }

    public void Reset()
    {
        for (int i = 0; i < Skeleton.Bones.Count; i++)
        {
            var b = Skeleton.Bones[i];
            LocalTranslation[i] = b.Translation;
            LocalRotation[i] = b.Rotation;
            LocalScale[i] = b.Scale;
        }
    }

    public void Apply(ClipSample sample)
    {
        Reset();
        // Only the channels a bone exposes are driven; the others keep the bind value (per axis for translation / scale).
        foreach (var (tag, v) in sample.Translations)
        {
            var i = Skeleton.IndexOfTag(tag);
            if (i < 0 || !Drives(i, AnimTrack.BonePosition)) continue;
            var d = Skeleton.Bones[i].Dofs; var bind = Skeleton.Bones[i].Translation;
            LocalTranslation[i] = new Vector3((d & BoneDofs.TransX) != 0 ? v.X : bind.X, (d & BoneDofs.TransY) != 0 ? v.Y : bind.Y, (d & BoneDofs.TransZ) != 0 ? v.Z : bind.Z);
        }
        foreach (var (tag, q) in sample.Rotations) { var i = Skeleton.IndexOfTag(tag); if (i >= 0 && Drives(i, AnimTrack.BoneRotation)) LocalRotation[i] = q; }
        foreach (var (tag, s) in sample.Scales)
        {
            var i = Skeleton.IndexOfTag(tag);
            if (i < 0 || !Drives(i, AnimTrack.BoneScale)) continue;
            var d = Skeleton.Bones[i].Dofs; var bind = Skeleton.Bones[i].Scale;
            LocalScale[i] = new Vector3((d & BoneDofs.ScaleX) != 0 ? s.X : bind.X, (d & BoneDofs.ScaleY) != 0 ? s.Y : bind.Y, (d & BoneDofs.ScaleZ) != 0 ? s.Z : bind.Z);
        }

        if (FixThighRollBones)
        {
            foreach (var (roll, source) in ThighRollPairs)
            {
                int ri = Skeleton.IndexOfTag(roll), si = Skeleton.IndexOfTag(source);
                if (ri >= 0 && si >= 0 && !sample.Rotations.ContainsKey(roll)) LocalRotation[ri] = LocalRotation[si];
            }
        }

        if (ApplyRootMotion)
        {
            var root = Skeleton.IndexOfTag(0);
            if (root >= 0)
            {
                LocalTranslation[root] = sample.RootMotionTranslation + Vector3.Transform(LocalTranslation[root], sample.RootMotionRotation);
                LocalRotation[root] = sample.RootMotionRotation * LocalRotation[root];
            }
        }
        Solve();
    }

    void Solve()
    {
        foreach (var i in Skeleton.EvaluationOrder)
        {
            var local = Matrix4x4.CreateScale(LocalScale[i]) * Matrix4x4.CreateFromQuaternion(LocalRotation[i]) * Matrix4x4.CreateTranslation(LocalTranslation[i]);
            var p = Skeleton.Bones[i].ParentIndex;
            World[i] = p >= 0 ? local * World[p] : local;
        }
    }

    public Vector3 WorldPosition(int boneIndex) => World[boneIndex].Translation;
}
