using System.Numerics;
using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Anim;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Model;
using Xunit.Abstractions;

namespace EmotePreviewer.Core.Tests.Anim;

/// <summary>
/// Bakes real clips from the local GTA V install and checks that replaying the baked local transforms through the
/// skeleton hierarchy reproduces the poses the solver computes directly. Skipped without GTA V / key files.
/// </summary>
public sealed class ClipBakerGameDataTests
{
    readonly ITestOutputHelper _out;
    public ClipBakerGameDataTests(ITestOutputHelper output) => _out = output;

    static GtaToolkitGameData OpenOrSkip()
    {
        var gta = GtaLocator.Detect();
        Skip.If(gta == null, "GTA V not installed");
        try { return GtaToolkitGameData.Open(gta!); }
        catch (GtaKeys.KeyMaterialMissingException ex) { throw new SkipException(ex.Message); }
    }

    [SkippableTheory]
    [InlineData("anim@mp_player_intcelebrationfemale@air_guitar", "air_guitar")]
    [InlineData("anim@amb@nightclub@dancers@podium_dancers@", "hi_dance_facedj_17_v2_male^5")]
    [InlineData("amb@world_human_musician@guitar@male@base", "base")]
    public void BakedTransformsReproduceSolverPoses(string dictionary, string clipName)
    {
        using var gd = OpenOrSkip();
        var skel = gd.LoadSkeleton("mp_m_freemode_01")!;
        var dict = gd.LoadClipDictionary(dictionary);
        Skip.If(dict == null, $"{dictionary} not in this game version");
        var clip = dict!.FindClip(clipName);
        Skip.If(clip == null, $"{clipName} not in {dictionary}");

        var baked = ClipBaker.Bake(clip!, skel);
        _out.WriteLine($"{dictionary}/{clipName}: {baked.Frames} frames @ {baked.Fps} fps, duration {baked.Duration:F2} s, root motion {baked.HasRootMotion}, {baked.TotalFloats * 4 / 1024} KB, native {clip!.NativeFrameRate:F1} fps");
        Assert.True(baked.Frames >= 2);
        Assert.Equal(skel.Bones.Count, baked.BoneCount);
        Assert.Empty(baked.Warnings);

        // Replay a few frames: world positions from the baked locals must match the solver's direct evaluation.
        var direct = new PoseSolver(skel);
        var sample = new ClipSample();
        var localT = new Vector3[baked.BoneCount];
        var localR = new Quaternion[baked.BoneCount];
        foreach (var f in new[] { 0, baked.Frames / 3, baked.Frames / 2, baked.Frames - 2 })
        {
            var t = Math.Min((double)f / baked.Fps, baked.Duration - 1e-4);
            clip.Sample(t, sample);
            direct.Apply(sample);

            for (int b = 0; b < baked.BoneCount; b++)
            {
                var v = baked.Bone(f, b);
                localT[b] = new Vector3(v[0], v[1], v[2]);
                localR[b] = new Quaternion(v[3], v[4], v[5], v[6]);
            }
            var world = SolveWorld(skel, localT, localR);
            for (int b = 0; b < baked.BoneCount; b++)
            {
                var expected = direct.WorldPosition(b);
                var got = world[b];
                Assert.True(Vector3.Distance(expected, got) < 1e-3f, $"frame {f} bone {skel.Bones[b].Name}: expected {expected} got {got}");
            }
        }
    }

    /// <summary>Hierarchy solve over the baked locals, the way the browser's Bone hierarchy does it (scale is not baked).</summary>
    static Vector3[] SolveWorld(SkeletonDef skel, Vector3[] t, Quaternion[] r)
    {
        var world = new Matrix4x4[skel.Bones.Count];
        foreach (var i in skel.EvaluationOrder)
        {
            var local = Matrix4x4.CreateFromQuaternion(r[i]) * Matrix4x4.CreateTranslation(t[i]);
            var p = skel.Bones[i].ParentIndex;
            world[i] = p >= 0 ? local * world[p] : local;
        }
        return world.Select(m => m.Translation).ToArray();
    }
}
