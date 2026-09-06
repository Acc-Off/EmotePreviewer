using System.Numerics;
using EmotePreviewer.Core.Model;

namespace EmotePreviewer.Core.Anim;

/// <summary>
/// A clip sampled at a fixed frame rate into local bone transforms, ready to be streamed to the browser.
/// Layout of <see cref="Data"/> (little-endian float32 when serialised):
/// <code>
/// [frame][bone] × (px, py, pz, qx, qy, qz, qw)     Frames × BoneCount × 7
/// [frame]       × (px, py, pz, qx, qy, qz, qw)     Frames × 7                (only when HasRootMotion)
/// </code>
/// Frame <c>i</c> is at time <c>i / Fps</c> seconds; the last frame is clamped to just before <see cref="Duration"/>
/// so non-looping clips end on their final pose instead of wrapping to the first frame.
/// </summary>
public sealed class BakedClip
{
    public const int LayoutVersion = 2;
    public const int FloatsPerBone = 7;

    public required int Fps { get; init; }
    public required int Frames { get; init; }
    public required float Duration { get; init; }
    public required int BoneCount { get; init; }
    public required bool HasRootMotion { get; init; }
    public required float[] Data { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    /// <summary>Bone indices that the clip actually animates (position, rotation or scale track).</summary>
    public required IReadOnlyList<int> AnimatedBones { get; init; }

    public int BoneFloats => Frames * BoneCount * FloatsPerBone;
    public int TotalFloats => BoneFloats + (HasRootMotion ? Frames * FloatsPerBone : 0);

    public ReadOnlySpan<float> Bone(int frame, int bone) => Data.AsSpan((frame * BoneCount + bone) * FloatsPerBone, FloatsPerBone);
    public ReadOnlySpan<float> RootMotion(int frame) => HasRootMotion ? Data.AsSpan(BoneFloats + frame * FloatsPerBone, FloatsPerBone) : ReadOnlySpan<float>.Empty;

    /// <summary>Serialises <see cref="Data"/> as little-endian float32.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[TotalFloats * sizeof(float)];
        var span = Data.AsSpan(0, TotalFloats);
        if (BitConverter.IsLittleEndian) System.Runtime.InteropServices.MemoryMarshal.AsBytes(span).CopyTo(bytes);
        else for (int i = 0; i < span.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), span[i]);
        return bytes;
    }
}

/// <summary>Samples an <see cref="IClip"/> through the <see cref="PoseSolver"/> into a <see cref="BakedClip"/>.</summary>
public static class ClipBaker
{
    public const int MaxFps = 60;
    public const int DefaultFps = 30;
    /// <summary>Upper bound for the bone data of one clip; the frame rate is lowered for very long clips to stay under it.</summary>
    public const long MaxBoneBytes = 12 * 1024 * 1024;

    public sealed record Options(int? Fps = null, bool FixThighRollBones = true, long MaxBoneBytes = MaxBoneBytes);

    public static BakedClip Bake(IClip clip, SkeletonDef skeleton, Options? options = null)
    {
        options ??= new Options();
        var warnings = new List<string>();
        var duration = clip.Duration;
        if (!(duration > 0f) || !float.IsFinite(duration))
        {
            warnings.Add($"clip has no duration ({clip.Duration}); baked as a single pose");
            duration = 0f;
        }

        var fps = options.Fps ?? ChooseFps(clip, warnings);
        var boneCount = skeleton.Bones.Count;
        var maxFrames = Math.Max(2, (int)(options.MaxBoneBytes / (boneCount * BakedClip.FloatsPerBone * sizeof(float))));
        var frames = duration > 0f ? (int)Math.Round(duration * fps) + 1 : 1;
        if (frames > maxFrames)
        {
            fps = Math.Max(1, (int)((maxFrames - 1) / duration));
            frames = (int)Math.Round(duration * fps) + 1;
            warnings.Add($"frame rate lowered to {fps} fps to keep the clip under {options.MaxBoneBytes / (1024 * 1024)} MB");
        }
        frames = Math.Max(1, frames);

        var hasRootMotion = clip.Tracks.Any(t => t.Track is (byte)AnimTrack.RootMotionPosition or (byte)AnimTrack.RootMotionRotation);
        var solver = new PoseSolver(skeleton) { ApplyRootMotion = false, FixThighRollBones = options.FixThighRollBones };
        var animated = new SortedSet<int>();
        int ignoredFacial = 0, ignoredDof = 0;
        foreach (var t in clip.Tracks)
        {
            if (t.Track is not ((byte)AnimTrack.BonePosition or (byte)AnimTrack.BoneRotation or (byte)AnimTrack.BoneScale)) continue;
            var i = skeleton.IndexOfTag(t.BoneTag);
            if (i < 0) continue;
            if (solver.Drives(i, (AnimTrack)t.Track)) animated.Add(i);
            else if (skeleton.Bones[i].IsFacial) ignoredFacial++;
            else ignoredDof++;
        }
        if (animated.Count == 0) warnings.Add("no track of this clip matches a bone of the skeleton");
        if (ignoredFacial > 0) warnings.Add($"{ignoredFacial} facial-rig track(s) ignored (the game's facial layer overrides them)");
        if (ignoredDof > 0) warnings.Add($"{ignoredDof} track(s) ignored: the bones do not expose that channel");
        var sample = new ClipSample();
        var boneFloats = frames * boneCount * BakedClip.FloatsPerBone;
        var data = new float[boneFloats + (hasRootMotion ? frames * BakedClip.FloatsPerBone : 0)];
        const float epsilon = 1e-4f;

        for (int f = 0; f < frames; f++)
        {
            var t = duration > 0f ? Math.Min((double)f / fps, duration - epsilon) : 0.0;
            clip.Sample(t, sample);
            solver.Apply(sample);
            var o = f * boneCount * BakedClip.FloatsPerBone;
            for (int b = 0; b < boneCount; b++, o += BakedClip.FloatsPerBone)
            {
                var p = solver.LocalTranslation[b];
                var q = Quaternion.Normalize(solver.LocalRotation[b]);
                data[o] = p.X; data[o + 1] = p.Y; data[o + 2] = p.Z;
                data[o + 3] = q.X; data[o + 4] = q.Y; data[o + 5] = q.Z; data[o + 6] = q.W;
            }
            if (hasRootMotion)
            {
                var r = boneFloats + f * BakedClip.FloatsPerBone;
                var p = sample.RootMotionTranslation;
                var q = Quaternion.Normalize(sample.RootMotionRotation);
                data[r] = p.X; data[r + 1] = p.Y; data[r + 2] = p.Z;
                data[r + 3] = q.X; data[r + 4] = q.Y; data[r + 5] = q.Z; data[r + 6] = q.W;
            }
        }

        return new BakedClip
        {
            Fps = fps,
            Frames = frames,
            Duration = duration,
            BoneCount = boneCount,
            HasRootMotion = hasRootMotion,
            Data = data,
            Warnings = warnings,
            AnimatedBones = animated.ToArray(),
        };
    }

    /// <summary>The clip's native frame rate rounded to an integer, clamped to [1, <see cref="MaxFps"/>]; <see cref="DefaultFps"/> when unknown.</summary>
    public static int ChooseFps(IClip clip, List<string>? warnings = null)
    {
        var native = clip.NativeFrameRate;
        if (!(native > 0f) || !float.IsFinite(native)) return DefaultFps;
        var fps = (int)Math.Round(native);
        if (fps > MaxFps) { warnings?.Add($"native rate {native:F1} fps capped to {MaxFps}"); return MaxFps; }
        return Math.Max(1, fps);
    }
}
