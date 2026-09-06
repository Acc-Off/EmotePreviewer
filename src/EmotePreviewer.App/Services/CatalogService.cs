using System.Diagnostics;
using EmotePreviewer.App.Web;
using EmotePreviewer.Core.Catalog;
using EmotePreviewer.Core.Model;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Services;

/// <summary>
/// Builds the emote catalog from the configured resources and, once the game data is indexed, works out which
/// entries can be previewed. Publishes the SSE <c>catalog</c> event whenever the DTO changes.
/// </summary>
public sealed class CatalogService : IDisposable
{
    public const string ReasonNotIndexed = "not-indexed";
    public const string ReasonKind = "kind";
    public const string ReasonAnimal = "animal";
    public const string ReasonNoDictionary = "no-dictionary";
    public const string ReasonNoClip = "no-clip";

    readonly AppOptions _options;
    readonly SettingsStore _settings;
    readonly AppState _state;
    readonly EventHub _events;
    readonly ILogger<CatalogService> _logger;
    readonly object _sync = new();

    EmoteCatalog _catalog = EmoteCatalog.Empty;
    CatalogDto _dto = new(Array.Empty<EmoteDto>(), Array.Empty<CatalogSourceDto>(), Array.Empty<string>(), false, 0);
    long _revision;
    CancellationTokenSource? _resolveCts;

    public CatalogService(AppOptions options, SettingsStore settings, AppState state, EventHub events, ILogger<CatalogService> logger)
    {
        _options = options;
        _settings = settings;
        _state = state;
        _events = events;
        _logger = logger;
        _state.CatalogEntriesProvider = () => Catalog.Entries.Count;
        _state.Changed += OnGameDataChanged;
    }

    public EmoteCatalog Catalog { get { lock (_sync) return _catalog; } }
    public CatalogDto Dto { get { lock (_sync) return _dto; } }

    /// <summary>Folder for resources downloaded from GitHub.</summary>
    public string ResourcesDirectory => Path.Combine(_options.DataDirectory, "resources");

    /// <summary>Resolves the configured resources to <see cref="ResourceSource"/>s (GitHub resources live under the data directory).</summary>
    public IReadOnlyList<ResourceSource> ConfiguredSources()
    {
        return _settings.Current.Resources.Where(r => r.Enabled).Select(r =>
        {
            var src = r.ToSource();
            return src.Origin == ResourceOrigin.GitHub ? src with { Path = Path.Combine(ResourcesDirectory, r.Id) } : src;
        }).ToList();
    }

    /// <summary>Rebuilds the catalog synchronously (about 0.1 s for 6,500 entries) and re-resolves previewability in the background.</summary>
    public void Rebuild()
    {
        var sw = Stopwatch.StartNew();
        var sources = ConfiguredSources();
        var catalog = CatalogBuilder.Build(sources);
        foreach (var w in catalog.Warnings) _logger.LogWarning("Resource: {Warning}", w);
        lock (_sync)
        {
            _catalog = catalog;
            _dto = BuildDto(catalog, resolved: null, ++_revision);
        }
        _logger.LogInformation("Catalog: {Entries} entries from {Sources} resource(s) in {Ms} ms", catalog.Entries.Count, sources.Count, sw.ElapsedMilliseconds);
        _events.Publish("catalog", new { revision = Dto.Revision, entries = catalog.Entries.Count, previewResolved = false });
        ResolvePreviewability(_state.Snapshot);
    }

    void OnGameDataChanged(GameDataSnapshot snapshot) => ResolvePreviewability(snapshot);

