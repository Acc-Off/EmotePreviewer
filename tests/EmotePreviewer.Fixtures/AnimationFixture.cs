using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmotePreviewer.Core.Rage.Anim;

namespace EmotePreviewer.Fixtures;

/// <summary>
/// Regression fixture for the .ycd decoder: a sample of clips with their raw sequence blocks and the values the
/// decoder produced when the fixture was generated. Generated locally from the user's own GTA V installation with
/// <c>poc regression</c>; the file is not part of the repository.
/// </summary>
public sealed class FixtureFile
{
    public int Version { get; set; } = 1;
    public string? Generated { get; set; }
    public string? Skeleton { get; set; }
    public List<FixtureClip> Clips { get; set; } = new();
}

public sealed class FixtureClip
{
    public string Dictionary { get; set; } = "";
    public string Clip { get; set; } = "";
    public string? ClipFullName { get; set; }
    /// <summary>True when the dictionary is a loose .ycd from an emote resource rather than a game archive.</summary>
    public bool Custom { get; set; }
    /// <summary>"rpf:&lt;archive path&gt;" or "file:&lt;path&gt;".</summary>
    public string? Source { get; set; }
    public string? Command { get; set; }
    public uint ClipHash { get; set; }
    public string ClipType { get; set; } = "";
    public float? StartTime { get; set; }
    public float? EndTime { get; set; }
    public float? Rate { get; set; }
    public float? ListDuration { get; set; }
    public List<FixtureListEntry>? ListEntries { get; set; }
    public List<FixtureAnimation> Animations { get; set; } = new();
    public override string ToString() => $"{Dictionary}/{Clip}";
}

public sealed class FixtureListEntry
{
    public uint AnimationHash { get; set; }
    public float StartTime { get; set; }
    public float EndTime { get; set; }
    public float Rate { get; set; }
}

public sealed class FixtureAnimation
{
    public uint Hash { get; set; }
    public ushort Frames { get; set; }
    public ushort SequenceFrameLimit { get; set; }
    public float Duration { get; set; }
    /// <summary>[boneId, format, trackId] per track index.</summary>
    public List<int[]> Tracks { get; set; } = new();
    public List<FixtureSequence> Sequences { get; set; } = new();
    public List<FixtureFrameSample> FrameSamples { get; set; } = new();
    public List<FixtureTimeSample> TimeSamples { get; set; } = new();

    public TrackDef[] TrackDefs() => Tracks.Select(t => new TrackDef((ushort)t[0], (byte)t[1], (byte)t[2])).ToArray();

    /// <summary>Builds the decoder input from the recorded header fields and Data bytes.</summary>
    public AnimationData Decode()
    {
        var tracks = TrackDefs();
        var seqs = new DecodedSequence[Sequences.Count];
        for (int i = 0; i < Sequences.Count; i++)
        {
            var s = Sequences[i];
            seqs[i] = DecodedSequence.Parse(s.Header(), Convert.FromBase64String(s.Data), tracks);
        }
        return new AnimationData(Frames, SequenceFrameLimit, Duration, tracks, seqs);
    }
}

public sealed class FixtureSequence
{
    public uint DataLength { get; set; }
    public uint FrameOffset { get; set; }
    public uint RootMotionRefsOffset { get; set; }
    public ushort NumFrames { get; set; }
    public ushort FrameLength { get; set; }
    public ushort IndirectQuantizeFloatNumInts { get; set; }
    public ushort QuantizeFloatValueBits { get; set; }
    public byte ChunkSize { get; set; }
    public byte RootMotionRefCounts { get; set; }
    public string Data { get; set; } = "";
    /// <summary>Per track index: channel type ids in slot order.</summary>
    public List<int[]> ChannelTypes { get; set; } = new();

    public SequenceHeader Header() => new(DataLength, FrameOffset, RootMotionRefsOffset, NumFrames, FrameLength,
        IndirectQuantizeFloatNumInts, QuantizeFloatValueBits, ChunkSize, RootMotionRefCounts);

    public static FixtureSequence From(SequenceHeader h, ReadOnlySpan<byte> data, DecodedSequence decoded)
    {
        var f = new FixtureSequence
        {
            DataLength = h.DataLength, FrameOffset = h.FrameOffset, RootMotionRefsOffset = h.RootMotionRefsOffset,
            NumFrames = h.NumFrames, FrameLength = h.FrameLength, IndirectQuantizeFloatNumInts = h.IndirectQuantizeFloatNumInts,
            QuantizeFloatValueBits = h.QuantizeFloatValueBits, ChunkSize = h.ChunkSize, RootMotionRefCounts = h.RootMotionRefCounts,
            Data = Convert.ToBase64String(data),
        };
        for (int t = 0; t < decoded.TrackCount; t++) f.ChannelTypes.Add(decoded.GetChannelTypes(t).Select(c => (int)c).ToArray());
        return f;
    }
}

public sealed class FixtureFrameSample
{
    public int Frame { get; set; }
    public List<float[]> Values { get; set; } = new();
}

public sealed class FixtureTimeSample
{
    public float Time { get; set; }
    public int Frame0 { get; set; }
    public float Alpha1 { get; set; }
    public List<float[]> Values { get; set; } = new();
}

/// <summary>Locates and loads the fixture file; <see cref="Instance"/> is null when it is absent.</summary>
public static class FixtureLoader
{
    public const string FileName = "ycd-regression-v1.json.gz";
    static readonly Lazy<FixtureFile?> _instance = new(() => Load(Path));
    public static FixtureFile? Instance => _instance.Value;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Default location: &lt;repo&gt;/tests/fixtures/&lt;FileName&gt;, or the FixtureDir assembly metadata of the calling test assembly.</summary>
    public static string? Path
    {
        get
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? dir;
                try { dir = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "FixtureDir")?.Value; }
                catch { continue; }
                if (dir == null) continue;
                var p = System.IO.Path.Combine(dir, FileName);
                if (File.Exists(p)) return p;
            }
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                var p = System.IO.Path.Combine(d.FullName, "tests", "fixtures", FileName);
                if (File.Exists(p)) return p;
                d = d.Parent;
            }
            return null;
        }
    }

    public static FixtureFile? Load(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<FixtureFile>(gz, JsonOptions);
    }

    public static void Save(FixtureFile file, string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gz, file, JsonOptions);
    }
}
