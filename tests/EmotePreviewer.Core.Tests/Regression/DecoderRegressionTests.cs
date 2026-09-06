using System.Diagnostics;
using System.Numerics;
using System.Text;
using EmotePreviewer.Core.Rage;
using EmotePreviewer.Core.Rage.Anim;
using EmotePreviewer.Fixtures;
using Xunit.Abstractions;

namespace EmotePreviewer.Core.Tests.Regression;

/// <summary>
/// Regression tests for the .ycd decoder against a locally generated fixture (<c>poc regression</c>; see README).
/// Skipped when the fixture file is absent.
/// </summary>
public sealed class DecoderRegressionTests
{
    const float Tolerance = 1e-4f;
    const int MaxReported = 40;
    readonly ITestOutputHelper _out;

    public DecoderRegressionTests(ITestOutputHelper output) => _out = output;

    static FixtureFile? Fixture => FixtureLoader.Instance;

    /// <summary>Channel layout of every block matches the recorded ChannelTypes.</summary>
    [SkippableFact]
    public void ChannelTypesMatchFixture()
    {
        var fixture = Fixture; Skip.If(fixture == null, "fixture file not found");
        var failures = new List<string>();
        int blocks = 0;
        foreach (var clip in fixture!.Clips)
            foreach (var anim in clip.Animations)
            {
                var decoded = anim.Decode();
                for (int s = 0; s < anim.Sequences.Count; s++)
                {
                    blocks++;
                    var expected = anim.Sequences[s].ChannelTypes;
                    var seq = decoded.Sequences[s];
                    if (expected.Count != seq.TrackCount) { failures.Add($"{clip} anim {anim.Hash:X8} block {s}: {expected.Count} tracks expected, decoder has {seq.TrackCount}"); continue; }
                    for (int t = 0; t < expected.Count; t++)
                    {
                        var got = seq.GetChannelTypes(t).Select(c => (int)c).ToArray();
                        if (!got.SequenceEqual(expected[t]))
                            failures.Add($"{clip} anim {anim.Hash:X8} block {s} track {t}: expected [{string.Join(",", expected[t])}] got [{string.Join(",", got)}]");
                    }
                }
            }
        _out.WriteLine($"checked {blocks} blocks");
        AssertNoFailures(failures);
    }

    /// <summary>Un-interpolated integer-frame evaluation.</summary>
    [SkippableFact]
    public void FrameSamplesMatchFixture()
    {
        var fixture = Fixture; Skip.If(fixture == null, "fixture file not found");
        var failures = new List<string>();
        int samples = 0;
        foreach (var clip in fixture!.Clips)
            foreach (var anim in clip.Animations)
            {
                var eval = new AnimationEvaluator(anim.Decode());
                foreach (var fs in anim.FrameSamples)
                {
                    samples++;
                    var pos = eval.GetFramePosition(fs.Frame);
                    CompareValues(eval, pos, interpolate: false, fs.Values, $"{clip} anim {anim.Hash:X8} frame {fs.Frame}", failures);
                }
            }
        _out.WriteLine($"checked {samples} frame samples");
        AssertNoFailures(failures);
    }

    /// <summary>Time to frame position, and interpolated evaluation.</summary>
    [SkippableFact]
    public void TimeSamplesMatchFixture()
    {
        var fixture = Fixture; Skip.If(fixture == null, "fixture file not found");
        var failures = new List<string>();
        int samples = 0, skipped = 0;
        foreach (var clip in fixture!.Clips)
            foreach (var anim in clip.Animations)
            {
                var eval = new AnimationEvaluator(anim.Decode());
                foreach (var ts in anim.TimeSamples)
                {
                    if (ts.Values.Any(v => v.Any(x => !float.IsFinite(x)))) { skipped++; continue; }
                    samples++;
                    var where = $"{clip} anim {anim.Hash:X8} t={ts.Time}";
                    var pos = eval.GetFramePosition(ts.Time);
                    if (pos.Frame0 != ts.Frame0 || MathF.Abs(pos.Alpha1 - ts.Alpha1) > Tolerance)
                    {
                        failures.Add($"{where}: frame position expected ({ts.Frame0}, {ts.Alpha1}) got ({pos.Frame0}, {pos.Alpha1})");
                        continue;
                    }
                    CompareValues(eval, pos, interpolate: true, ts.Values, where, failures);
                }
            }
        _out.WriteLine($"checked {samples} time samples, skipped {skipped} non-finite");
        AssertNoFailures(failures);
    }

