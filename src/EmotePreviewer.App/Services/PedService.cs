using System.Security.Cryptography;
using System.Text;
using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Model;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Services;

/// <summary>One component slot of a ped and the drawable chosen for it.</summary>
public sealed record PedComponentInfo(string Slot, string File, int Vertices);

/// <summary>What the viewer needs to build a ped: its storage form, the default drawable per slot and the skeleton size.</summary>
public sealed record PedDescription(string Name, PedStorage Storage, string Category, IReadOnlyList<PedComponentInfo> Components, int? Bones);

/// <summary>
/// Ped models: the list of peds in the game data and, per ped, the default drawable of every component slot (the
/// lowest-numbered variation that has real geometry). Folder peds are read file by file, component peds from their
/// single <c>.ydd</c>; both end up as skinned <see cref="MeshResult"/>s keyed by slot.
/// </summary>
public sealed class PedService
{
    const int Capacity = 4;
    /// <summary>Variations with this many vertices or fewer are placeholders ("none" / hidden) and skipped.</summary>
    const int PlaceholderVertices = 8;
    /// <summary>
    /// Slots whose default variation (0) is a placeholder fall back to the next one: a placeholder hair means a bald
    /// head, whereas placeholder masks / bags / vests are meant to stay off.
    /// </summary>
    static readonly HashSet<string> FallbackSlots = new(StringComparer.OrdinalIgnoreCase) { "hair" };
    /// <summary>Variation overrides per ped (the freemode peds' shoe slot 0 is bare feet).</summary>
    static readonly Dictionary<string, Dictionary<string, int>> Preferred = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp_m_freemode_01"] = new(StringComparer.OrdinalIgnoreCase) { ["feet"] = 1 },
        ["mp_f_freemode_01"] = new(StringComparer.OrdinalIgnoreCase) { ["feet"] = 1 },
    };

    readonly AppState _state;
    readonly SkeletonService _skeletons;
    readonly ILogger<PedService> _logger;
    readonly object _sync = new();
    readonly LinkedList<(string ped, LoadedPed loaded)> _cache = new();
    IReadOnlyList<GtaToolkitGameData.PedInfo>? _list;
    GtaToolkitGameData? _listSource;
    GtaToolkitGameData? _cacheSource;

    sealed record LoadedPed(PedDescription Description, Dictionary<string, MeshResult> Meshes);

    public PedService(AppState state, SkeletonService skeletons, ILogger<PedService> logger)
    {
        _state = state;
        _skeletons = skeletons;
        _logger = logger;
    }

    GtaToolkitGameData Require()
    {
        var s = _state.Snapshot;
        if (s.State != GtaState.Ready || s.GameData == null) throw new ClipServiceException("GTA_NOT_READY", "The game data has not been indexed yet", 503);
        return s.GameData;
    }

    /// <summary>Every ped in the game data (computed once per index).</summary>
    public IReadOnlyList<GtaToolkitGameData.PedInfo> List()
    {
        var gd = Require();
        lock (_sync)
        {
            if (_list != null && ReferenceEquals(_listSource, gd)) return _list;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var list = gd.ListPeds();
        _logger.LogDebug("Ped list: {Count} peds in {Ms} ms", list.Count, sw.ElapsedMilliseconds);
        lock (_sync) { _list = list; _listSource = gd; }
        return list;
    }

    public PedDescription Describe(string ped) => Load(ped).Description;

    /// <summary>The default drawable of a slot (<c>uppr</c>), or an explicit file of a folder ped (<c>uppr_003_r</c>).</summary>
    public MeshResult Component(string ped, string component)
    {
        var loaded = Load(ped);
        if (loaded.Meshes.TryGetValue(component, out var mesh)) return mesh;
        var parsed = PedNaming.ParseFile(component);
        if (parsed != null && loaded.Description.Storage == PedStorage.Folder)
        {
            var gd = Require();
            var extracted = LoadFolderFile(gd, ped, parsed.FileName);
            if (extracted != null)
            {
                lock (_sync) loaded.Meshes[component] = extracted;
                return extracted;
            }
        }
        throw new ClipServiceException("MODEL_NOT_FOUND", $"Ped {ped} has no {component} component");
    }

    LoadedPed Load(string ped)
    {
        if (!SkeletonService.IsValidName(ped)) throw new ClipServiceException("INVALID_MODEL", "Ped names are plain file names", 400);
        var gd = Require();
        lock (_sync)
        {
            // Another game data instance (other GTA folder) invalidates everything loaded from the previous one.
            if (!ReferenceEquals(_cacheSource, gd)) { _cache.Clear(); _cacheSource = gd; }
            var node = _cache.First;
            while (node != null)
            {
                if (string.Equals(node.Value.ped, ped, StringComparison.OrdinalIgnoreCase))
                {
                    _cache.Remove(node); _cache.AddFirst(node); return node.Value.loaded;
                }
                node = node.Next;
            }
        }
        var storage = gd.PedStorageOf(ped) ?? throw new ClipServiceException("PED_NOT_FOUND", $"Ped {ped} is not in the game data");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var meshes = new Dictionary<string, MeshResult>(StringComparer.OrdinalIgnoreCase);
        var components = new List<PedComponentInfo>();
        try
        {
            if (storage == PedStorage.Folder) LoadFolderPed(gd, ped, meshes, components);
            else LoadComponentPed(gd, ped, meshes, components);
        }
        catch (ClipServiceException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Ped {Ped} failed: {Message}", ped, ex.Message);
            throw new ClipServiceException("MESH_FAILED", $"Could not read ped {ped}: {ex.Message}", 500);
        }
        var skeleton = _skeletons.TryGet(ped);
        var description = new PedDescription(ped.ToLowerInvariant(), storage, PedNaming.Category(ped) ?? "other", components, skeleton?.Bones.Count);
        _logger.LogDebug("Ped {Ped} ({Storage}): {Slots} slots in {Ms} ms", ped, storage, components.Count, sw.ElapsedMilliseconds);
        var loaded = new LoadedPed(description, meshes);
        lock (_sync)
        {
            _cache.AddFirst((ped, loaded));
            while (_cache.Count > Capacity) _cache.RemoveLast();
        }
        return loaded;
    }

    void LoadFolderPed(GtaToolkitGameData gd, string ped, Dictionary<string, MeshResult> meshes, List<PedComponentInfo> components)
    {
        var bySlot = gd.PedFolderFiles(ped)
            .Select(f => PedNaming.ParseFile(f))
            .Where(f => f != null).Select(f => f!)
            .GroupBy(f => f.Slot, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Number).ThenBy(f => f.Race ? 0 : 1).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var slot in PedNaming.Slots)
        {
            if (!bySlot.TryGetValue(slot, out var files)) continue;
            foreach (var file in Candidates(ped, slot, files, f => f.Number))
            {
                var result = LoadFolderFile(gd, ped, file.FileName);
                if (result == null || result.Mesh.VertexCount <= PlaceholderVertices) continue;
                meshes[slot] = result;
                components.Add(new PedComponentInfo(slot, file.FileName, result.Mesh.VertexCount));
                break;
            }
        }
    }

    MeshResult? LoadFolderFile(GtaToolkitGameData gd, string ped, string file)
    {
        MeshData? mesh;
        try { mesh = gd.LoadPedComponent(ped, file); }
        catch (Exception ex)
        {
            _logger.LogWarning("Ped component {Ped}/{File} failed: {Message}", ped, file, ex.Message);
            return null;
        }
        return mesh == null ? null : ToResult(ped, file, mesh);
    }

    void LoadComponentPed(GtaToolkitGameData gd, string ped, Dictionary<string, MeshResult> meshes, List<PedComponentInfo> components)
    {
        var drawables = gd.LoadPedDictionary(ped);
        var candidates = new List<(string slot, int number, string name, MeshData mesh)>();
        foreach (var (name, mesh) in drawables)
        {
            var parsed = PedNaming.ParseFile(name, ped);
            (string slot, int number)? key = parsed != null ? (parsed.Slot, parsed.Number) : null;
            if (key == null)
            {
                foreach (var sub in mesh.SubMeshes)
                {
                    if (sub.Diffuse == null) continue;
                    key = PedNaming.ParseDiffuse(sub.Diffuse);
                    if (key != null) break;
                }
            }
            if (key == null) { _logger.LogDebug("Ped {Ped}: drawable {Name} has no recognisable slot", ped, name); continue; }
            candidates.Add((key.Value.slot, key.Value.number, name, mesh));
        }
        foreach (var group in candidates.GroupBy(c => c.slot, StringComparer.OrdinalIgnoreCase))
        {
            var pick = Candidates(ped, group.Key, group.ToList(), c => c.number).FirstOrDefault(c => c.mesh.VertexCount > PlaceholderVertices);
            if (pick.mesh == null) continue;
            var file = pick.name.StartsWith("0x", StringComparison.Ordinal) ? $"{pick.slot}_{pick.number:000}" : pick.name;
            meshes[pick.slot] = ToResult(ped, file, pick.mesh);
            components.Add(new PedComponentInfo(pick.slot, file, pick.mesh.VertexCount));
        }
        components.Sort((a, b) => Array.IndexOf(PedNaming.Slots, a.Slot).CompareTo(Array.IndexOf(PedNaming.Slots, b.Slot)));
    }

    /// <summary>
    /// The variations to try for a slot, in order: the preferred number (override or 0), then — for slots that fall
    /// back — the following numbers. Slots without a fallback only ever try their default.
    /// </summary>
    static IEnumerable<T> Candidates<T>(string ped, string slot, List<T> variations, Func<T, int> number)
    {
        var wanted = Preferred.TryGetValue(ped, out var overrides) && overrides.TryGetValue(slot, out var n) ? n : 0;
        var ordered = variations.OrderBy(v => number(v) == wanted ? -1 : number(v)).ToList();
        return FallbackSlots.Contains(slot) ? ordered.Take(4) : ordered.Take(1);
    }

    static MeshResult ToResult(string ped, string file, MeshData mesh)
    {
        var key = $"ped:{ped}/{file}".ToLowerInvariant();
        var etag = "\"" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{key}|{mesh.VertexCount}|{mesh.Indices.Length}|v{MeshData.LayoutVersion}")))[..20].ToLowerInvariant() + "\"";
        return new MeshResult($"{ped}/{file}", false, etag, mesh, "ped/" + ped.ToLowerInvariant());
    }

    /// <summary>Whether the ped exists in the (indexed) game data; null while the data is not ready.</summary>
    public bool? Exists(string ped)
    {
        var s = _state.Snapshot;
        if (s.State != GtaState.Ready || s.GameData == null) return null;
        return s.GameData.PedStorageOf(ped) != null;
    }

    public void Clear()
    {
        lock (_sync) { _cache.Clear(); _list = null; _listSource = null; }
    }
}
