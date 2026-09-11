namespace EmotePreviewer.Core.Catalog;

public enum EmoteKind
{
    /// <summary>Animation dictionary + clip name. Previewable via .ycd.</summary>
    Animation,
    /// <summary>GTA scenario (PROP_HUMAN_ATM etc). Not directly previewable.</summary>
    Scenario,
    /// <summary>Movement clipset name (move_m@...). Requires clip_sets resolution.</summary>
    Walk,
    /// <summary>Facial expression clip.</summary>
    Expression,
}

/// <param name="Model">Prop model name (e.g. <c>prop_tool_fireaxe</c>).</param>
/// <param name="Bone">Bone tag the prop is attached to.</param>
/// <param name="Placement">[x, y, z, rotX, rotY, rotZ] offset from the bone (metres / degrees).</param>
public sealed record EmoteProp(string Model, int Bone, float[] Placement);

public enum PlacementKind
{
    /// <summary>This ped stands at (Side, Front, Height) in the partner's frame, turned by Heading degrees from the partner's heading.</summary>
    Offset,
    /// <summary>This ped (the whole entity) is attached to a bone of the partner with a prop-style offset.</summary>
    Attach,
}

/// <summary>
/// How the ped of a shared emote is placed relative to the other ped. Offsets come from rpemotes
/// <c>SyncOffsetSide/Front/Height/Heading</c> and scully <c>SideOffset/FrontOffset/HeightOffset/HeadingOffset</c>; attachments
/// from <c>Attachto + bone + pos + rot</c> and <c>Attach + Bone + Placement</c>. Both menus attach the ped whose emote carries
/// the attachment to the other ped, and move the initiating ped to its offset in the other ped's frame.
/// </summary>
public sealed record SharedPlacement
{
    public required PlacementKind Kind { get; init; }
    public float Side { get; init; }
    public float Front { get; init; }
    public float Height { get; init; }
    /// <summary>Degrees subtracted from the partner's heading (180 = facing the partner).</summary>
    public float Heading { get; init; } = 180f;
    /// <summary>Bone tag the ped is attached to; -1 attaches to the partner's entity origin.</summary>
    public int Bone { get; init; } = -1;
    /// <summary>[x, y, z, rotX, rotY, rotZ] offset from the bone (metres / degrees), same convention as <see cref="EmoteProp.Placement"/>.</summary>
    public float[] Placement { get; init; } = new float[6];

    public static SharedPlacement Offset(float side, float front, float height = 0f, float heading = 180f) =>
        new() { Kind = PlacementKind.Offset, Side = side, Front = front, Height = height, Heading = heading };

    public static SharedPlacement Attach(int bone, float[] placement) =>
        new() { Kind = PlacementKind.Attach, Bone = bone, Placement = placement };
}

/// <summary>
/// Bits of the animation flag the menus pass to the game (rpemotes <c>AnimFlag</c>: LOOP = 1, STUCK = 50, MOVING = 51;
/// scully <c>Flags.Loop / Move / Stuck</c> mapped the same way). The game plays flag 0 / 1 emotes in the primary slot
/// (full body, one clip at a time) and flags with <see cref="Secondary"/> in the secondary slot; with <see cref="UpperBody"/>
/// only the SKEL_Spine_Root subtree comes from the secondary clip, the pelvis and legs stay with the primary one.
/// </summary>
public static class AnimFlags
{
    public const int Looping = 1;
    public const int HoldLastFrame = 2;
    public const int UpperBody = 16;
    public const int Secondary = 32;

    /// <summary>rpemotes <c>AnimFlag.LOOP</c> / scully <c>Loop</c>.</summary>
    public const int Loop = Looping;
    /// <summary>rpemotes <c>AnimFlag.STUCK</c> / scully <c>Stuck</c>: secondary, upper body, holds the last frame.</summary>
    public const int Stuck = Secondary | UpperBody | HoldLastFrame;
    /// <summary>rpemotes <c>AnimFlag.MOVING</c> / scully <c>Move</c>: secondary, upper body, looping.</summary>
    public const int Moving = Stuck | Looping;

    public static bool IsLooping(int flag) => (flag & Looping) != 0;
    public static bool IsSecondary(int flag) => (flag & Secondary) != 0;
    public static bool IsUpperBody(int flag) => (flag & UpperBody) != 0;
}

public sealed class EmoteEntry
{
    /// <summary>
    /// Stable identifier <c>source/category/command</c> (with a <c>#n</c> suffix when a resource lists the same command
    /// twice in one category). Used in URLs and for the selection state of the UI.
    /// </summary>
    public string Id { get; internal set; } = "";

