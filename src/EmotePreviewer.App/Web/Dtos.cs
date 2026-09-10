using System.Reflection;
using EmotePreviewer.App.Services;
using EmotePreviewer.Core.Catalog;

namespace EmotePreviewer.App.Web;

/// <summary>Error envelope: <c>{ "error": { "code": "...", "message": "..." } }</c>. Codes drive the UI messages.</summary>
public sealed record ApiError(ApiErrorBody Error)
{
    public static ApiError Of(string code, string message) => new(new ApiErrorBody(code, message));
}

public sealed record ApiErrorBody(string Code, string Message);

public static class AppVersion
{
    public static readonly string Value =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}

/// <summary><c>GET /api/status</c> and the SSE <c>status</c> event.</summary>
public sealed record StatusDto(
    string Version,
    string Gta,
    string? Message,
    int ProgressDone,
    int ProgressTotal,
    string? GtaFolder,
    string? KeysFolder,
    bool GtaDetected,
    int ClipDictionaries,
    int CatalogEntries,
    int? SkeletonBones,
    /// <summary>Name of the configured ped's skeleton when it is available (cached or loaded); null otherwise.</summary>
    string? Skeleton)
{
    public static string StateName(GtaState state) => state switch
    {
        GtaState.MissingGta => "missingGta",
        GtaState.MissingKeys => "missingKeys",
        GtaState.Indexing => "indexing",
        GtaState.Ready => "ready",
        _ => "error",
    };

    public static StatusDto From(GameDataSnapshot s, int catalogEntries, Core.Model.SkeletonDef? skeleton) => new(
        AppVersion.Value, StateName(s.State), s.Message, s.ProgressDone, s.ProgressTotal, s.GtaFolder, s.KeysFolder, s.GtaDetected,
        s.ClipDictionaries, catalogEntries, skeleton?.Bones.Count, skeleton?.Name);
}

/// <param name="Available">True when a mesh can be served (shipped .ydr or game data), false when not, null before indexing.</param>
public sealed record PropDto(string Model, int Bone, float[] Placement, bool? Available);

/// <summary>
/// Where the second ped of a shared emote goes, relative to the main (selected) ped. Both menus move the initiating
/// ped to its own offset in the other ped's frame and attach whichever ped's emote carries an attachment, so the
/// resolution below reads the main entry's placement first and the partner's second.
/// </summary>
/// <param name="Kind"><c>offset</c> or <c>attach</c>.</param>
/// <param name="Position">offset: the partner's entity origin in the main ped's frame (x right, y forward, z up; metres).</param>
/// <param name="Heading">offset: the partner's heading relative to the main ped's (degrees, counter-clockwise; 180 = facing it).</param>
/// <param name="Attached">attach: <c>partner</c> when the partner hangs on the main ped's bone, <c>main</c> when the main ped hangs on the partner's.</param>
/// <param name="Bone">attach: bone tag on the anchor ped; -1 = its entity origin.</param>
/// <param name="Offset">attach: [x, y, z, rotX, rotY, rotZ] from the bone, the prop convention (rotation order Y, Z, X).</param>
/// <param name="Explicit">False when neither side gives a placement and the default (facing each other 1 m apart) is used.</param>
public sealed record PartnerPlacementDto(string Kind, float[]? Position, float? Heading, string? Attached, int? Bone, float[]? Offset, bool Explicit)
{
    public static readonly PartnerPlacementDto Default = new("offset", new[] { 0f, 1f, 0f }, 180f, null, null, null, false);

    public static PartnerPlacementDto Resolve(SharedPlacement? main, SharedPlacement? partner)
    {
        if (main is { Kind: PlacementKind.Attach }) return new("attach", null, null, "main", main.Bone, main.Placement, true);
        if (partner is { Kind: PlacementKind.Attach }) return new("attach", null, null, "partner", partner.Bone, partner.Placement, true);
        if (main is { Kind: PlacementKind.Offset })
        {
            // The main ped stands at (side, front, height) in the partner's frame turned by -heading, so seen from the
            // main ped the partner sits at -Rz(heading) * (side, front, height) and is turned by +heading.
            var w = main.Heading * MathF.PI / 180f;
            var (sin, cos) = MathF.SinCos(w);
            var x = -(main.Side * cos - main.Front * sin);
            var y = -(main.Side * sin + main.Front * cos);
            return new("offset", new[] { Round(x), Round(y), Round(-main.Height) }, main.Heading, null, null, null, true);
        }
        if (partner is { Kind: PlacementKind.Offset })
            return new("offset", new[] { partner.Side, partner.Front, partner.Height }, -partner.Heading, null, null, null, true);
        return Default;
    }

