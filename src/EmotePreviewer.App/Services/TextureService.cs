using System.Security.Cryptography;
using System.Text;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Model;
using EmotePreviewer.Core.Textures;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Services;

/// <summary>A resolved texture and the DDS bytes served for it.</summary>
public sealed record TextureResult(string Scope, string Name, TextureImage Image, string ETag)
{
    byte[]? _dds;
    public byte[] Dds => _dds ??= Image.ToDds();
}

/// <summary>
/// Resolves diffuse textures for meshes. A prop's texture is embedded in its drawable or lives in a <c>.ytd</c> of the
/// model's name; a ped's is in <c>&lt;ped&gt;/&lt;name&gt;.ytd</c> (folder peds) or <c>&lt;ped&gt;.ytd</c> (component peds).
/// Unresolvable references (shared texture dictionaries the props reference by name) simply stay untextured.
/// </summary>
public sealed class TextureService
{
    const int Capacity = 96;

    readonly AppState _state;
    readonly MeshService _meshes;
    readonly ILogger<TextureService> _logger;
    readonly object _sync = new();
    readonly LinkedList<(string key, TextureResult? texture)> _cache = new();

    public TextureService(AppState state, MeshService meshes, ILogger<TextureService> logger)
    {
        _state = state;
        _meshes = meshes;
        _logger = logger;
    }

    /// <summary>
    /// Resolves <paramref name="name"/> for a scope (<c>prop/&lt;model&gt;</c> or <c>ped/&lt;ped&gt;</c>); null when not found.
    /// <paramref name="mode"/>: null = as stored, <c>rgba</c> = decoded, <c>gray</c> = decoded intensity for palette shaders.
    /// </summary>
    public TextureResult? Resolve(string scope, string name, string? mode = null)
    {
        var rgba = mode is "rgba" or "gray";
        var slash = scope.IndexOf('/');
        if (slash <= 0 || slash == scope.Length - 1 || name.Length == 0 || name.Length > 128 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ClipServiceException("INVALID_TEXTURE", "Texture scopes are prop/<model> or ped/<ped>, names are plain file names", 400);
        var kind = scope[..slash];
        var target = scope[(slash + 1)..];
        if (kind != "prop" && kind != "ped") throw new ClipServiceException("INVALID_TEXTURE", "Unknown texture scope " + kind, 400);
        var key = $"{kind}/{target}/{name}{(rgba ? "#" + mode : "")}".ToLowerInvariant();
        lock (_sync)
        {
            var node = _cache.First;
            while (node != null)
            {
                if (node.Value.key == key) { _cache.Remove(node); _cache.AddFirst(node); return node.Value.texture; }
                node = node.Next;
            }
        }
        TextureResult? result;
        if (rgba)
        {
            var compressed = Resolve(scope, name);
            result = compressed == null || !compressed.Image.CanDecode ? null
                : new TextureResult(compressed.Scope, compressed.Name, mode == "gray" ? compressed.Image.ToGray() : compressed.Image.DecodeToRgba(), Tag(compressed.ETag + "|" + mode));
        }
        else
        {
            try { result = kind == "prop" ? ResolveProp(target, name) : ResolvePed(target, name); }
            catch (ClipServiceException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Texture {Scope}/{Name} failed: {Message}", scope, name, ex.Message);
                result = null;
            }
            if (result == null) _logger.LogDebug("Texture {Scope}/{Name} not found", scope, name);
        }
        lock (_sync)
        {
            _cache.AddFirst((key, result));
            while (_cache.Count > Capacity) _cache.RemoveLast();
        }
        return result;
    }

    TextureResult? ResolveProp(string model, string name)
    {
        var mesh = _meshes.Get(model);
        var embedded = mesh.Mesh.FindEmbedded(name);
        if (embedded != null) return new TextureResult("prop/" + model, name, embedded, Tag($"{mesh.ETag}|{name}|embedded|{embedded.Data.Length}"));
        var s = _state.Snapshot;
        if (s.State != GtaState.Ready || s.GameData == null) return null;
        var external = s.GameData.LoadTexture(model, name);
        return external == null ? null : new TextureResult("prop/" + model, name, external, Tag($"ytd:{model}|{name}|{s.GameData.TextureDictionarySize(model)}|{external.Data.Length}"));
    }

    TextureResult? ResolvePed(string ped, string name)
    {
        var s = _state.Snapshot;
        if (s.State != GtaState.Ready || s.GameData == null) throw new ClipServiceException("GTA_NOT_READY", "The game data has not been indexed yet", 503);
        var storage = s.GameData.PedStorageOf(ped);
        if (storage == null) return null;
        var image = storage == PedStorage.Folder ? s.GameData.LoadPedTexture(ped, name) : s.GameData.LoadTexture(ped, name);
        return image == null ? null : new TextureResult("ped/" + ped, name, image, Tag($"ped:{ped}|{name}|{s.GameData.PedTextureSize(ped, name)}|{image.Data.Length}"));
    }

    /// <summary>
    /// Resolves every distinct diffuse a mesh references (used by the mesh meta so the browser knows what to fetch), with
    /// whether it should be served as a tinted intensity map: the shader lists a palette sampler (every ped shader does)
    /// and the texture content is an intensity map rather than a colour image.
    /// </summary>
    public List<(TextureResult texture, bool palette)> ForMesh(MeshResult mesh)
    {
        var result = new List<(TextureResult, bool)>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in mesh.Mesh.SubMeshes)
        {
            if (sub.Diffuse == null) continue;
            if (seen.TryGetValue(sub.Diffuse, out var index))
            {
                if (sub.Palette && index >= 0 && result[index].Item1.Image.IsIntensityMap) result[index] = (result[index].Item1, true);
                continue;
            }
            TextureResult? tex;
            try { tex = Resolve(mesh.TextureScope, sub.Diffuse); }
            catch (ClipServiceException) { tex = null; }
            if (tex == null) { seen[sub.Diffuse] = -1; continue; }
            seen[sub.Diffuse] = result.Count;
            result.Add((tex, sub.Palette && tex.Image.IsIntensityMap));
        }
        return result;
    }

    static string Tag(string identity) => "\"" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)))[..20].ToLowerInvariant() + "\"";

    public void Clear()
    {
        lock (_sync) _cache.Clear();
    }
}
