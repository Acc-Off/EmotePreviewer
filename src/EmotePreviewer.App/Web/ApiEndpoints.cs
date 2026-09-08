using EmotePreviewer.App.Services;
using EmotePreviewer.Core.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Web;

/// <summary>State, settings, catalog and housekeeping endpoints under <c>/api</c>.</summary>
public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (AppState state) => Results.Json(state.BuildStatus(state.Snapshot), AppHost.Json));

        api.MapGet("/events", (HttpContext context, EventHub hub, AppState state, CatalogService catalog, IHostApplicationLifetime lifetime) =>
            hub.ServeAsync(context, () => new (string, object)[]
            {
                ("status", state.BuildStatus(state.Snapshot)),
                ("catalog", new { revision = catalog.Dto.Revision, entries = catalog.Dto.Entries.Count, previewResolved = catalog.Dto.PreviewResolved }),
            }.Concat(context.RequestServices.GetRequiredService<ResourceManager>().Jobs.Select(j => ("resource", (object)j))), lifetime.ApplicationStopping));

        api.MapGet("/catalog", (CatalogService catalog) => Results.Json(catalog.Dto, AppHost.Json));

        api.MapGet("/settings", (SettingsStore settings) => Results.Json(settings.Current, AppHost.Json));

        api.MapPut("/settings", (AppSettings incoming, SettingsStore settings, AppState state, CatalogService catalog, ClipService clips, MeshService meshes, TextureService textures, PedService peds, SkeletonService skeletons, ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("Settings");
            var before = settings.Current;
            var next = SettingsStore.Normalize(incoming);
            if (next.GtaFolder != null && !Core.Gta.GtaLocator.IsGtaFolder(next.GtaFolder))
                return Results.Json(ApiError.Of("INVALID_GTA_FOLDER", $"GTA5.exe was not found in {next.GtaFolder}"), AppHost.Json, statusCode: StatusCodes.Status400BadRequest);
            if (next.KeysFolder != null && !Directory.Exists(next.KeysFolder))
                return Results.Json(ApiError.Of("INVALID_KEYS_FOLDER", $"The folder {next.KeysFolder} does not exist"), AppHost.Json, statusCode: StatusCodes.Status400BadRequest);
            foreach (var r in next.Resources.Where(r => r.Origin == "folder"))
            {
                if (!Directory.Exists(r.Path))
                    return Results.Json(ApiError.Of("INVALID_RESOURCE_FOLDER", $"The folder {r.Path} does not exist"), AppHost.Json, statusCode: StatusCodes.Status400BadRequest);
            }
            settings.Save(next);
            logger.LogInformation("Settings saved to {Path}", settings.Path);

            var gameDataChanged = before.GtaFolder != next.GtaFolder || before.KeysFolder != next.KeysFolder;
            var resourcesChanged = !ResourcesEqual(before.Resources, next.Resources);
            if (gameDataChanged || resourcesChanged) { clips.Clear(); meshes.Clear(); textures.Clear(); }
            if (gameDataChanged) { peds.Clear(); skeletons.Clear(); }
            if (resourcesChanged) catalog.Rebuild();
            if (gameDataChanged) state.Restart();
            // A different default ped changes the status' skeleton; the viewer keys its rig on it.
            else if (before.Ped != next.Ped) state.PublishStatus();
            return Results.Json(settings.Current, AppHost.Json);
        });

        api.MapGet("/diagnostics", (AppState state, CatalogService catalog, SettingsStore settings, HostPaths paths, ListenerInfo listener) =>
        {
            var s = state.Snapshot;
            var keys = state.ResolveKeysFolder();
            var gta = state.ResolveGtaFolder(out var detected);
            return Results.Json(new DiagnosticsDto(
                AppVersion.Value, paths.DataDirectory, settings.Path, paths.LogPath ?? "", gta, detected, keys,
                Directory.Exists(keys) ? AppState.MissingKeyFiles(keys) : Core.Adapters.GtaToolkit.GtaKeys.RequiredFiles,
                StatusDto.StateName(s.State), s.Archives, s.ClipDictionaries, s.IndexMilliseconds, state.SkeletonProvider?.Invoke()?.Bones.Count,
                catalog.Dto.Sources, catalog.Catalog.Entries.Count, catalog.Catalog.Warnings, listener.Url), AppHost.Json);
        });

        api.MapGet("/resources", (ResourceManager resources, SettingsStore settings, CatalogService catalog) =>
            Results.Json(ResourcesDto.Build(resources, settings.Current, catalog.Dto), AppHost.Json));

        api.MapPost("/resources", (AddResourceRequest request, ResourceManager resources) => GuardResource(() =>
        {
            if (string.Equals(request.Origin, "github", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(request.Repository))
                    return Results.Json(ApiError.Of("INVALID_REPOSITORY", "repository is required for github resources"), AppHost.Json, statusCode: StatusCodes.Status400BadRequest);
                var id = resources.AddGitHub(request.Repository.Trim(), request.Ref, request.Id);
                return Results.Json(new { id, started = true }, AppHost.Json, statusCode: StatusCodes.Status202Accepted);
            }
            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(ApiError.Of("INVALID_RESOURCE_FOLDER", "path is required for folder resources"), AppHost.Json, statusCode: StatusCodes.Status400BadRequest);
            var added = resources.AddFolder(request.Path.Trim(), request.Id);
            return Results.Json(added, AppHost.Json);
        }));

        api.MapPost("/resources/{id}/refresh", (string id, ResourceManager resources) => GuardResource(() =>
        {
            resources.Refresh(id);
            return Results.Json(new { id, started = true }, AppHost.Json, statusCode: StatusCodes.Status202Accepted);
        }));

        api.MapPost("/resources/{id}/rescan", (string id, ResourceManager resources) => GuardResource(() =>
        {
            var folder = resources.Rescan(id);
            return Results.Json(new { id, folder }, AppHost.Json);
        }));

        api.MapPost("/resources/{id}/enabled", (string id, EnableResourceRequest request, ResourceManager resources) => GuardResource(() =>
            resources.SetEnabled(id, request.Enabled)
                ? Results.Json(new { id, enabled = request.Enabled }, AppHost.Json)
                : Results.Json(ApiError.Of("RESOURCE_NOT_FOUND", $"No resource '{id}'"), AppHost.Json, statusCode: StatusCodes.Status404NotFound)));

        api.MapDelete("/resources/{id}", (string id, ResourceManager resources) =>
            resources.Remove(id)
                ? Results.Json(new { id, removed = true }, AppHost.Json)
                : Results.Json(ApiError.Of("RESOURCE_NOT_FOUND", $"No resource '{id}'"), AppHost.Json, statusCode: StatusCodes.Status404NotFound));

        api.MapGet("/resources/detect", (string path) =>
        {
            var root = ResourceSource.ResolveRoot(path);
            var kind = ResourceSource.DetectKind(root);
            return Results.Json(new
            {
                path = root,
                kind = kind switch { ResourceKind.RpEmotes => "rpemotes", ResourceKind.Scully => "scully", _ => "unknown" },
                suggestedId = ResourceSource.MakeId(Path.GetFileName(root.TrimEnd('\\', '/'))),
            }, AppHost.Json);
        });

        api.MapPost("/dialogs/folder", async (FolderDialogRequest? request, FolderDialog dialog, HttpContext context) =>
        {
            var picked = await dialog.PickAsync(request?.Initial, request?.Title, context.RequestAborted);
            return Results.Json(new FolderDialogResult(picked), AppHost.Json);
        });

        api.MapGet("/notices", () =>
        {
            using var stream = typeof(ApiEndpoints).Assembly.GetManifestResourceStream("THIRD-PARTY-NOTICES.md");
            if (stream == null) return Results.NotFound();
            using var reader = new StreamReader(stream);
            return Results.Text(reader.ReadToEnd(), "text/markdown; charset=utf-8");
        });

        api.MapPost("/quit", (IHostApplicationLifetime lifetime, ILoggerFactory loggers) =>
        {
            loggers.CreateLogger("App").LogInformation("Quit requested from the UI");
            _ = Task.Run(async () => { await Task.Delay(150); lifetime.StopApplication(); });
            return Results.Json(new { ok = true }, AppHost.Json);
        });
    }

    static IResult GuardResource(Func<IResult> body)
    {
        try { return body(); }
        catch (ResourceManager.ResourceException ex)
        {
            return Results.Json(ApiError.Of(ex.Code, ex.Message), AppHost.Json, statusCode: ex.Status);
        }
    }

    static bool ResourcesEqual(List<ResourceSetting> a, List<ResourceSetting> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].Path != b[i].Path || a[i].Origin != b[i].Origin || a[i].Ref != b[i].Ref || a[i].Enabled != b[i].Enabled) return false;
        }
        return true;
    }
}
