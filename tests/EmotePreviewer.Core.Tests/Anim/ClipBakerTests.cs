using System.Buffers.Binary;
using System.Numerics;
using EmotePreviewer.Core.Anim;
using EmotePreviewer.Core.Model;

namespace EmotePreviewer.Core.Tests.Anim;

/// <summary>Fixes the <c>clip.bin</c> layout the browser relies on.</summary>
public sealed class ClipBakerTests
{
    /// <summary>Root (tag 0) → spine (tag 100) → head (tag 200).</summary>
    static SkeletonDef ThreeBones() => new("test", new[]
    {
        new BoneDef(0, 0, "SKEL_ROOT", -1, new Vector3(0, 0, 1), Quaternion.Identity, Vector3.One),
        new BoneDef(1, 100, "SKEL_Spine", 0, new Vector3(0, 0, 0.5f), Quaternion.Identity, Vector3.One),
        new BoneDef(2, 200, "SKEL_Head", 1, new Vector3(0, 0, 0.25f), Quaternion.Identity, Vector3.One),
    });

    /// <summary>Animates the spine translation linearly and rotates the head 90° per second; root motion moves +x at 1 m/s.</summary>
    sealed class SyntheticClip : IClip
    {
        public string Name => "synthetic";
        public float Duration { get; init; } = 2f;
        public float NativeFrameRate { get; init; } = 10f;
        public bool RootMotion { get; init; } = true;
        public IReadOnlyList<TrackKey> Tracks => RootMotion
            ? new[] { new TrackKey(100, 0), new TrackKey(200, 1), new TrackKey(0, 5), new TrackKey(0, 6) }
            : new[] { new TrackKey(100, 0), new TrackKey(200, 1) };

        public void Sample(double time, ClipSample into)
        {
            into.Clear();
            var t = (float)(time % Duration);
            into.Translations[100] = new Vector3(0, t, 0.5f);
            into.Rotations[200] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2 * t);
            if (RootMotion)
            {
                into.RootMotionTranslation = new Vector3(t, 0, 0);
                into.RootMotionRotation = Quaternion.Identity;
            }
        }
    }

    [Fact]
    public void LayoutIsFramesTimesBonesTimesSevenFollowedByRootMotion()
    {
        var skel = ThreeBones();
        var baked = ClipBaker.Bake(new SyntheticClip(), skel);

        Assert.Equal(10, baked.Fps);
        Assert.Equal(21, baked.Frames);            // round(2.0 × 10) + 1
        Assert.Equal(3, baked.BoneCount);
        Assert.True(baked.HasRootMotion);
        Assert.Equal(21 * 3 * 7 + 21 * 7, baked.TotalFloats);
        Assert.Equal(baked.TotalFloats, baked.Data.Length);
        Assert.Equal(new[] { 1, 2 }, baked.AnimatedBones);

        // frame 0: bind pose everywhere
        Assert.Equal(new float[] { 0, 0, 1, 0, 0, 0, 1 }, baked.Bone(0, 0).ToArray());
        Assert.Equal(new float[] { 0, 0, 0.5f, 0, 0, 0, 1 }, baked.Bone(0, 1).ToArray());

        // frame 5 = 0.5 s: spine translated by 0.5 in y, head rotated 45° about z
        var spine = baked.Bone(5, 1);
        Assert.Equal(0.5f, spine[1], 1e-5f);
        var head = baked.Bone(5, 2);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4);
        Assert.Equal(expected.Z, head[5], 1e-5f);
        Assert.Equal(expected.W, head[6], 1e-5f);

        // root motion block follows the bone block; frame 5 has moved 0.5 m in x
        var rm = baked.RootMotion(5);
        Assert.Equal(0.5f, rm[0], 1e-5f);
        Assert.Equal(1f, rm[6], 1e-5f);

        // the last frame is clamped just before the duration instead of wrapping to frame 0
        Assert.True(baked.Bone(20, 1)[1] > 1.99f);
    }

    [Fact]
    public void BytesAreLittleEndianFloat32InDataOrder()
    {
        var baked = ClipBaker.Bake(new SyntheticClip { RootMotion = false, Duration = 0.5f }, ThreeBones());
        Assert.False(baked.HasRootMotion);
        var bytes = baked.ToBytes();
        Assert.Equal(baked.TotalFloats * 4, bytes.Length);
        for (int i = 0; i < baked.TotalFloats; i++)
            Assert.Equal(baked.Data[i], BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4)));
    }

    [Fact]
    public void FrameRateIsCappedAndLoweredForHugeClips()
    {
        Assert.Equal(60, ClipBaker.ChooseFps(new SyntheticClip { NativeFrameRate = 120f }));
        Assert.Equal(30, ClipBaker.ChooseFps(new SyntheticClip { NativeFrameRate = 0f }));
        Assert.Equal(24, ClipBaker.ChooseFps(new SyntheticClip { NativeFrameRate = 24.2f }));

        // 3 bones × 7 floats × 4 bytes = 84 bytes per frame; a 1 KB budget allows 12 frames, so a 10 s clip drops to 1 fps
        var baked = ClipBaker.Bake(new SyntheticClip { Duration = 10f, NativeFrameRate = 60f }, ThreeBones(), new ClipBaker.Options(MaxBoneBytes: 1024));
        Assert.Equal(1, baked.Fps);
        Assert.Equal(11, baked.Frames);
        Assert.Contains(baked.Warnings, w => w.Contains("lowered"));
    }

    [Fact]
    public void ZeroDurationClipBakesOnePose()
    {
        var baked = ClipBaker.Bake(new SyntheticClip { Duration = 0f }, ThreeBones());
        Assert.Equal(1, baked.Frames);
        Assert.Contains(baked.Warnings, w => w.Contains("duration"));
    }
}

