using EmotePreviewer.App.Services;
using EmotePreviewer.Core.Anim;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Model;
using EmotePreviewer.Core.Textures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace EmotePreviewer.App.Web;

public sealed record SkeletonBoneDto(int Index, ushort Tag, string Name, int Parent, float[] T, float[] R, float[] S);
public sealed record SkeletonDto(string Name, IReadOnlyList<SkeletonBoneDto> Bones);

public sealed record ClipMetaDto(
    string Dictionary,
    string Clip,
    bool Custom,
    int Fps,
    int Frames,
    float Duration,
    int BoneCount,
    bool HasRootMotion,
    int LayoutVersion,
    IReadOnlyList<int> AnimatedBones,
    IReadOnlyList<string> Warnings,
    /// <summary>Skeleton the clip was baked for (the bone indices refer to it).</summary>
    string Skeleton,
    string ETag);

public sealed record PedInfoDto(string Name, string Storage, string Category);
public sealed record PedListDto(IReadOnlyList<PedInfoDto> Peds);
public sealed record PedComponentDto(string Slot, string File, int Vertices);
public sealed record PedDto(string Name, string Storage, string Category, IReadOnlyList<PedComponentDto> Components, int? Bones);

/// <summary>Skeleton, baked clips, meshes, textures and dictionary listings.</summary>
public static class ClipEndpoints
{
    static readonly CacheControlHeaderValue PrivateHour = new() { Private = true, MaxAge = TimeSpan.FromHours(1) };

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ?ped= selects the skeleton; the configured ped is the default.
        api.MapGet("/skeleton", (string? ped, ClipService clips, SettingsStore settings) => Guard(() =>
            Results.Json(ToDto(clips.RequireSkeleton(PedName(ped, settings))), AppHost.Json)));

        api.MapGet("/emotes/{id}/clip", (string id, string? ped, ClipService clips, SettingsStore settings) => Guard(() =>
            Results.Json(Meta(clips.BakeEntry(Decode(id), EntryPed(clips, Decode(id), ped, settings))), AppHost.Json)));
        api.MapGet("/emotes/{id}/clip.bin", (string id, string? ped, HttpContext ctx, ClipService clips, SettingsStore settings) => Guard(() =>
            Binary(ctx, clips.BakeEntry(Decode(id), EntryPed(clips, Decode(id), ped, settings)))));

        api.MapGet("/clips/{dict}/{clip}", (string dict, string clip, string? ped, ClipService clips, SettingsStore settings) => Guard(() =>
            Results.Json(Meta(clips.BakeClip(Decode(dict), Decode(clip), PedName(ped, settings))), AppHost.Json)));
        api.MapGet("/clips/{dict}/{clip}.bin", (string dict, string clip, string? ped, HttpContext ctx, ClipService clips, SettingsStore settings) => Guard(() =>
            Binary(ctx, clips.BakeClip(Decode(dict), Decode(clip), PedName(ped, settings)))));

        api.MapGet("/props/{model}", (string model, MeshService meshes, TextureService textures) => Guard(() => Results.Json(MeshMeta(meshes.Get(Decode(model)), textures), AppHost.Json)));
        api.MapGet("/props/{model}.bin", (string model, HttpContext ctx, MeshService meshes) => Guard(() => MeshBinary(ctx, meshes.Get(Decode(model)))));

        // Ped models: the list, one ped's default components, and the component meshes (/api/ped/mp_m_freemode_01/uppr, or an explicit file such as uppr_003_r).
        api.MapGet("/peds", (PedService peds) => Guard(() =>
            Results.Json(new PedListDto(peds.List().Select(p => new PedInfoDto(p.Name, StorageName(p.Storage), p.Category)).ToList()), AppHost.Json)));
        api.MapGet("/peds/{ped}", (string ped, PedService peds) => Guard(() =>
        {
            var d = peds.Describe(Decode(ped));
            return Results.Json(new PedDto(d.Name, StorageName(d.Storage), d.Category, d.Components.Select(c => new PedComponentDto(c.Slot, c.File, c.Vertices)).ToList(), d.Bones), AppHost.Json);
        }));
        api.MapGet("/ped/{ped}/{component}", (string ped, string component, PedService peds, TextureService textures) => Guard(() =>
            Results.Json(MeshMeta(peds.Component(Decode(ped), Decode(component)), textures), AppHost.Json)));
        api.MapGet("/ped/{ped}/{component}.bin", (string ped, string component, HttpContext ctx, PedService peds) => Guard(() =>
            MeshBinary(ctx, peds.Component(Decode(ped), Decode(component)))));

        // Diffuse textures as DDS: /api/textures/prop/<model>/<name>.dds or /api/textures/ped/<ped>/<name>.dds (?format=rgba decodes BC1/BC3 on the server).
        api.MapGet("/textures/{kind}/{target}/{name}.dds", (string kind, string target, string name, string? format, HttpContext ctx, TextureService textures) => Guard(() =>
        {
            var mode = format?.ToLowerInvariant() is "rgba" or "gray" ? format!.ToLowerInvariant() : null;
            var tex = textures.Resolve($"{kind}/{Decode(target)}", Decode(name), mode)
                ?? throw new ClipServiceException("TEXTURE_NOT_FOUND", $"Texture {name} was not found for {kind} {target}");
            var headers = ctx.Response.GetTypedHeaders();
            headers.CacheControl = PrivateHour;
            headers.ETag = new EntityTagHeaderValue(tex.ETag);
            if (Matches(ctx, tex.ETag)) return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(tex.Dds, "image/vnd-ms.dds");
        }));

