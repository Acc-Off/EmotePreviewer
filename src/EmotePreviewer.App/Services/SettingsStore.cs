using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Services;

/// <summary>Reads and writes <c>settings.json</c> in the data directory. Saved immediately on every change; no hot reload.</summary>
public sealed class SettingsStore
{
    public const string FileName = "settings.json";
    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    readonly ILogger<SettingsStore> _logger;
    readonly object _sync = new();
    AppSettings _current = new();

    public SettingsStore(string dataDirectory, ILogger<SettingsStore> logger)
    {
        DataDirectory = dataDirectory;
        Path = System.IO.Path.Combine(dataDirectory, FileName);
        _logger = logger;
    }

    public string DataDirectory { get; }
    public string Path { get; }

    /// <summary>True when no settings file existed at load time (first run).</summary>
    public bool CreatedDefault { get; private set; }

    /// <summary>A snapshot; mutate a <see cref="AppSettings.Clone"/> and pass it to <see cref="Save"/>.</summary>
    public AppSettings Current
    {
        get { lock (_sync) return _current; }
    }

    public event Action<AppSettings>? Changed;

    public void Load()
    {
        lock (_sync)
        {
            if (!File.Exists(Path))
            {
                CreatedDefault = true;
                _current = new AppSettings();
                return;
            }
            try
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(Path, Encoding.UTF8), SettingsJsonContext.Default.AppSettings);
                _current = Normalize(loaded ?? new AppSettings());
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning("Could not read {Path} ({Message}); starting with default settings", Path, ex.Message);
                _current = new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        var normalized = Normalize(settings);
        lock (_sync)
        {
            Directory.CreateDirectory(DataDirectory);
            var json = JsonSerializer.Serialize(normalized, SettingsJsonContext.Default.AppSettings);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, json, Utf8NoBom);
            File.Move(tmp, Path, overwrite: true);
            _current = normalized;
        }
        Changed?.Invoke(normalized);
    }

    /// <summary>Trims strings, drops empty resources and turns blank folders into null.</summary>
    public static AppSettings Normalize(AppSettings s)
    {
        var n = s.Clone();
        n.GtaFolder = Blank(n.GtaFolder);
        n.KeysFolder = Blank(n.KeysFolder);
        n.Ped = Blank(n.Ped) is { } ped && SkeletonService.IsValidName(ped) ? ped.Trim().ToLowerInvariant() : AppSettings.DefaultPed;
        n.PartnerPed = Blank(n.PartnerPed) is { } partner && SkeletonService.IsValidName(partner) && !string.Equals(partner.Trim(), n.Ped, StringComparison.OrdinalIgnoreCase) ? partner.Trim().ToLowerInvariant() : null;
        n.Viewer.Theme = n.Viewer.Theme is "light" or "dark" ? n.Viewer.Theme : "system";
        n.Viewer.Language = n.Viewer.Language is "ja" or "en" ? n.Viewer.Language : "auto";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        n.Resources = n.Resources
            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => { r.Id = r.Id.Trim(); r.Path = r.Path.Trim(); r.Origin = r.Origin is "github" ? "github" : "folder"; r.Ref = Blank(r.Ref); r.Repository = Blank(r.Repository); return r; })
            .Where(r => seen.Add(r.Id))
            .ToList();
        return n;
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
