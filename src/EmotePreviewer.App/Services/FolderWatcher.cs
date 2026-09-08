using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Services;

/// <summary>
/// Watches the folder resources (not the downloaded ones) for changed .ycd, .ydr and .lua files and rescans the resource
/// after a short quiet period, so a clip written by a converter shows up in the viewer without a restart. Controlled by
/// <see cref="AppSettings.WatchFolders"/>; re-armed whenever the settings change.
/// </summary>
public sealed class FolderWatcher : IHostedService, IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);
    static readonly string[] Extensions = { ".ycd", ".ydr", ".lua" };

    readonly SettingsStore _settings;
    readonly ResourceManager _resources;
    readonly ILogger<FolderWatcher> _logger;
    readonly object _sync = new();
    readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, System.Threading.Timer> _pending = new(StringComparer.OrdinalIgnoreCase);
    bool _started;

    public FolderWatcher(SettingsStore settings, ResourceManager resources, ILogger<FolderWatcher> logger)
    {
        _settings = settings;
        _resources = resources;
        _logger = logger;
    }

    /// <summary>Resource ids currently watched (for diagnostics and tests).</summary>
    public IReadOnlyList<string> Watched { get { lock (_sync) return _watchers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); } }

    /// <summary>Raised after a resource was rescanned because of a file change (tests wait on it).</summary>
    public event Action<string>? Rescanned;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync) _started = true;
        _settings.Changed += OnSettingsChanged;
        Apply(_settings.Current);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.Changed -= OnSettingsChanged;
        lock (_sync) { _started = false; }
        Apply(null);
        return Task.CompletedTask;
    }

    void OnSettingsChanged(AppSettings settings) => Apply(settings);

    /// <summary>Makes the set of watchers match the enabled folder resources of <paramref name="settings"/> (null: none).</summary>
    void Apply(AppSettings? settings)
    {
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (settings != null && settings.WatchFolders)
            foreach (var r in settings.Resources)
                if (r.Enabled && !string.Equals(r.Origin, "github", StringComparison.OrdinalIgnoreCase) && Directory.Exists(r.Path))
                    wanted[r.Id] = r.Path;
        lock (_sync)
        {
            if (!_started && settings != null) return;
            foreach (var id in _watchers.Keys.ToList())
            {
                if (wanted.TryGetValue(id, out var path) && string.Equals(Path.GetFullPath(_watchers[id].Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) continue;
                _watchers[id].Dispose();
                _watchers.Remove(id);
            }
            foreach (var (id, path) in wanted)
            {
                if (_watchers.ContainsKey(id)) continue;
                try
                {
                    var w = new FileSystemWatcher(path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName };
                    w.Changed += (_, e) => OnFile(id, e.FullPath);
                    w.Created += (_, e) => OnFile(id, e.FullPath);
                    w.Deleted += (_, e) => OnFile(id, e.FullPath);
                    w.Renamed += (_, e) => { OnFile(id, e.OldFullPath); OnFile(id, e.FullPath); };
                    w.Error += (_, e) => _logger.LogWarning("Watching {Id} ({Path}): {Message}", id, path, e.GetException().Message);
                    w.EnableRaisingEvents = true;
                    _watchers[id] = w;
                    _logger.LogInformation("Watching resource {Id} ({Path}) for changed clips", id, path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not watch {Id} ({Path}): {Message}", id, path, ex.Message);
                }
            }
        }
    }

    void OnFile(string id, string fullPath)
    {
        var ext = Path.GetExtension(fullPath);
        bool interesting = Extensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase)) || ext.Length == 0;   // folders have no extension
        if (!interesting) return;
        // one timer per resource, restarted on every event: the rescan runs once the files have been quiet for Debounce
        var timer = _pending.GetOrAdd(id, _ => new System.Threading.Timer(_ => Fire(id), null, Timeout.Infinite, Timeout.Infinite));
        timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    void Fire(string id)
    {
        try
        {
            _resources.Rescan(id);
            Rescanned?.Invoke(id);
        }
        catch (ResourceManager.ResourceException) { }   // removed meanwhile
        catch (Exception ex)
        {
            _logger.LogWarning("Rescan of {Id} after a file change failed: {Message}", id, ex.Message);
        }
    }

    public void Dispose()
    {
        Apply(null);
        foreach (var t in _pending.Values) t.Dispose();
        _pending.Clear();
    }
}
