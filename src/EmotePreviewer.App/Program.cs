using EmotePreviewer.App;
using EmotePreviewer.App.Services;
using EmotePreviewer.App.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// EmotePreviewer.exe [--port 20300] [--data-dir <dir>] [--gta <dir>] [--keys <dir>] [--no-browser] [--app] [--verbose] [--help]

var options = new AppOptions();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            if (!int.TryParse(Next("--port"), out var port) || port < 0 || port > 65535) return Fail("--port needs a number between 0 and 65535");
            options.Port = port;
            break;
        case "--data-dir":
            options.DataDirectory = Path.GetFullPath(Next("--data-dir"));
            break;
        case "--gta":
            options.GtaFolderOverride = Path.GetFullPath(Next("--gta"));
            break;
        case "--keys":
            options.KeysFolderOverride = Path.GetFullPath(Next("--keys"));
            break;
        case "--no-browser":
            options.OpenBrowser = false;
            break;
        case "--app":
            options.AppWindow = true;
            break;
        case "--verbose":
        case "-v":
            options.MinimumLogLevel = LogLevel.Debug;
            break;
        case "--help":
        case "-h":
            Console.WriteLine(Usage());
            return 0;
        default:
            return Fail("unknown argument " + args[i]);
    }

    string Next(string name)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"{name} needs a value");
            Console.Error.WriteLine(Usage());
            Environment.Exit(64);
        }
        return args[++i];
    }
}

Directory.CreateDirectory(options.DataDirectory);
using var instance = SingleInstance.Acquire(options.DataDirectory);
if (!instance.IsFirst)
{
    var existing = SingleInstance.ReadUrl(options.DataDirectory);
    if (existing != null)
    {
        Console.WriteLine($"EmotePreviewer is already running at {existing}; opening it in the browser.");
        BrowserLauncher.Open(existing, options.AppWindow);
    }
    else
    {
        Console.Error.WriteLine("EmotePreviewer is already running for this data directory.");
    }
    return 0;
}

WebApplication app;
try
{
    app = await AppHost.StartAsync(options);
}
catch (Exception ex)
{
    Console.Error.WriteLine("EmotePreviewer could not start: " + ex.Message);
    return 4;
}

var url = app.Services.GetRequiredService<ListenerInfo>().Url;
SingleInstance.Publish(options.DataDirectory, url);
PrintBanner(app.Services, url);

if (options.OpenBrowser && !BrowserLauncher.Open(url, options.AppWindow))
    app.Logger.LogWarning("Could not open a browser; open {Url} manually", url);

// Ctrl+C / closing the console / POST /api/quit all end up in ApplicationStopping.
await app.WaitForShutdownAsync();
SingleInstance.Remove(options.DataDirectory);
await app.DisposeAsync();
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine(Usage());
    return 64;
}

static string Usage() => """
    usage: EmotePreviewer [options]

      --port <n>        listening port (default 20300; the next 10 ports are tried when busy)
      --data-dir <dir>  settings, cache, downloaded resources and logs (default %LOCALAPPDATA%\EmotePreviewer)
      --gta <dir>       GTA V folder, overriding the saved setting for this run
      --keys <dir>      folder with the four gtav_*.dat key files, overriding the saved setting for this run
      --no-browser      do not open the browser at start-up
      --app             open the UI as an app window (Edge / Chrome --app=) instead of a tab
      --verbose, -v     debug logging
      --help, -h        this text
    """;

static void PrintBanner(IServiceProvider services, string url)
{
    var options = services.GetRequiredService<AppOptions>();
    var settings = services.GetRequiredService<SettingsStore>();
    var state = services.GetRequiredService<AppState>();
    var paths = services.GetRequiredService<HostPaths>();
    var gta = state.ResolveGtaFolder(out var detected);
    var keys = state.ResolveKeysFolder();
    var missing = Directory.Exists(keys) ? AppState.MissingKeyFiles(keys).Count : 4;

    Console.WriteLine();
    Console.WriteLine($"============ EmotePreviewer {AppVersion.Value} ============");
    Console.WriteLine($"  UI        : {url}");
    Console.WriteLine($"  data      : {options.DataDirectory}");
    Console.WriteLine($"  settings  : {settings.Path}{(settings.CreatedDefault ? "  (new)" : "")}");
    if (paths.LogPath != null) Console.WriteLine($"  log       : {paths.LogPath}");
    Console.WriteLine($"  GTA V     : {(gta ?? "not found")}{(detected && gta != null ? "  (detected)" : "")}");
    Console.WriteLine($"  keys      : {keys}{(missing == 0 ? "" : $"  ({missing} of 4 files missing)")}");
    Console.WriteLine($"  resources : {settings.Current.Resources.Count} configured");
    Console.WriteLine("  Press Ctrl+C to quit.");
    Console.WriteLine("=================================================");
    Console.WriteLine();
}

/// <summary>Entry point marker so tests and tooling can reference the assembly's program type.</summary>
public partial class Program { }
