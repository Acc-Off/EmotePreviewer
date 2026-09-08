using System.Security.Cryptography;
using System.Text;
using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Anim;
using EmotePreviewer.Core.Catalog;
using EmotePreviewer.Core.Model;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Services;

/// <summary>Thrown by <see cref="ClipService"/> with an API error code the endpoint maps to a status.</summary>
public sealed class ClipServiceException : Exception
{
    public ClipServiceException(string code, string message, int status = 404) : base(message)
    {
        Code = code;
        Status = status;
    }

    public string Code { get; }
    public int Status { get; }
}

/// <summary>A baked clip plus the identity used for HTTP caching.</summary>
public sealed record BakedClipResult(string Dictionary, string ClipName, bool Custom, string ETag, BakedClip Baked, string Skeleton);

/// <summary>
/// Loads clip dictionaries (game archives or resource-shipped .ycd files) through a small LRU cache and bakes clips
/// with <see cref="ClipBaker"/>. Baked results are cached too so ETag revalidation costs nothing.
/// </summary>
public sealed class ClipService
{
    const int DictionaryCapacity = 12;
    const int BakedCapacity = 24;

    readonly AppState _state;
    readonly CatalogService _catalog;
    readonly SkeletonService _skeletons;
    readonly ILogger<ClipService> _logger;
    readonly object _sync = new();
    readonly LinkedList<(string key, IClipDictionary dict)> _dicts = new();
    readonly LinkedList<(string key, BakedClipResult baked)> _baked = new();

    public ClipService(AppState state, CatalogService catalog, SkeletonService skeletons, ILogger<ClipService> logger)
    {
        _state = state;
        _catalog = catalog;
        _skeletons = skeletons;
        _logger = logger;
    }

    /// <summary>The skeleton of a ped (from the game data or the cache) or an error when nothing is available yet.</summary>
    public SkeletonDef RequireSkeleton(string ped) => _skeletons.Require(ped);

    public EmoteEntry RequireEntry(string id) =>
        _catalog.Catalog.FindById(id) ?? throw new ClipServiceException("EMOTE_NOT_FOUND", $"No emote with id '{id}'");

    /// <summary>Resolves a dictionary by name: a resource-shipped .ycd first, then the game archives.</summary>
    public (IClipDictionary dict, bool custom, string identity) GetDictionary(string name)
    {
        var catalog = _catalog.Catalog;
        if (catalog.CustomYcds.TryGetValue(name, out var path))
            return (GetLooseDictionary(path), true, path);
        return (GetGameDictionary(name), false, "rpf:" + name);
    }

    public IClipDictionary GetDictionaryFor(EmoteEntry entry)
    {
        if (!entry.HasClip)
            throw new ClipServiceException("NOT_PREVIEWABLE", $"'{entry.Id}' is a {entry.Kind} entry and has no animation dictionary", 409);
        return entry.IsCustom && entry.CustomYcdPath != null ? GetLooseDictionary(entry.CustomYcdPath) : GetGameDictionary(entry.Dictionary!);
    }

    IClipDictionary GetLooseDictionary(string path)
    {
        // The size and mtime are part of the key, so a .ycd rewritten in place is read again instead of served stale.
        var key = "file:" + path.ToLowerInvariant() + "|" + FileStamp(path);
        return Cached(key, () =>
        {
            if (!File.Exists(path)) throw new ClipServiceException("DICTIONARY_NOT_FOUND", $"The .ycd file {path} no longer exists");
            try { return GtaToolkitGameData.LoadLooseDictionary(path); }
            catch (Exception ex) { throw new ClipServiceException("DICTIONARY_UNREADABLE", $"Could not read {Path.GetFileName(path)}: {ex.Message}", 500); }
        });
    }

    IClipDictionary GetGameDictionary(string name)
    {
        var s = _state.Snapshot;
        if (s.State != GtaState.Ready || s.GameData == null)
            throw new ClipServiceException("GTA_NOT_READY", "The game data has not been indexed yet", 503);
        var key = "rpf:" + name.ToLowerInvariant();
        return Cached(key, () =>
        {
            IClipDictionary? dict;
            try { dict = s.GameData.LoadClipDictionary(name); }
            catch (Exception ex) { throw new ClipServiceException("DICTIONARY_UNREADABLE", $"Could not read dictionary {name}: {ex.Message}", 500); }
            return dict ?? throw new ClipServiceException("DICTIONARY_NOT_FOUND", $"Dictionary {name} is not in the game data");
        });
    }

    /// <summary>
    /// Loads run under one lock: the meta and .bin requests of the same clip arrive together, and the archive layer is
    /// single-threaded anyway, so serialising here avoids loading (and baking) the same dictionary twice.
    /// </summary>
    readonly object _loadLock = new();

