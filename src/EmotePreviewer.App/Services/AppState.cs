using System.Diagnostics;
using EmotePreviewer.App.Web;
using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Model;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Services;

public enum GtaState
{
    MissingGta,
    MissingKeys,
    Indexing,
    Ready,
    Error,
}

/// <summary>Immutable view of the game-data side of the app, handed to the API.</summary>
public sealed record GameDataSnapshot(
    GtaState State,
    string? Message,
    int ProgressDone,
    int ProgressTotal,
    string? GtaFolder,
    string? KeysFolder,
    bool GtaDetected,
    int ClipDictionaries,
    int Archives,
    GtaToolkitGameData? GameData,
    long IndexMilliseconds);

/// <summary>
/// Owns the GTA V game data: resolves the install and key folders and indexes the archives in the background.
/// State changes are broadcast as the SSE <c>status</c> event. Skeletons and meshes are loaded on demand by
/// <see cref="SkeletonService"/> / <see cref="PedService"/> from the indexed data.
/// </summary>
public sealed class AppState : IDisposable
{
    readonly AppOptions _options;
    readonly SettingsStore _settings;
    readonly EventHub _events;
    readonly ILogger<AppState> _logger;
    readonly object _sync = new();

    GameDataSnapshot _snapshot = new(GtaState.MissingGta, null, 0, 0, null, null, false, 0, 0, null, 0);
    CancellationTokenSource? _cts;
    Task? _task;

    public AppState(AppOptions options, SettingsStore settings, EventHub events, ILogger<AppState> logger)
    {
        _options = options;
        _settings = settings;
        _events = events;
        _logger = logger;
    }

    public GameDataSnapshot Snapshot { get { lock (_sync) return _snapshot; } }

    /// <summary>Raised (on a background thread) whenever the state changes; the catalog uses it to re-resolve previewability.</summary>
    public event Action<GameDataSnapshot>? Changed;

    public string CacheDirectory => Path.Combine(_options.DataDirectory, "cache");

    // ------------------------------------------------------------------ folder resolution

    /// <summary>Effective GTA folder: <c>--gta</c>, then settings, then the registry.</summary>
    public string? ResolveGtaFolder(out bool detected)
    {
        detected = false;
        if (!string.IsNullOrWhiteSpace(_options.GtaFolderOverride)) return _options.GtaFolderOverride;
        var s = _settings.Current.GtaFolder;
        if (!string.IsNullOrWhiteSpace(s)) return s;
        detected = true;
        return GtaLocator.Detect();
    }

    /// <summary>Effective key folder: <c>--keys</c>, then settings, then <c>EMOTEPREVIEWER_KEYS</c>, then <c>&lt;data-dir&gt;/keys</c>.</summary>
    public string ResolveKeysFolder()
    {
        if (!string.IsNullOrWhiteSpace(_options.KeysFolderOverride)) return _options.KeysFolderOverride;
        var s = _settings.Current.KeysFolder;
        if (!string.IsNullOrWhiteSpace(s)) return s;
        var env = Environment.GetEnvironmentVariable(GtaKeys.KeyFolderVariable);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(_options.DataDirectory, "keys");
    }

    public static IReadOnlyList<string> MissingKeyFiles(string folder) =>
        GtaKeys.RequiredFiles.Where(f => !File.Exists(Path.Combine(folder, f))).ToList();

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Starts the background initialisation.</summary>
    public void Start() => Restart();