        api.MapGet("/dictionaries/{name}/clips", (string name, ClipService clips) => Guard(() =>
        {
            var (dict, custom, _) = clips.GetDictionary(Decode(name));
            var list = dict.Clips.Select(c => new ClipInfoDto(c.Name, c.Duration, c.Tracks.Count)).ToList();
            return Results.Json(new DictionaryClipsDto(dict.Name, custom, list), AppHost.Json);
        }));
    }

    /// <summary>Route values may arrive with encoded slashes (ids look like <c>source/category/command</c>).</summary>
    static string Decode(string value) => value.Contains('%') ? Uri.UnescapeDataString(value) : value;

    static string StorageName(PedStorage storage) => storage == PedStorage.Folder ? "folder" : "component";

    /// <summary>The requested ped or the configured one.</summary>
    static string PedName(string? ped, SettingsStore settings)
    {
        if (string.IsNullOrWhiteSpace(ped)) return settings.Current.Ped;
        if (!SkeletonService.IsValidName(ped)) throw new ClipServiceException("INVALID_MODEL", "Ped names are plain file names", 400);
        return ped;
    }

    /// <summary>Animal entries default to their animal ped; everything else to the configured ped.</summary>
    static string EntryPed(ClipService clips, string id, string? ped, SettingsStore settings)
    {
        if (!string.IsNullOrWhiteSpace(ped)) return PedName(ped, settings);
        var entry = clips.RequireEntry(id);
        return entry.AnimalPed ?? settings.Current.Ped;
    }

    static IResult Guard(Func<IResult> body)
    {
        try { return body(); }
        catch (ClipServiceException ex)
        {
            return Results.Json(ApiError.Of(ex.Code, ex.Message), AppHost.Json, statusCode: ex.Status);
        }
    }

    static bool Matches(HttpContext ctx, string etag)
    {
        var ifNoneMatch = ctx.Request.Headers.IfNoneMatch;
        return ifNoneMatch.Count > 0 && ifNoneMatch.Any(v => v != null && v.Split(',').Select(s => s.Trim()).Contains(etag));
    }

    static ClipMetaDto Meta(BakedClipResult r) => new(r.Dictionary, r.ClipName, r.Custom, r.Baked.Fps, r.Baked.Frames, r.Baked.Duration,
        r.Baked.BoneCount, r.Baked.HasRootMotion, BakedClip.LayoutVersion, r.Baked.AnimatedBones, r.Baked.Warnings, r.Skeleton, r.ETag);

    static IResult Binary(HttpContext ctx, BakedClipResult r)
    {
        var headers = ctx.Response.GetTypedHeaders();
        headers.CacheControl = PrivateHour;
        headers.ETag = new EntityTagHeaderValue(r.ETag);
        ctx.Response.Headers["X-Clip-Frames"] = r.Baked.Frames.ToString();
        ctx.Response.Headers["X-Clip-Fps"] = r.Baked.Fps.ToString();
        if (Matches(ctx, r.ETag)) return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(r.Baked.ToBytes(), "application/octet-stream");
    }

    static MeshMetaDto MeshMeta(MeshResult r, TextureService textures)
    {
        var resolved = textures.ForMesh(r);
        return new MeshMetaDto(r.Model, r.Custom, r.Mesh.VertexCount, r.Mesh.Indices.Length, r.Mesh.Normals != null, r.Mesh.Uvs != null, r.Mesh.IsSkinned,
            r.Mesh.SubMeshes.Select(s => new MeshSubMeshDto(s.IndexStart, s.IndexCount, s.ShaderHash, s.Diffuse, s.ShaderName, s.Cutout, s.Hidden, s.Cloth)).ToList(),
            new[] { r.Mesh.BoundsMin.X, r.Mesh.BoundsMin.Y, r.Mesh.BoundsMin.Z }, new[] { r.Mesh.BoundsMax.X, r.Mesh.BoundsMax.Y, r.Mesh.BoundsMax.Z },
            r.Mesh.Warnings, MeshData.LayoutVersion, r.ETag, r.TextureScope,
            resolved.Select(r => new MeshTextureDto(r.texture.Name, TextureImage.FormatName(r.texture.Image.Format), r.texture.Image.Width, r.texture.Image.Height, r.texture.Image.HasAlpha, r.texture.Image.CanDecode, r.palette, r.texture.ETag)).ToList());
    }

    static IResult MeshBinary(HttpContext ctx, MeshResult r)
    {
        var headers = ctx.Response.GetTypedHeaders();
        headers.CacheControl = PrivateHour;
        headers.ETag = new EntityTagHeaderValue(r.ETag);
        if (Matches(ctx, r.ETag)) return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(r.Mesh.ToBytes(), "application/octet-stream");
    }

    static SkeletonDto ToDto(SkeletonDef skel) => new(skel.Name, skel.Bones.Select(b => new SkeletonBoneDto(
        b.Index, b.Tag, b.Name, b.ParentIndex,
        new[] { b.Translation.X, b.Translation.Y, b.Translation.Z },
        new[] { b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W },
        new[] { b.Scale.X, b.Scale.Y, b.Scale.Z })).ToList());
}