    IClipDictionary Cached(string key, Func<IClipDictionary> load)
    {
        lock (_loadLock)
        {
            lock (_sync)
            {
                var node = _dicts.First;
                while (node != null)
                {
                    if (node.Value.key == key)
                    {
                        _dicts.Remove(node);
                        _dicts.AddFirst(node);
                        return node.Value.dict;
                    }
                    node = node.Next;
                }
            }
            var dict = load();
            lock (_sync)
            {
                _dicts.AddFirst((key, dict));
                while (_dicts.Count > DictionaryCapacity) _dicts.RemoveLast();
            }
            return dict;
        }
    }

    /// <summary>Bakes the clip of a catalog entry onto the given ped's skeleton (null = the entry's animal ped or the configured ped).</summary>
    public BakedClipResult BakeEntry(string id, string ped)
    {
        var entry = RequireEntry(id);
        var dict = GetDictionaryFor(entry);
        var identity = entry.IsCustom ? entry.CustomYcdPath! : "rpf:" + entry.Dictionary;
        return Bake(dict, entry.Clip ?? "", entry.IsCustom, identity, ped);
    }

    /// <summary>Bakes an arbitrary clip of a dictionary (manual selection from the dictionary listing).</summary>
    public BakedClipResult BakeClip(string dictionaryName, string clipName, string ped)
    {
        var (dict, custom, identity) = GetDictionary(dictionaryName);
        return Bake(dict, clipName, custom, identity, ped);
    }

    BakedClipResult Bake(IClipDictionary dict, string clipName, bool custom, string identity, string ped)
    {
        lock (_loadLock) return BakeLocked(dict, clipName, custom, identity, ped);
    }

    BakedClipResult BakeLocked(IClipDictionary dict, string clipName, bool custom, string identity, string ped)
    {
        var skeleton = RequireSkeleton(ped);
        var clip = dict.FindClip(clipName) ?? throw new ClipServiceException("CLIP_NOT_FOUND", $"Clip '{clipName}' is not in dictionary {dict.Name}");
        var key = $"{identity}|{clip.Name}|{skeleton.Name}|{skeleton.Bones.Count}|v{BakedClip.LayoutVersion}";
        lock (_sync)
        {
            var node = _baked.First;
            while (node != null)
            {
                if (node.Value.key == key) { _baked.Remove(node); _baked.AddFirst(node); return node.Value.baked; }
                node = node.Next;
            }
        }
        BakedClip baked;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            baked = ClipBaker.Bake(clip, skeleton);
            _logger.LogDebug("Baked {Dictionary}/{Clip}: {Frames} frames @ {Fps} fps, {Bytes} KB in {Ms} ms", dict.Name, clip.Name, baked.Frames, baked.Fps, baked.TotalFloats * 4 / 1024, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Baking {Dictionary}/{Clip} failed: {Message}", dict.Name, clip.Name, ex.Message);
            _logger.LogDebug(ex, "Bake failure detail");
            throw new ClipServiceException("BAKE_FAILED", $"Could not decode {dict.Name}/{clip.Name}: {ex.Message}", 500);
        }
        var etag = "\"" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key + "|" + FileStamp(identity))))[..20].ToLowerInvariant() + "\"";
        var result = new BakedClipResult(dict.Name, clip.Name, custom, etag, baked, skeleton.Name);
        lock (_sync)
        {
            _baked.AddFirst((key, result));
            while (_baked.Count > BakedCapacity) _baked.RemoveLast();
        }
        return result;
    }

    /// <summary>Size + mtime for loose files, archive entry size for game dictionaries: enough to invalidate the ETag when the source changes.</summary>
    string FileStamp(string identity)
    {
        if (identity.StartsWith("rpf:", StringComparison.Ordinal))
            return (_state.Snapshot.GameData?.ClipDictionarySize(identity[4..]) ?? 0).ToString();
        try
        {
            var fi = new FileInfo(identity);
            return fi.Exists ? $"{fi.Length}-{fi.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch (IOException) { return "unknown"; }
    }

    /// <summary>Drops every cached dictionary and baked clip (after the game data or resources changed).</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _dicts.Clear();
            _baked.Clear();
        }
    }

    /// <summary>Drops the cached dictionaries and baked clips that were read from files under <paramref name="folder"/>. Returns how many entries went.</summary>
    public int Invalidate(string folder)
    {
        var prefix = Path.GetFullPath(folder).TrimEnd('\\', '/').ToLowerInvariant() + Path.DirectorySeparatorChar;
        int removed = 0;
        lock (_sync)
        {
            removed += RemoveWhere(_dicts, e => e.key.StartsWith("file:", StringComparison.Ordinal) && e.key.AsSpan(5).StartsWith(prefix));
            removed += RemoveWhere(_baked, e => !e.key.StartsWith("rpf:", StringComparison.Ordinal) && e.key.ToLowerInvariant().StartsWith(prefix, StringComparison.Ordinal));
        }
        return removed;
    }

    static int RemoveWhere<T>(LinkedList<T> list, Func<T, bool> predicate)
    {
        int removed = 0;
        var node = list.First;
        while (node != null)
        {
            var next = node.Next;
            if (predicate(node.Value)) { list.Remove(node); removed++; }
            node = next;
        }
        return removed;
    }
}
