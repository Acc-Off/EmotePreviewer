using RageLib.GTA5.PSO;
using RageLib.GTA5.PSOWrappers;
using RageLib.GTA5.PSOWrappers.Types;

namespace EmotePreviewer.Core.Rage;

/// <summary>
/// One movement clip set (<c>fwClipSet</c>) from <c>clip_sets.ymt</c>: the dictionary its clips are looked up in, the set
/// consulted when a clip is missing, and the per-clip items the set declares (properties only; a set can serve clips it
/// does not list as long as its dictionary has them).
/// </summary>
/// <param name="Id">Hash of the clip set name (e.g. <c>move_m@gangster@var_a</c>).</param>
/// <param name="Dictionary">Hash of the clip dictionary name; 0 when the set names none.</param>
/// <param name="Fallback">Hash of the fallback clip set; 0 when there is none.</param>
/// <param name="Items">Hashes of the clip names the set declares items for.</param>
public sealed record ClipSetDef(uint Id, uint Dictionary, uint Fallback, IReadOnlySet<uint> Items);

/// <summary>
/// The clip set table of the game (<c>data/anim/clip_sets/clip_sets.ymt</c>, a PSO file whose root is
/// <c>fwClipSetManager.clipSets</c>). Movement styles (walks) are clip set names: the walk animation is the clip
/// <c>walk</c> of the set's dictionary, or of the fallback chain when that dictionary lacks it.
/// </summary>
public sealed class ClipSetTable
{
    // Field names of fwClipSetManager / fwClipSet as they appear in the PSO type definitions (joaat of the member name).
    static readonly uint ClipSetsField = JenkinsHash.Hash("clipSets");
    static readonly uint DictionaryField = JenkinsHash.Hash("clipDictionaryName");
    static readonly uint FallbackField = JenkinsHash.Hash("fallbackId");
    /// <summary>The clip item map of a set (member name unknown; hash observed in the game file).</summary>
    const uint ItemsField = 0xC4622BBA;
    // atBinaryMap entries are structures with these two members.
    const int MapKey = 0x6098a50e;
    const int MapValue = 0x063fa3f2;

    /// <summary>Longest fallback chain followed before giving up (the game's chains are one or two deep).</summary>
    public const int MaxFallbackDepth = 8;

    readonly Dictionary<uint, ClipSetDef> _sets = new();

    public int Count => _sets.Count;
    public IEnumerable<ClipSetDef> Sets => _sets.Values;

    public ClipSetDef? Find(uint id) => _sets.TryGetValue(id, out var s) ? s : null;
    public ClipSetDef? Find(string name) => Find(JenkinsHash.HashLower(name));
    public bool Contains(string name) => _sets.ContainsKey(JenkinsHash.HashLower(name));

    /// <summary>Adds or replaces a set (later files override earlier ones, like the game's DLC order).</summary>
    public void Add(ClipSetDef set) => _sets[set.Id] = set;

    public void Merge(ClipSetTable later)
    {
        foreach (var s in later._sets.Values) Add(s);
    }

    /// <summary>
    /// Finds the dictionary that provides <paramref name="clip"/> for <paramref name="set"/>: the set's own dictionary
    /// when <paramref name="dictionaryHasClip"/> says it holds the clip, else the fallback set's, and so on.
    /// </summary>
    /// <param name="dictionaryHasClip">(dictionary hash, clip hash) → whether that dictionary exists and holds the clip.</param>
    /// <param name="chain">The clip sets visited, first to last (for diagnostics).</param>
    /// <returns>The dictionary hash, or null when no set of the chain provides the clip.</returns>
    public uint? ResolveDictionary(uint set, uint clip, Func<uint, uint, bool> dictionaryHasClip, out List<ClipSetDef> chain)
    {
        chain = new List<ClipSetDef>();
        var id = set;
        for (int depth = 0; depth < MaxFallbackDepth && id != 0; depth++)
        {
            if (!_sets.TryGetValue(id, out var def) || chain.Contains(def)) break;
            chain.Add(def);
            if (def.Dictionary != 0 && dictionaryHasClip(def.Dictionary, clip)) return def.Dictionary;
            id = def.Fallback;
        }
        return null;
    }

    public uint? ResolveDictionary(string set, string clip, Func<uint, uint, bool> dictionaryHasClip, out List<ClipSetDef> chain) =>
        ResolveDictionary(JenkinsHash.HashLower(set), JenkinsHash.HashLower(clip), dictionaryHasClip, out chain);

    /// <summary>Parses a <c>clip_sets.ymt</c> (PSO) stream.</summary>
    public static ClipSetTable Parse(Stream stream)
    {
        var pso = new PsoFile();
        pso.Load(stream);
        var root = new PsoReader().Parse(pso) as PsoStructure ?? throw new InvalidDataException("clip_sets.ymt: root is not a structure");
        var table = new ClipSetTable();
        if (!root.Values.TryGetValue((int)ClipSetsField, out var setsValue) || setsValue is not PsoMap map || map.Entries == null)
            throw new InvalidDataException("clip_sets.ymt: no clipSets map");
        foreach (var entry in map.Entries)
        {
            if (!entry.Values.TryGetValue(MapKey, out var keyValue) || HashOf(keyValue) is not { } id) continue;
            var body = entry.Values.TryGetValue(MapValue, out var v) ? Unwrap(v) : null;
            if (body == null) continue;
            var dictionary = body.Values.TryGetValue((int)DictionaryField, out var d) ? HashOf(d) ?? 0 : 0;
            var fallback = body.Values.TryGetValue((int)FallbackField, out var f) ? HashOf(f) ?? 0 : 0;
            var items = new HashSet<uint>();
            if (body.Values.TryGetValue(unchecked((int)ItemsField), out var itemsValue) && itemsValue is PsoMap itemMap && itemMap.Entries != null)
                foreach (var item in itemMap.Entries)
                    if (item.Values.TryGetValue(MapKey, out var k) && HashOf(k) is { } clipHash) items.Add(clipHash);
            table.Add(new ClipSetDef(id, dictionary, fallback, items));
        }
        return table;
    }

    static PsoStructure? Unwrap(IPsoValue v) => v switch
    {
        PsoStructure s => s,
        PsoStructure3 s3 => s3.Value,
        _ => null,
    };

    /// <summary>Hash value of a hash-string member (atHashString / atFinalHashString), or of a plain 32-bit field.</summary>
    static uint? HashOf(IPsoValue v) => v switch
    {
        PsoString7 s => (uint)s.Value,
        PsoString8 s => (uint)s.Value,
        PsoUInt32 u => u.Value,
        PsoUInt32Hex u => u.Value,
        PsoInt32 i => (uint)i.Value,
        _ => null,
    };
}