/// <summary>Bone DOF flags and the facial rule in <see cref="PoseSolver"/>.</summary>
public sealed class PoseSolverDofTests
{
    static SkeletonDef Skeleton() => new("test", new[]
    {
        new BoneDef(0, 0, "SKEL_ROOT", -1, Vector3.Zero, Quaternion.Identity, Vector3.One, BoneDofs.All),
        // Rotation-only body bone: translation tracks must not move it.
        new BoneDef(1, 100, "SKEL_Spine", 0, new Vector3(0, 0, 0.5f), Quaternion.Identity, Vector3.One, BoneDofs.Rotation),
        // Facial bone with every channel: still left alone (the facial layer owns it in-game).
        new BoneDef(2, 200, "FB_Jaw_045", 1, new Vector3(0, 0, 0.25f), Quaternion.Identity, Vector3.One, BoneDofs.All),
        // Translation limited to one axis.
        new BoneDef(3, 300, "SKEL_L_Hand", 1, new Vector3(0.1f, 0, 0), Quaternion.Identity, Vector3.One, BoneDofs.Rotation | BoneDofs.TransX),
        // The story characters' facial rig root: facial as well, even though it does not start with FB_.
        new BoneDef(4, 400, "FACIAL_facialRoot", 1, new Vector3(0, 0.1f, 0.3f), Quaternion.Identity, Vector3.One, BoneDofs.Rotation),
    });

    [Fact]
    public void TracksOnlyDriveTheChannelsABoneExposes()
    {
        var solver = new PoseSolver(Skeleton());
        var sample = new ClipSample();
        sample.Translations[100] = new Vector3(1, 2, 3);
        sample.Rotations[100] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f);
        sample.Translations[200] = new Vector3(1, 1, 1);
        sample.Rotations[200] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1f);
        sample.Translations[300] = new Vector3(0.5f, 0.5f, 0.5f);
        sample.Rotations[400] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2f);
        solver.Apply(sample);
        Assert.Equal(Quaternion.Identity, solver.LocalRotation[4]);
        Assert.False(solver.Drives(4, AnimTrack.BoneRotation));
        Assert.Equal(new Vector3(0, 0, 0.5f), solver.LocalTranslation[1]);
        Assert.Equal(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f), solver.LocalRotation[1]);
        Assert.Equal(new Vector3(0, 0, 0.25f), solver.LocalTranslation[2]);
        Assert.Equal(Quaternion.Identity, solver.LocalRotation[2]);
        Assert.Equal(new Vector3(0.5f, 0, 0), solver.LocalTranslation[3]);
        Assert.False(solver.Drives(2, AnimTrack.BoneRotation));
        Assert.True(solver.Drives(1, AnimTrack.BoneRotation));
        Assert.False(solver.Drives(1, AnimTrack.BonePosition));
    }

    [Fact]
    public void FacialBonesCanBeOptedBackIn()
    {
        var solver = new PoseSolver(Skeleton()) { IgnoreFacialBones = false };
        var sample = new ClipSample();
        sample.Translations[200] = new Vector3(1, 1, 1);
        solver.Apply(sample);
        Assert.Equal(new Vector3(1, 1, 1), solver.LocalTranslation[2]);
    }
}
