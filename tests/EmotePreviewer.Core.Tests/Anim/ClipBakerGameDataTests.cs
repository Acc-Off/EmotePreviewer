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

    /// <summary>
    /// A ClipAnimations (list) clip plays each element inside its own [StartTime, EndTime) window of a long scene
    /// animation. wave_a of friends@frj@ig_1 is two such windows (53.2–56.7 s of a 67.9 s animation and 37.6–41.1 s of a
    /// 111.7 s one); handing the elements the raw clip time played the start of those animations, where nobody waves.
    /// </summary>
    [SkippableFact]
    public void ListClipElementsPlayInsideTheirTimeWindows()
    {
        using var gd = OpenOrSkip();
        var skel = gd.LoadSkeleton("mp_m_freemode_01")!;
        var dict = gd.LoadClipDictionary("friends@frj@ig_1");
        Skip.If(dict == null, "friends@frj@ig_1 not in this game version");
        var clip = dict!.FindClip("wave_a");
        Skip.If(clip == null, "wave_a not in friends@frj@ig_1");
        Assert.InRange(clip!.Duration, 3.4f, 3.5f);

        var solver = new PoseSolver(skel);
        var sample = new ClipSample();
        int hand = skel.Bones.First(b => b.Name == "SKEL_L_Hand").Index;
        int head = skel.Bones.First(b => b.Name == "SKEL_Head").Index;
        float highest = float.MinValue;
        for (double t = 0; t < clip.Duration; t += 0.1)
        {
            clip.Sample(t, sample);
            solver.Apply(sample);
            highest = Math.Max(highest, solver.WorldPosition(hand).Z - solver.WorldPosition(head).Z);
        }
        _out.WriteLine($"wave_a: left hand rises to {highest:F2} m above the head");
        // Waving (left-handed in this scene): the hand goes well above the head from about 2.0 s to 3.1 s of the clip;
        // the start of the scene animation keeps both hands below the hips.
        Assert.True(highest > 0.3f, $"the left hand never rose above the head (max {highest:F2} m)");
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