    /// <summary>Resource id the entry came from (<see cref="ResourceSource.Id"/>).</summary>
    public required string Source { get; init; }
    public required string Category { get; init; }    // Emotes / Dances / PropEmotes / general_emotes ...
    public required string Command { get; init; }
    public required string Label { get; init; }
    public required EmoteKind Kind { get; init; }

    /// <summary>
    /// Clip dictionary and clip. Loaders fill them from the resource; for walks they start as the guess "the clip set
    /// name is the dictionary name" and are replaced once the game's clip set table has been consulted (CatalogService).
    /// </summary>
    public string? Dictionary { get; set; }
    public string? Clip { get; set; }
    /// <summary>Scenario name, walk clipset or expression clip depending on Kind.</summary>
    public string? Name { get; init; }

    /// <summary>The animation flag the menu passes to the game (see <see cref="AnimFlags"/>); 0 when the resource gives none.</summary>
    public int AnimFlag { get; init; }
    /// <summary>LOOPING bit of <see cref="AnimFlag"/> (also set by MOVING).</summary>
    public bool Loop => AnimFlags.IsLooping(AnimFlag);
    /// <summary>SECONDARY bit: the emote plays in the game's secondary slot, so the ped can walk and a primary emote keeps playing underneath.</summary>
    public bool Move => AnimFlags.IsSecondary(AnimFlag);
    /// <summary>UPPERBODY bit: only the spine, arms and head come from this clip when it plays as a secondary.</summary>
    public bool UpperBody => AnimFlags.IsUpperBody(AnimFlag);
    public int? DurationMs { get; init; }
    public string? ExitEmote { get; init; }
    public IReadOnlyList<EmoteProp> Props { get; init; } = Array.Empty<EmoteProp>();

    /// <summary>
    /// Command of the other side of a shared emote (rpemotes <c>Shared</c> entries' fourth element, scully
    /// <c>Options.Shared.OtherEmote</c>), looked up in the same source and category; null for solo emotes.
    /// </summary>
    public string? PartnerCommand { get; init; }
    /// <summary>Id of the partner entry when <see cref="PartnerCommand"/> resolved (set by CatalogBuilder).</summary>
    public string? PartnerId { get; set; }
    /// <summary>Where this ped stands or hangs relative to the partner's ped; null when the resource gives nothing (the menus then default to facing each other 1 m apart).</summary>
    public SharedPlacement? Placement { get; init; }
    /// <summary>Milliseconds this side waits before its clip starts (rpemotes <c>StartDelay</c>, scully <c>Delay</c>).</summary>
    public int StartDelayMs { get; init; }

    /// <summary>True for the two halves of a two-ped emote (including self-pairs such as scully's synchronised dances).</summary>
    public bool IsShared => PartnerCommand != null;

    /// <summary>True when the dictionary is shipped as a .ycd inside the resource (set by CatalogBuilder).</summary>
    public bool IsCustom { get; set; }
    /// <summary>Path to the resource-shipped .ycd if IsCustom.</summary>
    public string? CustomYcdPath { get; set; }

    /// <summary>The clip previewed for movement clip sets (walks).</summary>
    public const string WalkClip = "walk";

    /// <summary>True when the entry names a dictionary and a clip that can be looked up (animations, and walks resolved by name).</summary>
    public bool HasClip => !string.IsNullOrEmpty(Dictionary) && !string.IsNullOrEmpty(Clip);

    /// <summary>Resource hints about the animal the emote is for (scully <c>PedTypes</c>); empty for human emotes.</summary>
    public IReadOnlyList<string> PedTypes { get; init; } = Array.Empty<string>();
    /// <summary>Set when the resource flags the entry as an animal emote (rpemotes <c>AnimalEmote</c>).</summary>
    public bool AnimalFlag { get; init; }

    /// <summary>
    /// Animal emotes use animal ped skeletons: an animal category, a <c>creatures@</c> dictionary, or a resource flag.
    /// The human sides of human–animal pairs (rpemotes flags both) are recognised by their non-animal dictionary.
    /// </summary>
    public bool IsAnimal => Category.Contains("animal", StringComparison.OrdinalIgnoreCase)
        || (Dictionary?.StartsWith("creatures@", StringComparison.OrdinalIgnoreCase) ?? false)
        || (AnimalFlag && AnimalPeds.LooksAnimal(Dictionary));

    /// <summary>The animal ped the clip is meant for (see <see cref="AnimalPeds"/>); null for human emotes.</summary>
    public string? AnimalPed => IsAnimal ? AnimalPeds.Resolve(Dictionary, PedTypes, Clip) : null;

    public override string ToString() =>
        Kind == EmoteKind.Animation ? $"[{Source}/{Category}] {Command}: {Dictionary} / {Clip} ({Label})"
                                    : $"[{Source}/{Category}] {Command}: {Kind} {Name} ({Label})";
}