    static float Round(float v) => MathF.Abs(v) < 1e-6f ? 0f : MathF.Round(v, 5);
}

/// <summary>The other half of a shared emote, with what the viewer needs to show it on a second ped.</summary>
/// <param name="Ped">The animal ped the partner clip is meant for; null for human partners (the configured partner ped).</param>
public sealed record PartnerDto(
    string Id,
    string Command,
    string Label,
    string? Dictionary,
    string? Clip,
    bool Loop,
    int? DurationMs,
    int StartDelayMs,
    IReadOnlyList<PropDto> Props,
    bool Previewable,
    string? PreviewReason,
    string? Ped,
    PartnerPlacementDto Placement);

/// <summary>One catalog entry as sent to the browser. Internal paths are omitted.</summary>
public sealed record EmoteDto(
    string Id,
    string Source,
    string Category,
    string Command,
    string Label,
    string Kind,
    string? Dictionary,
    string? Clip,
    string? Name,
    bool Loop,
    bool Move,
    int? DurationMs,
    string? ExitEmote,
    IReadOnlyList<PropDto> Props,
    bool Custom,
    bool Previewable,
    /// <summary><c>not-indexed</c>, <c>kind</c>, <c>animal</c>, <c>no-dictionary</c>, <c>no-clip</c> or null when previewable.</summary>
    string? PreviewReason,
    /// <summary>The animal ped the clip is meant for; null for human emotes (played on the configured ped).</summary>
    string? Ped,
    /// <summary>Milliseconds this side starts after the other (rpemotes <c>StartDelay</c>, scully <c>Delay</c>).</summary>
    int StartDelayMs,
    /// <summary>The other side's command for shared emotes (also set when it did not resolve); null for solo emotes.</summary>
    string? PartnerCommand,
    /// <summary>The resolved partner entry; null for solo emotes and for partner commands that are not in the catalog.</summary>
    PartnerDto? Partner)
{
    public static string KindName(EmoteKind kind) => kind switch
    {
        EmoteKind.Animation => "animation",
        EmoteKind.Scenario => "scenario",
        EmoteKind.Walk => "walk",
        _ => "expression",
    };
}

public sealed record CatalogSourceDto(string Id, string Path, string Origin, string? Ref, string Kind, int Entries);

/// <summary><c>GET /api/catalog</c>.</summary>
public sealed record CatalogDto(
    IReadOnlyList<EmoteDto> Entries,
    IReadOnlyList<CatalogSourceDto> Sources,
    IReadOnlyList<string> Warnings,
    /// <summary>False until the game data has been indexed and every entry's previewability is known.</summary>
    bool PreviewResolved,
    /// <summary>Changes whenever the catalog is rebuilt or re-resolved; the UI compares it before re-fetching.</summary>
    long Revision);

public sealed record ClipInfoDto(string Name, float Duration, int Tracks);

public sealed record DictionaryClipsDto(string Dictionary, bool Custom, IReadOnlyList<ClipInfoDto> Clips);

public sealed record DiagnosticsDto(
    string Version,
    string DataDirectory,
    string SettingsPath,
    string LogPath,
    string? GtaFolder,
    bool GtaDetected,
    string? KeysFolder,
    IReadOnlyList<string> MissingKeyFiles,
    string Gta,
    int Archives,
    int ClipDictionaries,
    long IndexMilliseconds,
    int? SkeletonBones,
    IReadOnlyList<CatalogSourceDto> Resources,
    int CatalogEntries,
    IReadOnlyList<string> CatalogWarnings,
    string Url);

public sealed record FolderDialogRequest(string? Initial, string? Title);
public sealed record FolderDialogResult(string? Path);

public sealed record AddResourceRequest(string Origin, string? Path, string? Id, string? Repository, string? Ref);
public sealed record EnableResourceRequest(bool Enabled);

public sealed record ResourceItemDto(
    string Id,
    string Path,
    string Origin,
    string? Ref,
    string? Repository,
    bool Enabled,
    string Kind,
    int Entries,
    bool Exists,
    ResourceSourceInfo? Source,
    ResourceJobDto? Job);

public sealed record ResourceTemplateDto(string Id, string Repository, string Ref, string Kind, string Description, bool Installed);

