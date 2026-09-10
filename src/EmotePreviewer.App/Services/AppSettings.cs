using System.Text.Json.Serialization;
using EmotePreviewer.Core.Catalog;

namespace EmotePreviewer.App.Services;

/// <summary>Contents of <c>settings.json</c>. Null folders mean "auto-detect / default".</summary>
public sealed class AppSettings
{
    public const string DefaultPed = "mp_m_freemode_01";

    public string? GtaFolder { get; set; }
    public string? KeysFolder { get; set; }
    public List<ResourceSetting> Resources { get; set; } = new();
    public string Ped { get; set; } = DefaultPed;
    /// <summary>Ped for the second character of shared emotes; null plays the partner on <see cref="Ped"/> too.</summary>
    public string? PartnerPed { get; set; }
    public ViewerSettings Viewer { get; set; } = new();
    /// <summary>Watch folder resources for changed .ycd / .lua / .ydr files and rescan them automatically.</summary>
    public bool WatchFolders { get; set; } = true;

    public AppSettings Clone() => new()
    {
        GtaFolder = GtaFolder,
        KeysFolder = KeysFolder,
        Resources = Resources.Select(r => r.Clone()).ToList(),
        Ped = Ped,
        PartnerPed = PartnerPed,
        Viewer = Viewer.Clone(),
        WatchFolders = WatchFolders,
    };
}

public sealed class ResourceSetting
{
    public string Id { get; set; } = "";
    /// <summary>Folder the resource is read from. For <c>github</c> resources this is <c>&lt;data-dir&gt;/resources/&lt;id&gt;</c>.</summary>
    public string Path { get; set; } = "";
    /// <summary><c>folder</c> or <c>github</c>.</summary>
    public string Origin { get; set; } = "folder";
    /// <summary>Branch or tag for <c>github</c> resources.</summary>
    public string? Ref { get; set; }
    /// <summary><c>owner/name</c> for <c>github</c> resources.</summary>
    public string? Repository { get; set; }
    /// <summary>Disabled resources stay configured (and downloaded) but are left out of the catalog.</summary>
    public bool Enabled { get; set; } = true;

    public ResourceSetting Clone() => new() { Id = Id, Path = Path, Origin = Origin, Ref = Ref, Repository = Repository, Enabled = Enabled };

    public ResourceSource ToSource() => new(Id, Path,
        string.Equals(Origin, "github", StringComparison.OrdinalIgnoreCase) ? ResourceOrigin.GitHub : ResourceOrigin.Folder, Ref);
}

public sealed class ViewerSettings
{
    public bool ShowHelperBones { get; set; }
    public bool RootMotion { get; set; }
    public bool ShowProps { get; set; } = true;
    /// <summary>Show the ped mesh instead of the stick figure.</summary>
    public bool ShowMesh { get; set; }
    /// <summary>Diffuse textures on props and the ped mesh (flat colours when off).</summary>
    public bool ShowTextures { get; set; } = true;
    /// <summary>Draw the cloth-simulated parts of ped components (skinned to the body; the simulation itself is not reproduced).</summary>
    public bool ShowCloth { get; set; } = true;
    /// <summary>Play animal emotes on their animal ped instead of the configured one.</summary>
    public bool AnimalPeds { get; set; } = true;
    /// <summary>Show the other ped of shared emotes.</summary>
    public bool ShowPartner { get; set; } = true;
    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Theme { get; set; } = "system";
    /// <summary><c>auto</c>, <c>ja</c> or <c>en</c>.</summary>
    public string Language { get; set; } = "auto";

    public ViewerSettings Clone() => new() { ShowHelperBones = ShowHelperBones, RootMotion = RootMotion, ShowProps = ShowProps, ShowMesh = ShowMesh, ShowTextures = ShowTextures, ShowCloth = ShowCloth, AnimalPeds = AnimalPeds, ShowPartner = ShowPartner, Theme = Theme, Language = Language };
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
