using System.Numerics;

namespace EmotePreviewer.Core.Model;

/// <summary>Track ids used by RAGE animations (the Track byte of an animation bone id entry).</summary>
public enum AnimTrack : byte
{
    BonePosition = 0,
    BoneRotation = 1,
    BoneScale = 2,
    RootMotionPosition = 5,
    RootMotionRotation = 6,
    CameraPosition = 7,
    CameraRotation = 8,
    FacialTranslation = 24,
    FacialEuler = 25,
    FacialRotation = 26,
}

public readonly record struct TrackKey(ushort BoneTag, byte Track);

/// <summary>Result of sampling a clip at one point in time. Keyed by bone tag; only animated bones are present.</summary>
public sealed class ClipSample
{
    public Dictionary<ushort, Vector3> Translations { get; } = new();
    public Dictionary<ushort, Quaternion> Rotations { get; } = new();
    public Dictionary<ushort, Vector3> Scales { get; } = new();
    public Vector3 RootMotionTranslation { get; set; }
    public Quaternion RootMotionRotation { get; set; } = Quaternion.Identity;

    public void Clear()
    {
        Translations.Clear(); Rotations.Clear(); Scales.Clear();
        RootMotionTranslation = Vector3.Zero;
        RootMotionRotation = Quaternion.Identity;
    }
}

/// <summary>A playable clip (one entry of a clip dictionary).</summary>
public interface IClip
{
    string Name { get; }
    /// <summary>Playback length in seconds, after rate scaling.</summary>
    float Duration { get; }
    /// <summary>Which (bone, track) pairs this clip animates.</summary>
    IReadOnlyList<TrackKey> Tracks { get; }
    /// <summary>Keyframes per second of playback (after rate scaling); 0 when unknown.</summary>
    float NativeFrameRate { get; }
    /// <summary>Evaluates the clip at <paramref name="time"/> seconds (wrapping past Duration) into <paramref name="into"/>.</summary>
    void Sample(double time, ClipSample into);
}

public interface IClipDictionary
{
    string Name { get; }
    IReadOnlyList<IClip> Clips { get; }
    IClip? FindClip(string name);
}

/// <summary>Where a clip of a movement clip set is found (see <c>ClipSetTable</c>).</summary>
/// <param name="Dictionary">Name of the clip dictionary that holds the clip.</param>
/// <param name="Clip">The clip name asked for.</param>
/// <param name="ViaFallback">True when the set's own dictionary lacks the clip and a fallback set supplied it.</param>
public sealed record ClipSetClip(string Dictionary, string Clip, bool ViaFallback);

/// <summary>Access to GTA V game data needed for previews. Implementations wrap a specific file-format library.</summary>
public interface IGameDataSource : IDisposable
{
    string GtaFolder { get; }
    int ClipDictionaryCount { get; }
    SkeletonDef? LoadSkeleton(string yftName);
    /// <summary>True when a skeleton (.yft) of that name is indexed (without loading it).</summary>
    bool HasSkeleton(string yftName);
    /// <summary>True when a dictionary of that name is indexed (without loading it).</summary>
    bool HasClipDictionary(string dictionaryName);
    IClipDictionary? LoadClipDictionary(string dictionaryName);
    /// <summary>
    /// Resolves a clip of a movement clip set (<c>move_m@...</c>) through the game's clip_sets.ymt, following fallback
    /// sets; null when the table is unavailable, the set is unknown, or no set of the chain has the clip.
    /// </summary>
    ClipSetClip? ResolveClipSetClip(string clipSet, string clip);
    /// <summary>Loads a .ycd shipped loose in a FiveM resource (RSC7, or raw headerless resource data).</summary>
    IClipDictionary LoadLooseClipDictionary(string path);
    /// <summary>True when a drawable (.ydr / .ydd) of that model name is indexed.</summary>
    bool HasDrawable(string modelName);
    /// <summary>Highest-LOD mesh of a prop or ped component model; null when the model is not in the game data.</summary>
    MeshData? LoadDrawable(string modelName);
    /// <summary>True when the ped folder (e.g. <c>mp_m_freemode_01</c>) has a component dictionary of that file name (e.g. <c>uppr_000_r</c>).</summary>
    bool HasPedComponent(string pedFolder, string fileName);
    /// <summary>Skinned mesh of a ped component dictionary (first drawable); null when absent.</summary>
    MeshData? LoadPedComponent(string pedFolder, string fileName, string? modelName = null);
}
