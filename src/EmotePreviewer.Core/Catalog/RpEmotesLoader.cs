using NLua;
using static EmotePreviewer.Core.Catalog.LuaHelpers;

namespace EmotePreviewer.Core.Catalog;

/// <summary>Loads rpemotes-reborn AnimationList.lua (RP.* tables).</summary>
public static class RpEmotesLoader
{
    static readonly HashSet<string> ScenarioTypes = new(StringComparer.Ordinal) { "MaleScenario", "Scenario", "ScenarioObject" };

    public static List<EmoteEntry> Load(string sourceId, string resourceRoot)
    {
        using var lua = CreateSandbox();
        lua.DoFile(Path.Combine(resourceRoot, "types.lua"));
        lua.DoFile(Path.Combine(resourceRoot, "client", "AnimationList.lua"));
        var custom = Path.Combine(resourceRoot, "client", "AnimationListCustom.lua");
        if (File.Exists(custom)) lua.DoFile(custom);

        var rp = Table(lua["RP"]) ?? throw new InvalidDataException("RP table not found");
        var result = new List<EmoteEntry>();
        // Lua hash tables have no stable iteration order, so categories and commands are sorted to keep ids and listings deterministic.
        foreach (var catObj in rp.Keys.Cast<object>().OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            var category = catObj.ToString()!;
            var cat = Table(rp[catObj]);
            if (cat == null) continue;
            foreach (var keyObj in cat.Keys.Cast<object>().OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                var command = keyObj.ToString()!;
                var e = Table(cat[keyObj]);
                if (e == null) continue;
                var entry = Parse(sourceId, category, command, e);
                if (entry != null) result.Add(entry);
            }
        }
        return result;
    }

    static EmoteEntry? Parse(string sourceId, string category, string command, LuaTable e)
    {
        var arr = Array(e);
        if (arr.Count == 0) return null;
        var opts = Table(e["AnimationOptions"]);
        var a0 = Str(arr[0]) ?? "";

        switch (category)
        {
            case "Expressions":
                return new EmoteEntry { Source = sourceId, Category = category, Command = command, Label = Str(arr.ElementAtOrDefault(1)) ?? command, Kind = EmoteKind.Expression, Name = a0 };
            case "Walks":
                return new EmoteEntry { Source = sourceId, Category = category, Command = command, Label = Str(arr.ElementAtOrDefault(1)) ?? command, Kind = EmoteKind.Walk, Name = a0, Dictionary = a0, Clip = EmoteEntry.WalkClip, Loop = true };
        }

        if (ScenarioTypes.Contains(a0))
        {
            return new EmoteEntry { Source = sourceId, Category = category, Command = command, Label = Str(arr.ElementAtOrDefault(2)) ?? command, Kind = EmoteKind.Scenario, Name = Str(arr.ElementAtOrDefault(1)) };
        }

        var props = new List<EmoteProp>();
        if (opts != null)
        {
            var p1 = Str(opts["Prop"]);
            if (p1 != null) props.Add(new EmoteProp(p1, Int(opts["PropBone"]) ?? 0, Vec6(Table(opts["PropPlacement"]))));
            var p2 = Str(opts["SecondProp"]);
            if (p2 != null) props.Add(new EmoteProp(p2, Int(opts["SecondPropBone"]) ?? 0, Vec6(Table(opts["SecondPropPlacement"]))));
        }
        var flag = opts != null ? Int(opts["onFootFlag"]) : null; // AnimFlag: LOOP=1, STUCK=50, MOVING=51
        // Shared emotes name the other side as the fourth element; a missing name means both peds play the same clip.
        var shared = string.Equals(category, "Shared", StringComparison.OrdinalIgnoreCase);
        return new EmoteEntry
        {
            Source = sourceId, Category = category, Command = command,
            Label = Str(arr.ElementAtOrDefault(2)) ?? command,
            Kind = EmoteKind.Animation,
            Dictionary = a0, Clip = Str(arr.ElementAtOrDefault(1)),
            Loop = flag == 1 || (opts != null && Bool(opts["EmoteLoop"])),
            Move = flag == 51 || (opts != null && Bool(opts["EmoteMoving"])),
            DurationMs = opts != null ? Int(opts["EmoteDuration"]) : null,
            ExitEmote = opts != null ? Str(opts["ExitEmote"]) : null,
            Props = props,
            AnimalFlag = Bool(e["AnimalEmote"]),
            PartnerCommand = shared ? Str(arr.ElementAtOrDefault(3)) ?? command : null,
            Placement = shared ? ReadPlacement(opts) : null,
            StartDelayMs = opts != null ? Int(opts["StartDelay"]) ?? 0 : 0,
        };
    }

    /// <summary><c>Attachto</c> wins over the sync offsets (the menu attaches first, which makes the offset moot).</summary>
    static SharedPlacement? ReadPlacement(LuaTable? opts)
    {
        if (opts == null) return null;
        if (Bool(opts["Attachto"]))
        {
            var pos = Table(opts["pos"]) is { } p ? Vec3(p) : new[] { Flt(opts["xPos"]), Flt(opts["yPos"]), Flt(opts["zPos"]) };
            var rot = Table(opts["rot"]) is { } r ? Vec3(r) : new[] { Flt(opts["xRot"]), Flt(opts["yRot"]), Flt(opts["zRot"]) };
            return SharedPlacement.Attach(Int(opts["bone"]) ?? -1, new[] { pos[0], pos[1], pos[2], rot[0], rot[1], rot[2] });
        }
        var side = FltOrNull(opts["SyncOffsetSide"]);
        var front = FltOrNull(opts["SyncOffsetFront"]);
        var height = FltOrNull(opts["SyncOffsetHeight"]);
        var heading = FltOrNull(opts["SyncOffsetHeading"]);
        if (side == null && front == null && height == null && heading == null) return null;
        return SharedPlacement.Offset(side ?? 0f, front ?? 1f, height ?? 0f, heading ?? 180f);
    }
}