    /// <summary>Cancels a running initialisation and starts over (after the GTA / key folder changed).</summary>
    public void Restart()
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            _cts?.Cancel();
            cts = _cts = new CancellationTokenSource();
        }
        _task = Task.Run(() => InitializeAsync(cts.Token), CancellationToken.None);
    }

    async Task InitializeAsync(CancellationToken ct)
    {
        // Give a cancelled predecessor a moment to release its file handles.
        await Task.Yield();
        var gtaFolder = ResolveGtaFolder(out var detected);
        var keysFolder = ResolveKeysFolder();

        if (!GtaLocator.IsGtaFolder(gtaFolder))
        {
            Publish(s => s with { State = GtaState.MissingGta, Message = gtaFolder, GtaFolder = gtaFolder, KeysFolder = keysFolder, GtaDetected = detected, GameData = null, ClipDictionaries = 0, Archives = 0 });
            _logger.LogWarning("GTA V folder not found{Hint}", gtaFolder == null ? " (set it in the settings or pass --gta)" : ": " + gtaFolder);
            return;
        }

        var missing = MissingKeyFiles(keysFolder);
        if (missing.Count > 0)
        {
            Publish(s => s with { State = GtaState.MissingKeys, Message = string.Join(", ", missing), GtaFolder = gtaFolder, KeysFolder = keysFolder, GtaDetected = detected, GameData = null, ClipDictionaries = 0, Archives = 0 });
            _logger.LogWarning("Key files missing in {Folder}: {Files}", keysFolder, string.Join(", ", missing));
            return;
        }

        Publish(s => s with { State = GtaState.Indexing, Message = null, ProgressDone = 0, ProgressTotal = 0, GtaFolder = gtaFolder, KeysFolder = keysFolder, GtaDetected = detected });
        GtaToolkitGameData? gd = null;
        try
        {
            var sw = Stopwatch.StartNew();
            GtaKeys.InstallFromFolder(keysFolder);
            _logger.LogInformation("Keys loaded from {Folder}", keysFolder);
            var lastPublish = Stopwatch.StartNew();
            gd = GtaToolkitGameData.Open(gtaFolder!, keysFolder,
                log: m => _logger.LogDebug("{Message}", m),
                error: m => _logger.LogWarning("{Message}", m),
                progress: (done, total) =>
                {
                    if (lastPublish.ElapsedMilliseconds < 200 && done != total) return;
                    lastPublish.Restart();
                    Publish(s => s with { ProgressDone = done, ProgressTotal = total });
                },
                cancellation: ct);
            ct.ThrowIfCancellationRequested();

            GtaToolkitGameData? old;
            lock (_sync)
            {
                old = _snapshot.GameData;
                _snapshot = _snapshot with
                {
                    State = GtaState.Ready, Message = null, GameData = gd,
                    ClipDictionaries = gd.ClipDictionaryCount, Archives = gd.ArchiveCount, IndexMilliseconds = sw.ElapsedMilliseconds,
                    ProgressDone = _snapshot.ProgressTotal,
                };
            }
            old?.Dispose();
            _logger.LogInformation("Game data ready: {Archives} archives, {Dictionaries} clip dictionaries in {Ms} ms",
                gd.ArchiveCount, gd.ClipDictionaryCount, sw.ElapsedMilliseconds);
            Notify();
        }
        catch (OperationCanceledException)
        {
            gd?.Dispose();
        }
        catch (GtaKeys.KeyMaterialMissingException ex)
        {
            gd?.Dispose();
            Publish(s => s with { State = GtaState.MissingKeys, Message = ex.Message, GameData = null });
            _logger.LogWarning("{Message}", ex.Message);
        }
        catch (Exception ex)
        {
            gd?.Dispose();
            Publish(s => s with { State = GtaState.Error, Message = ex.Message, GameData = null });
            _logger.LogError(ex, "Game data initialisation failed");
        }
    }

    void Publish(Func<GameDataSnapshot, GameDataSnapshot> update)
    {
        lock (_sync) _snapshot = update(_snapshot);
        Notify();
    }

    /// <summary>Re-sends the status event (after something the DTO carries but the snapshot does not — the default ped — changed).</summary>
    public void PublishStatus() => _events.Publish("status", BuildStatus(Snapshot));

    void Notify()
    {
        var snapshot = Snapshot;
        _events.Publish("status", BuildStatus(snapshot));
        Changed?.Invoke(snapshot);
    }

    /// <summary>The status DTO for a snapshot, with the catalog size and the default ped's skeleton filled in by the other services.</summary>
    public StatusDto BuildStatus(GameDataSnapshot snapshot) =>
        StatusDto.From(snapshot, CatalogEntriesProvider?.Invoke() ?? 0, SkeletonProvider?.Invoke());

    /// <summary>Set by the catalog service so status events can carry the entry count without a circular dependency.</summary>
    public Func<int>? CatalogEntriesProvider { get; set; }
    /// <summary>Set by the skeleton service: the configured ped's skeleton when it is available (cached or loaded).</summary>
    public Func<SkeletonDef?>? SkeletonProvider { get; set; }

    public void Dispose()
    {
        lock (_sync)
        {
            _cts?.Cancel();
            _snapshot.GameData?.Dispose();
            _snapshot = _snapshot with { GameData = null };
        }
    }
}
