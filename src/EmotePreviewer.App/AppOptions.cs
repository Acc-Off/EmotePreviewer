using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App;

/// <summary>Command line and host options. Tests construct this directly; <see cref="Program"/> fills it from the arguments.</summary>
public sealed class AppOptions
{
    public const int DefaultPort = 20300;
    public const string WebRootVariable = "EMOTEPREVIEWER_WEBROOT";

    /// <summary>Requested port; 0 lets Kestrel pick one. When the port is busy, <see cref="PortRetries"/> higher ports are tried.</summary>
    public int Port { get; set; } = DefaultPort;
    public int PortRetries { get; set; } = 10;

    /// <summary>Settings, cache, downloaded resources and logs. Default: %LOCALAPPDATA%\EmotePreviewer.</summary>
    public string DataDirectory { get; set; } = DefaultDataDirectory();

    /// <summary>Temporary overrides of the saved settings (<c>--gta</c> / <c>--keys</c>).</summary>
    public string? GtaFolderOverride { get; set; }
    public string? KeysFolderOverride { get; set; }

    public bool OpenBrowser { get; set; } = true;
    /// <summary>Open the UI as an app window (<c>msedge --app=</c>) instead of a tab.</summary>
    public bool AppWindow { get; set; }

    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;
    public bool ConsoleLogging { get; set; } = true;
    public bool FileLogging { get; set; } = true;

    /// <summary>Serve the SPA from this directory instead of the embedded build (<see cref="WebRootVariable"/>).</summary>
    public string? WebRootDirectory { get; set; } = Environment.GetEnvironmentVariable(WebRootVariable);

    /// <summary>Start indexing the game data in the background once the host is up. Tests turn this off.</summary>
    public bool StartGameData { get; set; } = true;

    /// <summary>Runs after all registrations; tests use it to swap in fakes.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmotePreviewer");
}
