using System.Security.Cryptography;
using System.Text;
using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Model;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Services;

/// <param name="TextureScope">Where the mesh's diffuse textures are resolved: <c>prop/&lt;model&gt;</c> or <c>ped/&lt;ped&gt;</c>.</param>
public sealed record MeshResult(string Model, bool Custom, string ETag, MeshData Mesh, string TextureScope);

/// <summary>
/// Serves prop meshes: resource-shipped <c>.ydr</c> files first, then the game archives. Extracted meshes are kept in
/// a small LRU so re-selecting an emote with the same prop costs nothing. Ped meshes come from <see cref="PedService"/>.
/// </summary>
public sealed class MeshService
{
    const int Capacity = 32;

    readonly AppState _state;
    readonly CatalogService _catalog;
    readonly ILogger<MeshService> _logger;
    readonly object _sync = new();
    readonly LinkedList<(string key, MeshResult mesh)> _cache = new();

    public MeshService(AppState state, CatalogService catalog, ILogger<MeshService> logger)
    {
        _state = state;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>Extracts (or returns the cached) mesh of <paramref name="model"/>.</summary>
    public MeshResult Get(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 128 || model.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ClipServiceException("INVALID_MODEL", "Model names are plain file names", 400);
        var catalog = _catalog.Catalog;
        var custom = catalog.CustomDrawables.TryGetValue(model, out var loosePath);
        var key = custom ? "file:" + loosePath!.ToLowerInvariant() : "rpf:" + model.ToLowerInvariant();

        lock (_sync)
        {
            var node = _cache.First;
            while (node != null)
            {
                if (node.Value.key == key) { _cache.Remove(node); _cache.AddFirst(node); return node.Value.mesh; }
                node = node.Next;
            }
        }

        MeshData? mesh;
        string stamp;
        try
        {
            if (custom)
            {
                if (!File.Exists(loosePath)) throw new ClipServiceException("MODEL_NOT_FOUND", $"The .ydr file {loosePath} no longer exists");
                mesh = GtaToolkitGameData.LoadLooseDrawable(loosePath!);
                var fi = new FileInfo(loosePath!);
                stamp = $"{fi.Length}-{fi.LastWriteTimeUtc.Ticks}";
            }
            else
            {
                var s = _state.Snapshot;
                if (s.State != GtaState.Ready || s.GameData == null) throw new ClipServiceException("GTA_NOT_READY", "The game data has not been indexed yet", 503);
                if (!s.GameData.HasDrawable(model)) throw new ClipServiceException("MODEL_NOT_FOUND", $"Model {model} is not in the game data (props from other resources have to be shipped as .ydr)");
                mesh = s.GameData.LoadDrawable(model);
                stamp = (s.GameData.DrawableSize(model) ?? 0).ToString();
            }
        }
        catch (ClipServiceException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Mesh {Model} failed: {Message}", model, ex.Message);
            _logger.LogDebug(ex, "Mesh failure detail");
            throw new ClipServiceException("MESH_FAILED", $"Could not read model {model}: {ex.Message}", 500);
        }
        if (mesh == null) throw new ClipServiceException("MODEL_NOT_FOUND", $"Model {model} is not in the game data");
        if (mesh.VertexCount == 0) throw new ClipServiceException("MESH_FAILED", $"Model {model} has no readable geometry: {string.Join("; ", mesh.Warnings)}", 500);

        var etag = "\"" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{key}|{stamp}|v{MeshData.LayoutVersion}")))[..20].ToLowerInvariant() + "\"";
        var result = new MeshResult(model, custom, etag, mesh, "prop/" + model.ToLowerInvariant());
        _logger.LogDebug("Mesh {Model}: {Vertices} vertices, {Triangles} triangles{Custom}", model, mesh.VertexCount, mesh.Indices.Length / 3, custom ? " (shipped)" : "");
        lock (_sync)
        {
            _cache.AddFirst((key, result));
            while (_cache.Count > Capacity) _cache.RemoveLast();
        }
        return result;
    }

    /// <summary>Whether a model can be served right now (used by the catalog to mark props as available).</summary>
    public bool IsAvailable(string model)
    {
        if (_catalog.Catalog.CustomDrawables.ContainsKey(model)) return true;
        var s = _state.Snapshot;
        return s.State == GtaState.Ready && s.GameData != null && s.GameData.HasDrawable(model);
    }

    public void Clear()
    {
        lock (_sync) _cache.Clear();
    }
}
