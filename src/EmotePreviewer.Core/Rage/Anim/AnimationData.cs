using System.Numerics;

namespace EmotePreviewer.Core.Rage.Anim;

/// <summary>A decoded animation: header values (spec §2), track table and its sequence blocks (spec §3).</summary>
public sealed class AnimationData
{
    public ushort Frames { get; }
    public ushort SequenceFrameLimit { get; }
    public float Duration { get; }
    public IReadOnlyList<TrackDef> Tracks { get; }
    public IReadOnlyList<DecodedSequence> Sequences { get; }

    public AnimationData(ushort frames, ushort sequenceFrameLimit, float duration, IReadOnlyList<TrackDef> tracks, IReadOnlyList<DecodedSequence> sequences)
    {
        if (sequences.Count == 0) throw new ArgumentException("an animation needs at least one sequence block", nameof(sequences));
        Frames = frames; SequenceFrameLimit = sequenceFrameLimit; Duration = duration; Tracks = tracks; Sequences = sequences;
    }

    /// <summary>Number of sequence blocks expected from the header (spec §3): ceil((F − 1) / L), at least 1.</summary>
    public static int ExpectedSequenceCount(int frames, int sequenceFrameLimit)
    {
        if (frames <= 1 || sequenceFrameLimit <= 0) return 1;
        return (frames - 1 + sequenceFrameLimit - 1) / sequenceFrameLimit;
    }

    /// <summary>Builds an animation from raw sequence blocks (32-byte header + data each) as stored in the file.</summary>
    public static AnimationData FromRawSequences(ushort frames, ushort sequenceFrameLimit, float duration, IReadOnlyList<TrackDef> tracks, IReadOnlyList<ReadOnlyMemory<byte>> rawBlocks)
    {
        var seqs = new DecodedSequence[rawBlocks.Count];
        for (int i = 0; i < rawBlocks.Count; i++)
        {
            var block = rawBlocks[i];
            var header = SequenceHeader.Parse(block.Span);
            seqs[i] = DecodedSequence.Parse(header, block.Slice(SequenceHeader.Size), tracks);
        }
        return new AnimationData(frames, sequenceFrameLimit, duration, tracks, seqs);
    }

    public int FindTrack(ushort boneId, byte trackId)
    {
        for (int i = 0; i < Tracks.Count; i++)
            if (Tracks[i].BoneId == boneId && Tracks[i].TrackId == trackId) return i;
        return -1;
    }
}

/// <summary>Result of spec §5.2: which block/frame pair a time maps to, and the blend weight.</summary>
/// <param name="Frame0">Absolute frame index (0 … F − 2 for in-range times).</param>
/// <param name="Alpha1">Weight of the following frame.</param>
/// <param name="Sequence">Block index.</param>
/// <param name="LocalFrame0">Frame index inside the block; the next frame is LocalFrame0 + 1.</param>
public readonly record struct FramePosition(int Frame0, float Alpha1, int Sequence, int LocalFrame0)
{
    public float Alpha0 => 1f - Alpha1;
}

/// <summary>Evaluates an <see cref="AnimationData"/> at arbitrary times (spec §5.2–§5.3).</summary>
public sealed class AnimationEvaluator
{
    public AnimationData Animation { get; }

    public AnimationEvaluator(AnimationData animation) => Animation = animation;

    /// <summary>Maps an animation-local time in seconds to a frame position (spec §5.2).</summary>
    public FramePosition GetFramePosition(float t)
    {
        var a = Animation;
        int frames = a.Frames;
        int frame0;
        float alpha1;
        if (frames <= 1 || a.Duration <= 0f)
        {
            frame0 = 0; alpha1 = 0f;
        }
        else
        {
            float nframes = frames - 1;
            float curPos = (t / a.Duration) * nframes;
            float fl = MathF.Floor(curPos);
            frame0 = (ushort)(uint)fl % frames;
            alpha1 = curPos - fl;
        }
        return Locate(frame0, alpha1);
    }

    /// <summary>Frame position for an integer frame with no blending (used for un-interpolated evaluation).</summary>
    public FramePosition GetFramePosition(int frame) => Locate(frame, 0f);

    FramePosition Locate(int frame0, float alpha1)
    {
        var a = Animation;
        int limit = a.SequenceFrameLimit;
        int seq = limit > 0 ? frame0 / limit : 0;
        int local = limit > 0 ? frame0 % limit : frame0;
        int count = a.Sequences.Count;
        if (seq >= count) { local += (seq - (count - 1)) * limit; seq = count - 1; }
        return new FramePosition(frame0, alpha1, seq, local);
    }

    /// <summary>Vector/float track value (spec §5.3: component-wise lerp when interpolating).</summary>
    public Vector4 EvaluateVector(int track, in FramePosition pos, bool interpolate)
    {
        var seq = Animation.Sequences[pos.Sequence];
        var v0 = seq.GetVector(track, pos.LocalFrame0);
        if (!interpolate) return v0;
        var v1 = seq.GetVector(track, pos.LocalFrame0 + 1);
        return v0 * pos.Alpha0 + v1 * pos.Alpha1;
    }

    /// <summary>Quaternion track value (spec §5.3: nlerp without sign alignment when interpolating; raw otherwise).</summary>
    public Quaternion EvaluateQuaternion(int track, in FramePosition pos, bool interpolate)
    {
        var seq = Animation.Sequences[pos.Sequence];
        var q0 = seq.GetQuaternion(track, pos.LocalFrame0);
        if (!interpolate) return q0;
        var q1 = seq.GetQuaternion(track, pos.LocalFrame0 + 1);
        float a1 = pos.Alpha1, a0 = 1f - a1;
        var q = new Quaternion(q0.X * a0 + q1.X * a1, q0.Y * a0 + q1.Y * a1, q0.Z * a0 + q1.Z * a1, q0.W * a0 + q1.W * a1);
        return Quaternion.Normalize(q);
    }
}

/// <summary>Clip time → animation time (spec §5.4).</summary>
public static class ClipTiming
{
    /// <summary>Type 1 (ClipAnimation): scaled by Rate, wrapped into [StartTime, EndTime).</summary>
    public static float ClipAnimationTime(float t, float startTime, float endTime, float rate)
    {
        float dur = endTime - startTime;
        if (dur <= 0f) return startTime;
        float scaled = t * rate;
        return startTime + Mod(scaled, dur);
    }

    /// <summary>Playback length of a ClipAnimation in seconds.</summary>
    public static float ClipAnimationDuration(float startTime, float endTime, float rate)
    {
        float dur = endTime - startTime;
        return rate != 0f ? dur / rate : dur;
    }

    /// <summary>Type 2 (ClipAnimations): the same wrapped time is handed to every element.</summary>
    public static float ClipAnimationsTime(float t, float duration) => duration > 0f ? Mod(t, duration) : 0f;

    static float Mod(float x, float m)
    {
        float r = x % m;
        return r < 0f ? r + m : r;
    }
}
