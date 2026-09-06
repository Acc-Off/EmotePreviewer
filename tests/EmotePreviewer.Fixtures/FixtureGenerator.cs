using System.Numerics;
using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Catalog;
using EmotePreviewer.Core.Model;
using EmotePreviewer.Core.Rage.Anim;
using RageLib.Resources.GTA5.PC.Clips;

namespace EmotePreviewer.Fixtures;

/// <summary>
/// Builds a <see cref="FixtureFile"/> from the user's own game data: samples clips from the emote catalog, records their
/// raw sequence blocks, and evaluates them with the current decoder. Running the regression tests against the result
/// later detects any behavioural change of the decoder or the archive adapter.
/// </summary>
public static class FixtureGenerator
{
    public sealed record Options(int Builtin = 120, int Custom = 60, int Seed = 20260905, long MaxSequenceBytesPerClip = 1_000_000);

    public static FixtureFile Generate(GtaToolkitGameData gd, EmoteCatalog catalog, Options? options = null, Action<string>? log = null)
    {
        options ??= new Options();
        log ??= _ => { };
        var rnd = new Random(options.Seed);
        var candidates = catalog.Entries
            .Where(e => e.Kind == EmoteKind.Animation && e.Dictionary != null && e.Clip != null)
            .GroupBy(e => (e.Dictionary!.ToLowerInvariant(), e.Clip!.ToLowerInvariant()))
            .Select(g => g.First())
            .OrderBy(_ => rnd.Next())
            .ToList();

        var file = new FixtureFile { Generated = DateTime.UtcNow.ToString("O"), Skeleton = "mp_m_freemode_01" };
        var dictCache = new Dictionary<string, IClipDictionary?>(StringComparer.OrdinalIgnoreCase);

        void Take(IEnumerable<EmoteEntry> source, int n)
        {
            foreach (var e in source)
            {
                if (n <= 0) break;
                var fc = TryBuild(gd, e, dictCache, options);
                if (fc == null) continue;
                file.Clips.Add(fc);
                n--;
            }
        }
        Take(candidates.Where(e => !e.IsCustom), options.Builtin);
        Take(candidates.Where(e => e.IsCustom), options.Custom);
        log($"{file.Clips.Count} clips ({file.Clips.Count(c => c.Custom)} from loose files)");
        return file;
    }

    static FixtureClip? TryBuild(GtaToolkitGameData gd, EmoteEntry e, Dictionary<string, IClipDictionary?> cache, Options options)
    {
        if (!cache.TryGetValue(e.Dictionary!, out var dict))
        {
            try { dict = e.IsCustom ? gd.LoadLooseClipDictionary(e.CustomYcdPath!) : gd.LoadClipDictionary(e.Dictionary!); }
            catch { dict = null; }
            cache[e.Dictionary!] = dict;
        }
        if (dict is not GtClipDictionary gtDict || gtDict.FindClip(e.Clip!) is not GtClip clip) return null;

        var fc = new FixtureClip
        {
            Dictionary = e.Dictionary!,
            Clip = clip.Name,
            ClipFullName = clip.FullName,
            Custom = e.IsCustom,
            Source = e.IsCustom ? "file:" + e.CustomYcdPath : "rpf:" + gd.FindClipDictionaryPath(e.Dictionary!),
            Command = $"{e.Source}/{e.Command}",
            ClipHash = clip.Hash,
            ClipType = clip.RawClip.GetType().Name,
        };
        switch (clip.RawClip)
        {
            case ClipAnimation ca:
                fc.StartTime = ca.StartTime; fc.EndTime = ca.EndTime; fc.Rate = ca.Rate;
                break;
            case ClipAnimations cl:
                fc.ListDuration = cl.Duration;
                fc.ListEntries = (cl.Animations?.Entries ?? Enumerable.Empty<ClipAnimationsEntry>())
                    .Where(x => x?.Animation != null)
                    .Select(x => new FixtureListEntry { AnimationHash = gtDict.AnimationHash(x.Animation!) ?? 0, StartTime = x.StartTime, EndTime = x.EndTime, Rate = x.Rate })
                    .ToList();
                break;
            default:
                return null;
        }

        var anims = clip.RawAnimations().ToList();
        if (anims.Count == 0) return null;
        if (anims.Sum(a => a.Sequences?.Entries?.Sum(s => (long)(s?.Data?.Length ?? 0)) ?? 0) > options.MaxSequenceBytesPerClip) return null;
        foreach (var a in anims)
        {
            var fa = BuildAnimation(a, gtDict.AnimationHash(a) ?? 0);
            if (fa == null) return null;
            fc.Animations.Add(fa);
        }
        return fc;
    }

    static FixtureAnimation? BuildAnimation(Animation a, uint hash)
    {
        if (a.Tracks?.Entries == null || a.Sequences?.Entries == null) return null;
        var tracks = GtResourceHelpers.ToTracks(a);
        var fa = new FixtureAnimation
        {
            Hash = hash,
            Frames = a.Unknown_14h,
            SequenceFrameLimit = a.Unknown_16h,
            Duration = a.Unknown_18h,
            Tracks = tracks.Select(t => new[] { (int)t.BoneId, t.Format, t.TrackId }).ToList(),
        };
        var decodedSeqs = new List<DecodedSequence>();
        foreach (var s in a.Sequences.Entries)
        {
            if (s?.Data == null) return null;
            var header = GtResourceHelpers.ToHeader(s);
            var decoded = DecodedSequence.Parse(header, s.Data, tracks);
            decodedSeqs.Add(decoded);
            fa.Sequences.Add(FixtureSequence.From(header, s.Data, decoded));
        }
        var eval = new AnimationEvaluator(new AnimationData(fa.Frames, fa.SequenceFrameLimit, fa.Duration, tracks, decodedSeqs));

        // Integer frames 0..F-2 (F-1 is only reachable as the interpolation partner), no interpolation.
        var last = Math.Max(0, fa.Frames - 2);
        var frames = new SortedSet<int> { 0, 1, last - 1, last };
        for (int k = 1; k < 5; k++) frames.Add((int)((long)last * k / 5));
        frames.RemoveWhere(f => f < 0 || f > last);
        foreach (var f in frames)
            fa.FrameSamples.Add(new FixtureFrameSample { Frame = f, Values = Evaluate(eval, tracks, eval.GetFramePosition(f), interpolate: false) });

        // Fractional times inside the animation, interpolated.
        for (int k = 0; k < 6; k++)
        {
            var t = fa.Duration * (k + 0.37f) / 6f;
            var pos = eval.GetFramePosition(t);
            fa.TimeSamples.Add(new FixtureTimeSample { Time = t, Frame0 = pos.Frame0, Alpha1 = pos.Alpha1, Values = Evaluate(eval, tracks, pos, interpolate: true) });
        }
        return fa;
    }

    static List<float[]> Evaluate(AnimationEvaluator eval, TrackDef[] tracks, FramePosition pos, bool interpolate)
    {
        var list = new List<float[]>(tracks.Length);
        for (int i = 0; i < tracks.Length; i++)
        {
            Vector4 v;
            if (tracks[i].IsQuaternion) { var q = eval.EvaluateQuaternion(i, pos, interpolate); v = new Vector4(q.X, q.Y, q.Z, q.W); }
            else v = eval.EvaluateVector(i, pos, interpolate);
            list.Add(new[] { v.X, v.Y, v.Z, v.W });
        }
        return list;
    }
}
