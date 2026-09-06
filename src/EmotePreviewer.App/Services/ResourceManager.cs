using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using EmotePreviewer.App.Web;
using EmotePreviewer.Core.Catalog;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Services;

/// <summary>A well-known resource the user can add with one click.</summary>
public sealed record ResourceTemplate(string Id, string Repository, string Ref, string Kind, string Description);

/// <summary>Progress of a download / extraction, broadcast as the SSE <c>resource</c> event.</summary>
public sealed record ResourceJobDto(string Id, string State, long Bytes, long? Total, string? Message, DateTimeOffset UpdatedAt);

/// <summary>Contents of <c>source.json</c> next to a downloaded resource.</summary>
public sealed record ResourceSourceInfo(string Repository, string Ref, DateTimeOffset FetchedAt, string? ETag);

/// <summary>
/// Adds, refreshes and removes emote resources. GitHub resources are downloaded as a branch zip into
/// <c>&lt;data-dir&gt;/resources/&lt;id&gt;/</c>; folder resources are only referenced. Every change is written to the
/// settings and rebuilds the catalog.
/// </summary>
public sealed class ResourceManager
{
    public static readonly IReadOnlyList<ResourceTemplate> Templates = new[]
    {
        new ResourceTemplate("rpemotes-reborn", "alberttheprince/rpemotes-reborn", "master", "rpemotes", "rpemotes-reborn (official repository)"),
        new ResourceTemplate("rpemotes-reborn-nui", "Jerrys-C/rpemotes-reborn-nui", "master", "rpemotes", "rpemotes-reborn with a NUI menu"),
        new ResourceTemplate("scully_emotemenu", "Scullyy/scully_emotemenu", "main", "scully", "scully_emotemenu"),
    };

    public const string SourceFileName = "source.json";
    static readonly TimeSpan JobRetention = TimeSpan.FromMinutes(10);

