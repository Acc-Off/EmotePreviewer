using System.Text.Json;
using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Model;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Services;

/// <summary>
/// Skeletons by ped name: an in-memory LRU, then <c>cache/skeleton-&lt;ped&gt;.json</c> (so the viewer can show a rig
/// before the archives are indexed), then the game data. Several peds can be loaded at once (animals, the second ped
/// of a shared emote), so switching the ped in the settings never re-indexes anything.
/// </summary>
public sealed class SkeletonService
{
    const int Capacity = 8;

    readonly AppOptions _options;
    readonly AppState _state;
    readonly ILogger<SkeletonService> _logger;
    readonly object _sync = new();
    readonly LinkedList<(string ped, SkeletonDef skeleton)> _cache = new();
    GtaToolkitGameData? _gameDataSeen;

    public SkeletonService(AppOptions options, AppState state, SettingsStore settings, ILogger<SkeletonService> logger)
    {
        _options = options;
        _state = state;
        _logger = logger;
        // The status event carries the configured ped's skeleton (cached before indexing, loaded after).
        state.SkeletonProvider = () =>
        {
            try { return TryGet(settings.Current.Ped); }
            catch (ClipServiceException) { return null; }
        };
    }

    string CacheDirectory => Path.Combine(_options.DataDirectory, "cache");
    string CachePath(string ped) => Path.Combine(CacheDirectory, $"skeleton-{ped.ToLowerInvariant()}.json");

    /// <summary>The skeleton, or an error the API can report (503 while the game data is loading, 404 for an unknown ped).</summary>
    public SkeletonDef Require(string ped)
    {
        var skel = TryGet(ped, out var gameDataReady);
        if (skel != null) return skel;
        throw gameDataReady
            ? new ClipServiceException("PED_NOT_FOUND", $"No skeleton {ped}.yft in the game data")
            : new ClipServiceException("SKELETON_UNAVAILABLE", "The game data has not been indexed yet", 503);
    }

    /// <summary>The skeleton if it is cached or loadable right now; null otherwise.</summary>
    public SkeletonDef? TryGet(string ped) => TryGet(ped, out _);

    SkeletonDef? TryGet(string ped, out bool gameDataReady)
    {
        var s = _state.Snapshot;
        gameDataReady = s.State == GtaState.Ready && s.GameData != null;
        if (!IsValidName(ped)) return null;
        lock (_sync)
        {
            // A new game data instance (other GTA folder) invalidates what was loaded from the previous one.
            if (gameDataReady && !ReferenceEquals(_gameDataSeen, s.GameData)) { _cache.Clear(); _gameDataSeen = s.GameData; }
            var node = _cache.First;
            while (node != null)
            {
                if (string.Equals(node.Value.ped, ped, StringComparison.OrdinalIgnoreCase)) { _cache.Remove(node); _cache.AddFirst(node); return node.Value.skeleton; }
                node = node.Next;
            }
        }
        SkeletonDef? skel = null;
        if (gameDataReady)
        {
            try
            {
                skel = s.GameData!.LoadSkeleton(ped);
                if (skel != null) Save(skel);
                else _logger.LogWarning("Skeleton {Ped}.yft not found in the game data", ped);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Skeleton {Ped} failed: {Message}", ped, ex.Message);
                throw new ClipServiceException("SKELETON_UNREADABLE", $"Could not read {ped}.yft: {ex.Message}", 500);
            }
        }
        else skel = LoadCached(ped);
        if (skel == null) return null;
        lock (_sync)
        {
            _cache.AddFirst((ped, skel));
            while (_cache.Count > Capacity) _cache.RemoveLast();
        }
        return skel;
    }

    public static bool IsValidName(string ped) =>
        !string.IsNullOrWhiteSpace(ped) && ped.Length <= 64 && ped.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !ped.Contains('/');

    SkeletonDef? LoadCached(string ped)
    {
        var path = CachePath(ped);
        if (!File.Exists(path)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<SkeletonCache>(File.ReadAllText(path));
            if (doc?.Bones == null || doc.Name == null || doc.Version != SkeletonCache.CurrentVersion) return null;
            var bones = doc.Bones.Select((b, i) => new BoneDef(i, b.Tag, b.Name, b.Parent,
                new System.Numerics.Vector3(b.T[0], b.T[1], b.T[2]),
                new System.Numerics.Quaternion(b.R[0], b.R[1], b.R[2], b.R[3]),
                new System.Numerics.Vector3(b.S[0], b.S[1], b.S[2]),
                (BoneDofs)b.F)).ToList();
            _logger.LogDebug("Loaded cached skeleton {Ped} ({Bones} bones)", ped, bones.Count);
            return new SkeletonDef(doc.Name, bones);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Ignoring unreadable skeleton cache {Path}: {Message}", path, ex.Message);
            return null;
        }
    }

    void Save(SkeletonDef skel)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var doc = new SkeletonCache
            {
                Name = skel.Name,
                Version = SkeletonCache.CurrentVersion,
                Bones = skel.Bones.Select(b => new SkeletonCacheBone
                {
                    Tag = b.Tag, Name = b.Name, Parent = b.ParentIndex, F = (ushort)b.Dofs,
                    T = new[] { b.Translation.X, b.Translation.Y, b.Translation.Z },
                    R = new[] { b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W },
                    S = new[] { b.Scale.X, b.Scale.Y, b.Scale.Z },
                }).ToList(),
            };
            File.WriteAllText(CachePath(skel.Name), JsonSerializer.Serialize(doc));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not write the skeleton cache: {Message}", ex.Message);
        }
    }

    public void Clear()
    {
        lock (_sync) _cache.Clear();
    }

    sealed class SkeletonCache
    {
        /// <summary>Bumped when the cached fields change (2: bone DOF flags added).</summary>
        public const int CurrentVersion = 2;
        public int Version { get; set; }
        public string? Name { get; set; }
        public List<SkeletonCacheBone>? Bones { get; set; }
    }

    sealed class SkeletonCacheBone
    {
        public ushort Tag { get; set; }
        public string Name { get; set; } = "";
        public int Parent { get; set; }
        public float[] T { get; set; } = new float[3];
        public float[] R { get; set; } = new float[4];
        public float[] S { get; set; } = new float[3];
        /// <summary>Bone DOF flags.</summary>
        public ushort F { get; set; } = (ushort)BoneDofs.All;
    }
}
