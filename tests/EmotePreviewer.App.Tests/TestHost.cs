using System.Text;
using EmotePreviewer.App;
using EmotePreviewer.App.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EmotePreviewer.App.Tests;

/// <summary>
/// Starts the real host on a free loopback port with a scratch data directory. Game data indexing is disabled, so
/// the tests run on machines without GTA V or key files.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "emotepreviewer-tests", Guid.NewGuid().ToString("N"));
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string Url => App.Services.GetRequiredService<ListenerInfo>().Url;

    public static async Task<TestHost> StartAsync(Action<AppOptions>? configure = null, Action<string>? prepareDataDirectory = null)
    {
        var host = new TestHost();
        Directory.CreateDirectory(host.DataDirectory);
        prepareDataDirectory?.Invoke(host.DataDirectory);
        var options = new AppOptions
        {
            Port = 0,
            DataDirectory = host.DataDirectory,
            OpenBrowser = false,
            ConsoleLogging = false,
            FileLogging = false,
            MinimumLogLevel = LogLevel.None,
            StartGameData = false,
            WebRootDirectory = null,
            KeysFolderOverride = Path.Combine(host.DataDirectory, "no-keys"),
            GtaFolderOverride = Path.Combine(host.DataDirectory, "no-gta"),
        };
        configure?.Invoke(options);
        host.App = await AppHost.StartAsync(options);
        host.Client = new HttpClient { BaseAddress = new Uri(host.Url) };
        return host;
    }

    /// <summary>Writes a small rpemotes-style resource (two animations, one walk, one expression) and returns its folder.</summary>
    public static string WriteRpEmotesResource(string root, string name = "fake-rpemotes")
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "client"));
        File.WriteAllText(Path.Combine(dir, "types.lua"), "AnimFlag = { MOVING = 51, LOOP = 1, STUCK = 50 }\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "client", "AnimationList.lua"), """
            RP = {}
            RP.Emotes = {
                ["wave"] = { "friends@frj@ig_1", "wave_a", "Wave", AnimationOptions = { EmoteLoop = true } },
                ["guitar"] = { "amb@world_human_musician@guitar@male@base", "base", "Guitar", AnimationOptions = { Prop = "prop_acc_guitar_01", PropBone = 24818, PropPlacement = { 0.1, 0.2, 0.3, 10, 20, 30 } } },
            }
            RP.Walks = {
                ["alien"] = { "move_m@alien", "Alien" },
            }
            RP.Expressions = {
                ["angry"] = { "mood_angry_1", "Angry" },
            }
            RP.Shared = {
                ["hug"] = { "mp_ped_interaction", "kisses_guy_a", "Hug", "hug2", AnimationOptions = { EmoteDuration = 5000, SyncOffsetFront = 1.05 } },
                ["hug2"] = { "mp_ped_interaction", "kisses_guy_b", "Hug 2", "hug", AnimationOptions = { EmoteDuration = 5000, SyncOffsetSide = -0.05, SyncOffsetFront = 1.18 } },
                ["carry"] = { "missfinale_c2mcs_1", "fin_c2_mcs_1_camman", "Carry", "carry2", AnimationOptions = { onFootFlag = AnimFlag.MOVING } },
                ["carry2"] = { "nm", "firemans_carry", "Be carried", "carry", AnimationOptions = { onFootFlag = AnimFlag.LOOP, Attachto = true, bone = 40269, pos = vector3(-0.14, 0.15, 0.14), rot = vector3(0.0, -59.0, -4.5) } },
                ["punch"] = { "melee@unarmed@streamed_variations", "plyr_takedown_front_slap", "Punch", "punched" },
                ["punched"] = { "melee@unarmed@streamed_variations", "victim_takedown_front_slap", "Punched", "punch" },
                ["lonely"] = { "some@dict", "clip", "Lonely", "nobody", AnimationOptions = { StartDelay = 250 } },
            }
            """, Encoding.UTF8);
        return dir;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { }
    }
}