    void ResolvePreviewability(GameDataSnapshot snapshot)
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            _resolveCts?.Cancel();
            cts = _resolveCts = new CancellationTokenSource();
        }
        if (snapshot.State != GtaState.Ready || snapshot.GameData == null)
        {
            lock (_sync)
            {
                if (_dto.PreviewResolved) { _dto = BuildDto(_catalog, null, ++_revision); }
            }
            return;
        }
        var catalog = Catalog;
        var gd = snapshot.GameData;
        _ = Task.Run(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var resolved = Resolve(catalog, gd, cts.Token);
                lock (_sync)
                {
                    if (cts.IsCancellationRequested || !ReferenceEquals(_catalog, catalog)) return;
                    _dto = BuildDto(catalog, resolved, ++_revision);
                }
                var ok = resolved.Reasons.Count(r => r.Value == null);
                _logger.LogInformation("Preview check: {Ok} of {Total} animation entries previewable, {Props} of {PropTotal} prop models available ({Ms} ms)",
                    ok, resolved.Reasons.Count, resolved.Props.Count(p => p.Value), resolved.Props.Count, sw.ElapsedMilliseconds);
                _events.Publish("catalog", new { revision = Dto.Revision, entries = catalog.Entries.Count, previewResolved = true });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Preview check failed");
            }
        }, CancellationToken.None);
    }

    /// <param name="Reasons">Entry id → reason (null = previewable) for every Animation entry.</param>
    /// <param name="Props">Prop model → whether a mesh is available (shipped .ydr or game data).</param>
    sealed record Resolved(Dictionary<string, string?> Reasons, Dictionary<string, bool> Props);

    static Resolved Resolve(EmoteCatalog catalog, IGameDataSource gd, CancellationToken ct)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var props = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var dictCache = new Dictionary<string, IClipDictionary?>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in catalog.Entries)
        {
            foreach (var prop in e.Props)
                if (prop.Model.Length > 0 && !props.ContainsKey(prop.Model))
                    props[prop.Model] = catalog.CustomDrawables.ContainsKey(prop.Model) || gd.HasDrawable(prop.Model);
            if (!e.HasClip) continue;
            ct.ThrowIfCancellationRequested();
            // Walks name a movement clip set; the game's clip_sets.ymt says which dictionary (own or fallback) holds its "walk".
            if (e.Kind == EmoteKind.Walk && e.Name != null && gd.ResolveClipSetClip(e.Name, EmoteEntry.WalkClip) is { } walk)
            {
                e.Dictionary = walk.Dictionary;
                e.Clip = walk.Clip;
            }
            // Animal emotes play on the animal's own skeleton; without that ped in the game data they stay unpreviewable.
            if (e.AnimalPed is { } animal && !gd.HasSkeleton(animal)) { result[e.Id] = ReasonAnimal; continue; }
            var key = e.IsCustom ? "file:" + e.CustomYcdPath : e.Dictionary!;
            if (!dictCache.TryGetValue(key, out var dict))
            {
                try
                {
                    dict = e.IsCustom ? gd.LoadLooseClipDictionary(e.CustomYcdPath!)
                         : gd.HasClipDictionary(e.Dictionary!) ? gd.LoadClipDictionary(e.Dictionary!) : null;
                }
                catch (Exception) { dict = null; }
                dictCache[key] = dict;
            }
            if (dict == null) { result[e.Id] = ReasonNoDictionary; continue; }
            result[e.Id] = dict.FindClip(e.Clip!) != null ? null : ReasonNoClip;
        }
        return new Resolved(result, props);
    }

    static CatalogDto BuildDto(EmoteCatalog catalog, Resolved? resolved, long revision)
    {
        var entries = new List<EmoteDto>(catalog.Entries.Count);
        string? Reason(EmoteEntry e)
        {
            if (!e.HasClip) return ReasonKind;
            if (resolved == null) return ReasonNotIndexed;
            return resolved.Reasons.TryGetValue(e.Id, out var r) ? r : ReasonNotIndexed;
        }
        List<PropDto> Props(EmoteEntry e) => e.Props.Select(p => new PropDto(p.Model, p.Bone, p.Placement,
            catalog.CustomDrawables.ContainsKey(p.Model) ? true : resolved != null && resolved.Props.TryGetValue(p.Model, out var a) ? a : null)).ToList();
        foreach (var e in catalog.Entries)
        {
            var reason = Reason(e);
            PartnerDto? partner = null;
            if (e.PartnerId != null && catalog.FindById(e.PartnerId) is { } p)
            {
                var pReason = Reason(p);
                partner = new PartnerDto(p.Id, p.Command, p.Label, p.Dictionary, p.Clip, p.Loop, p.DurationMs, p.StartDelayMs, Props(p),
                    pReason == null, pReason, p.AnimalPed, PartnerPlacementDto.Resolve(e.Placement, p.Placement));
            }
            entries.Add(new EmoteDto(e.Id, e.Source, e.Category, e.Command, e.Label, EmoteDto.KindName(e.Kind), e.Dictionary, e.Clip, e.Name,
                e.Loop, e.Move, e.DurationMs, e.ExitEmote, Props(e), e.IsCustom, reason == null, reason, e.AnimalPed,
                e.StartDelayMs, e.PartnerCommand, partner));
        }
        var counts = catalog.Entries.GroupBy(e => e.Source).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var sources = catalog.Sources.Select(s => new CatalogSourceDto(s.Id, s.Path, s.Origin == ResourceOrigin.GitHub ? "github" : "folder", s.Ref,
            ResourceSource.DetectKind(ResourceSource.ResolveRoot(s.Path)) switch { ResourceKind.RpEmotes => "rpemotes", ResourceKind.Scully => "scully", _ => "unknown" },
            counts.TryGetValue(s.Id, out var c) ? c : 0)).ToList();
        return new CatalogDto(entries, sources, catalog.Warnings, resolved != null, revision);
    }

    public void Dispose()
    {
        _state.Changed -= OnGameDataChanged;
        lock (_sync) _resolveCts?.Cancel();
    }
}