    readonly AppOptions _options;
    readonly SettingsStore _settings;
    readonly CatalogService _catalog;
    readonly ClipService _clips;
    readonly MeshService _meshes;
    readonly EventHub _events;
    readonly ILogger<ResourceManager> _logger;
    readonly HttpClient _http;
    readonly ConcurrentDictionary<string, ResourceJobDto> _jobs = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.OrdinalIgnoreCase);
    readonly object _settingsLock = new();

    public ResourceManager(AppOptions options, SettingsStore settings, CatalogService catalog, ClipService clips, MeshService meshes, EventHub events, ILogger<ResourceManager> logger, HttpMessageHandler? handler = null)
    {
        _options = options;
        _settings = settings;
        _catalog = catalog;
        _clips = clips;
        _meshes = meshes;
        _events = events;
        _logger = logger;
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromMinutes(30);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EmotePreviewer", AppVersion.Value));
    }

    /// <summary>Base URL of the zip downloads; tests point it at a local server.</summary>
    public string ArchiveBaseUrl { get; set; } = "https://github.com/";

    public string ResourcesDirectory => Path.Combine(_options.DataDirectory, "resources");

    public string ResourceFolder(string id) => Path.Combine(ResourcesDirectory, id);

    public IReadOnlyList<ResourceJobDto> Jobs
    {
        get
        {
            var cutoff = DateTimeOffset.UtcNow - JobRetention;
            foreach (var (id, job) in _jobs)
                if (job.State is "done" or "error" && job.UpdatedAt < cutoff) _jobs.TryRemove(id, out _);
            return _jobs.Values.OrderBy(j => j.Id).ToList();
        }
    }

    public ResourceSourceInfo? ReadSourceInfo(string id)
    {
        var path = Path.Combine(ResourceFolder(id), SourceFileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ResourceSourceInfo>(File.ReadAllText(path), AppHost.Json); }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ folder resources

    public sealed class ResourceException : Exception
    {
        public ResourceException(string code, string message, int status = 400) : base(message)
        {
            Code = code;
            Status = status;
        }

        public string Code { get; }
        public int Status { get; }
    }

    /// <summary>Adds a folder the user picked. The id is derived from the folder name unless given.</summary>
    public ResourceSetting AddFolder(string path, string? id)
    {
        var root = ResourceSource.ResolveRoot(path);
        var kind = ResourceSource.DetectKind(root);
        if (kind == ResourceKind.Unknown) throw new ResourceException("UNKNOWN_RESOURCE_LAYOUT", $"{path} is not an rpemotes-style or scully_emotemenu folder");
        ResourceSetting added;
        lock (_settingsLock)
        {
            var settings = _settings.Current.Clone();
            if (settings.Resources.Any(r => string.Equals(Path.GetFullPath(r.Path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)))
                throw new ResourceException("DUPLICATE_RESOURCE", "That folder is already configured", 409);
            added = new ResourceSetting { Id = UniqueId(settings, id ?? ResourceSource.MakeId(Path.GetFileName(root.TrimEnd('\\', '/')))), Path = root, Origin = "folder" };
            settings.Resources.Add(added);
            _settings.Save(settings);
        }
        AfterChange();
        return added;
    }

    /// <summary>Includes or excludes a resource from the catalog without touching its files. False when the id is unknown.</summary>
    public bool SetEnabled(string id, bool enabled)
    {
        lock (_settingsLock)
        {
            var settings = _settings.Current.Clone();
            var target = settings.Resources.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (target == null) return false;
            if (target.Enabled == enabled) return true;
            target.Enabled = enabled;
            _settings.Save(settings);
        }
        _logger.LogInformation("Resource {Id} {State}", id, enabled ? "enabled" : "disabled");
        AfterChange();
        return true;
    }

    /// <summary>Removes a resource from the settings; downloaded files are deleted too.</summary>
    public bool Remove(string id)
    {
        ResourceSetting? removed;
        lock (_settingsLock)
        {
            var settings = _settings.Current.Clone();
            removed = settings.Resources.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == null) return false;
            settings.Resources.Remove(removed);
            _settings.Save(settings);
        }
        if (_running.TryGetValue(id, out var cts)) cts.Cancel();
        if (removed.Origin == "github")
        {
            var folder = ResourceFolder(removed.Id);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) { _logger.LogWarning("Could not delete {Folder}: {Message}", folder, ex.Message); }
        }
        _jobs.TryRemove(id, out _);
        AfterChange();
        return true;
    }

    // ------------------------------------------------------------------ GitHub resources

    /// <summary>
    /// Starts downloading <paramref name="repository"/> (<c>owner/name</c>) at <paramref name="ref"/>. Returns the id the
    /// resource will have; progress arrives over SSE. Adding an id that is already downloading is a no-op.
    /// </summary>
    public string AddGitHub(string repository, string? @ref, string? id)
    {
        if (!IsValidRepository(repository)) throw new ResourceException("INVALID_REPOSITORY", "Repository must be owner/name");
        var branch = string.IsNullOrWhiteSpace(@ref) ? Templates.FirstOrDefault(t => t.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase))?.Ref ?? "main" : @ref.Trim();
        var resourceId = ResourceSource.MakeId(string.IsNullOrWhiteSpace(id) ? repository.Split('/')[1] : id);
        lock (_settingsLock)
        {
            var existing = _settings.Current.Resources.FirstOrDefault(r => string.Equals(r.Id, resourceId, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing.Origin != "github") throw new ResourceException("DUPLICATE_RESOURCE", $"A folder resource with id '{resourceId}' already exists", 409);
        }
        Start(resourceId, repository, branch);
        return resourceId;
    }

    /// <summary>Downloads a GitHub resource again (same repository and ref).</summary>
    public void Refresh(string id)
    {
        var setting = _settings.Current.Resources.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ResourceException("RESOURCE_NOT_FOUND", $"No resource '{id}'", 404);
        if (setting.Origin != "github" || string.IsNullOrEmpty(setting.Repository))
            throw new ResourceException("NOT_A_GITHUB_RESOURCE", "Only resources downloaded from GitHub can be refreshed", 409);
        Start(setting.Id, setting.Repository, setting.Ref ?? "main");
    }

    void Start(string id, string repository, string branch)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(id, cts)) return; // already running
        Publish(id, "downloading", 0, null, null);
        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadAsync(id, repository, branch, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _jobs.TryRemove(id, out _);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Resource {Id}: {Message}", id, ex.Message);
                Publish(id, "error", 0, null, ex.Message);
            }
            finally
            {
                _running.TryRemove(id, out _);
                cts.Dispose();
            }
        }, CancellationToken.None);
    }

    async Task DownloadAsync(string id, string repository, string branch, CancellationToken ct)
    {
        var url = $"{ArchiveBaseUrl.TrimEnd('/')}/{repository}/archive/refs/heads/{Uri.EscapeDataString(branch)}.zip";
        Directory.CreateDirectory(ResourcesDirectory);
        var zipPath = Path.Combine(ResourcesDirectory, id + ".zip.tmp");
        var extractPath = Path.Combine(ResourcesDirectory, id + ".extract.tmp");
        var target = ResourceFolder(id);
        string? etag;
        try
        {
            _logger.LogInformation("Downloading {Url}", url);
            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    throw new ResourceException("REPOSITORY_NOT_FOUND", $"{repository} (branch {branch}) was not found on GitHub", 404);
                response.EnsureSuccessStatusCode();
                etag = response.Headers.ETag?.Tag;
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                var buffer = new byte[1 << 16];
                long bytes = 0;
                var lastPublish = System.Diagnostics.Stopwatch.StartNew();
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    bytes += read;
                    if (lastPublish.ElapsedMilliseconds >= 250)
                    {
                        lastPublish.Restart();
                        Publish(id, "downloading", bytes, total, null);
                    }
                }
                Publish(id, "extracting", bytes, total, null);
            }

            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, recursive: true);
            ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);
            ct.ThrowIfCancellationRequested();

            // GitHub zips wrap everything in <name>-<branch>/; use that folder as the resource root.
            var root = ResourceSource.ResolveRoot(extractPath);
            if (ResourceSource.DetectKind(root) == ResourceKind.Unknown)
                throw new ResourceException("UNKNOWN_RESOURCE_LAYOUT", $"{repository} does not look like an rpemotes-style or scully_emotemenu resource");

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(root, target);
            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, recursive: true);
            File.WriteAllText(Path.Combine(target, SourceFileName), JsonSerializer.Serialize(new ResourceSourceInfo(repository, branch, DateTimeOffset.UtcNow, etag), AppHost.Json));

            lock (_settingsLock)
            {
                var settings = _settings.Current.Clone();
                var existing = settings.Resources.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (existing == null) settings.Resources.Add(new ResourceSetting { Id = id, Path = target, Origin = "github", Ref = branch, Repository = repository });
                else { existing.Path = target; existing.Origin = "github"; existing.Ref = branch; existing.Repository = repository; }
                _settings.Save(settings);
            }
            _logger.LogInformation("Resource {Id} ready in {Folder}", id, target);
            Publish(id, "done", 0, null, null);
            AfterChange();
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch (IOException) { }
            try { if (Directory.Exists(extractPath)) Directory.Delete(extractPath, recursive: true); } catch (IOException) { }
        }
    }

    void AfterChange()
    {
        _clips.Clear();
        _meshes.Clear();
        _catalog.Rebuild();
    }

    void Publish(string id, string state, long bytes, long? total, string? message)
    {
        var job = new ResourceJobDto(id, state, bytes, total, message, DateTimeOffset.UtcNow);
        _jobs[id] = job;
        _events.Publish("resource", job);
    }

    static string UniqueId(AppSettings settings, string baseId)
    {
        var id = baseId;
        for (int n = 2; settings.Resources.Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)); n++) id = $"{baseId}-{n}";
        return id;
    }

    public static bool IsValidRepository(string repository)
    {
        var parts = repository.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && p != "." && p != "..");
    }
}