    /// <summary>Clip hashes are Jenkins(clip name), and the header-derived block count matches the file.</summary>
    [SkippableFact]
    public void ClipHashesAndBlockCountsAreConsistent()
    {
        var fixture = Fixture; Skip.If(fixture == null, "fixture file not found");
        var failures = new List<string>();
        foreach (var clip in fixture!.Clips)
        {
            if (JenkinsHash.Hash(clip.Clip) != clip.ClipHash) failures.Add($"{clip}: Jenkins hash mismatch");
            foreach (var anim in clip.Animations)
            {
                var expected = AnimationData.ExpectedSequenceCount(anim.Frames, anim.SequenceFrameLimit);
                if (expected != anim.Sequences.Count) failures.Add($"{clip} anim {anim.Hash:X8}: expected {expected} blocks, file has {anim.Sequences.Count}");
                if (clip.ClipType == "ClipAnimation")
                {
                    var dur = ClipTiming.ClipAnimationDuration(clip.StartTime!.Value, clip.EndTime!.Value, clip.Rate!.Value);
                    if (!(dur >= 0)) failures.Add($"{clip}: negative clip duration {dur}");
                }
            }
        }
        AssertNoFailures(failures);
    }

    /// <summary>Decoding every block of the biggest animation stays fast.</summary>
    [SkippableFact]
    public void FullDecodeIsFast()
    {
        var fixture = Fixture; Skip.If(fixture == null, "fixture file not found");
        var biggest = fixture!.Clips.SelectMany(c => c.Animations.Select(a => (clip: c, anim: a)))
            .OrderByDescending(x => x.anim.Sequences.Sum(s => (long)s.DataLength)).First();
        var raw = biggest.anim.Sequences.Select(s => (header: s.Header(), data: (ReadOnlyMemory<byte>)Convert.FromBase64String(s.Data))).ToArray();
        var tracks = biggest.anim.TrackDefs();
        // warm up
        foreach (var (h, d) in raw) DecodedSequence.Parse(h, d, tracks);
        var sw = Stopwatch.StartNew();
        const int iterations = 5;
        for (int i = 0; i < iterations; i++)
            foreach (var (h, d) in raw) DecodedSequence.Parse(h, d, tracks);
        var perDecode = sw.Elapsed.TotalMilliseconds / iterations;

        // Frame-record channels are read lazily, so also time a full evaluation of every frame of every track.
        var anim = biggest.anim.Decode();
        var eval = new AnimationEvaluator(anim);
        sw.Restart();
        var sink = Vector4.Zero;
        for (int f = 0; f < anim.Frames - 1; f++)
        {
            var pos = eval.GetFramePosition(f);
            for (int t = 0; t < tracks.Length; t++)
                sink += tracks[t].IsQuaternion ? new Vector4(eval.EvaluateQuaternion(t, pos, true).W) : eval.EvaluateVector(t, pos, true);
        }
        var evalMs = sw.Elapsed.TotalMilliseconds;
        _out.WriteLine($"{biggest.clip} anim {biggest.anim.Hash:X8}: {biggest.anim.Frames} frames x {tracks.Length} tracks, {raw.Sum(r => r.data.Length)} bytes, {perDecode:F2} ms per full decode, {evalMs:F1} ms to evaluate every frame (sink {sink.X:F0})");
        Assert.True(perDecode < 50, $"full decode took {perDecode:F1} ms");
        Assert.True(evalMs < 500, $"full evaluation took {evalMs:F1} ms");
    }

    static void CompareValues(AnimationEvaluator eval, FramePosition pos, bool interpolate, List<float[]> expected, string where, List<string> failures)
    {
        var tracks = eval.Animation.Tracks;
        if (expected.Count != tracks.Count) { failures.Add($"{where}: {expected.Count} values expected for {tracks.Count} tracks"); return; }
        for (int t = 0; t < tracks.Count; t++)
        {
            var e = expected[t];
            Vector4 got;
            if (tracks[t].IsQuaternion)
            {
                var q = eval.EvaluateQuaternion(t, pos, interpolate);
                got = new Vector4(q.X, q.Y, q.Z, q.W);
            }
            else got = eval.EvaluateVector(t, pos, interpolate);
            // Vector3 tracks: only x,y,z are meaningful. Float tracks: only x.
            int n = tracks[t].Format switch { TrackDef.FormatQuaternion => 4, TrackDef.FormatVector3 => 3, _ => 1 };
            for (int k = 0; k < n; k++)
            {
                float ev = e[k], gv = k switch { 0 => got.X, 1 => got.Y, 2 => got.Z, _ => got.W };
                if (!float.IsFinite(ev)) continue;
                if (MathF.Abs(ev - gv) > Tolerance)
                {
                    failures.Add($"{where} track {t} (bone {tracks[t].BoneId} id {tracks[t].TrackId} fmt {tracks[t].Format}) comp {k}: expected {ev} got {gv} | expected [{string.Join(", ", e)}] got [{got.X}, {got.Y}, {got.Z}, {got.W}]");
                    break;
                }
            }
        }
    }

    static void AssertNoFailures(List<string> failures)
    {
        if (failures.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine($"{failures.Count} mismatches:");
        foreach (var f in failures.Take(MaxReported)) sb.AppendLine("  " + f);
        if (failures.Count > MaxReported) sb.AppendLine($"  ... and {failures.Count - MaxReported} more");
        Assert.Fail(sb.ToString());
    }
}