/// <summary><c>GET /api/resources</c>: configured resources with their state, and the one-click templates.</summary>
public sealed record ResourcesDto(IReadOnlyList<ResourceItemDto> Resources, IReadOnlyList<ResourceTemplateDto> Templates)
{
    public static ResourcesDto Build(ResourceManager manager, AppSettings settings, CatalogDto catalog)
    {
        var jobs = manager.Jobs.ToDictionary(j => j.Id, StringComparer.OrdinalIgnoreCase);
        var sources = catalog.Sources.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var items = settings.Resources.Select(r =>
        {
            var path = r.Origin == "github" ? manager.ResourceFolder(r.Id) : r.Path;
            sources.TryGetValue(r.Id, out var src);
            jobs.TryGetValue(r.Id, out var job);
            var kind = src?.Kind ?? (Directory.Exists(path) ? ResourceSource.DetectKind(ResourceSource.ResolveRoot(path)) switch { ResourceKind.RpEmotes => "rpemotes", ResourceKind.Scully => "scully", _ => "unknown" } : "unknown");
            return new ResourceItemDto(r.Id, path, r.Origin, r.Ref, r.Repository, r.Enabled, kind, src?.Entries ?? 0, Directory.Exists(path),
                r.Origin == "github" ? manager.ReadSourceInfo(r.Id) : null, job);
        }).ToList();
        // Downloads that are not in the settings yet (first fetch in progress) show up as pending items.
        foreach (var job in jobs.Values.Where(j => !settings.Resources.Any(r => string.Equals(r.Id, j.Id, StringComparison.OrdinalIgnoreCase))))
            items.Add(new ResourceItemDto(job.Id, manager.ResourceFolder(job.Id), "github", null, null, true, "unknown", 0, false, null, job));
        var templates = ResourceManager.Templates.Select(t => new ResourceTemplateDto(t.Id, t.Repository, t.Ref, t.Kind, t.Description,
            settings.Resources.Any(r => string.Equals(r.Repository, t.Repository, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Id, t.Id, StringComparison.OrdinalIgnoreCase)))).ToList();
        return new ResourcesDto(items, templates);
    }
}

/// <param name="Diffuse">Name of the diffuse texture (see <see cref="MeshMetaDto.Textures"/> for the ones that resolved); null when the shader has none.</param>
/// <param name="Shader">Shader name when known (<c>ped</c>, <c>ped_hair_spiked</c>, <c>normal_spec</c> …).</param>
/// <param name="Cutout">Whether the shader discards by diffuse alpha; null for unknown shaders (the browser then guesses from the texture format).</param>
/// <param name="Hidden">Geometry the game keeps out of the colour pass (hair hulls of the secondary hair pass); the browser does not draw it.</param>
/// <param name="Cloth">Geometry the game moves with its cloth simulation (jackets of the story peds); skinned rigidly here, and the viewer can hide it.</param>
public sealed record MeshSubMeshDto(int IndexStart, int IndexCount, uint ShaderHash, string? Diffuse, string? Shader, bool? Cutout, bool Hidden, bool Cloth);

/// <summary>A diffuse texture that can be fetched as <c>/api/textures/{textureScope}/{name}.dds?v={eTag}</c>.</summary>
/// <param name="Format"><c>bc1</c> / <c>bc2</c> / <c>bc3</c> / <c>bc7</c> / <c>rgba8</c> …</param>
/// <param name="Decodable">True when <c>?format=rgba</c> / <c>gray</c> is available (BC1 / BC3 / RGBA8 sources).</param>
/// <param name="Palette">True when a palette shader uses the texture: the browser fetches it as <c>?format=gray</c> and tints it.</param>
public sealed record MeshTextureDto(string Name, string Format, int Width, int Height, bool Alpha, bool Decodable, bool Palette, string ETag);

/// <summary><c>GET /api/props/{model}</c> and <c>/api/ped/{ped}/{component}</c>: describes the <c>.bin</c> layout (positions, normals?, uvs?, blend indices/weights?, indices) and the textures.</summary>
public sealed record MeshMetaDto(
    string Model,
    bool Custom,
    int VertexCount,
    int IndexCount,
    bool HasNormals,
    bool HasUvs,
    bool Skinned,
    IReadOnlyList<MeshSubMeshDto> SubMeshes,
    float[] BoundsMin,
    float[] BoundsMax,
    IReadOnlyList<string> Warnings,
    int LayoutVersion,
    string ETag,
    /// <summary><c>prop/&lt;model&gt;</c> or <c>ped/&lt;ped&gt;</c>: the path segment textures are fetched under.</summary>
    string TextureScope,
    IReadOnlyList<MeshTextureDto> Textures);
