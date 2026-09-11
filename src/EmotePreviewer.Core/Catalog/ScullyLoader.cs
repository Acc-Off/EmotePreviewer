using NLua;
using static EmotePreviewer.Core.Catalog.LuaHelpers;

namespace EmotePreviewer.Core.Catalog;

/// <summary>Loads scully_emotemenu shared/data/*.lua (files that `return {...}`).</summary>
public static class ScullyLoader
{
    public static List<EmoteEntry> Load(string sourceId, string resourceRoot)
    {
        var result = new List<EmoteEntry>();
        var dataDir = Path.Combine(resourceRoot, "shared", "data");
        using var lua = CreateSandbox();
        // ox_lib emulation used by derived data files (synchronized_dance_emotes.lua).
        lua["__resource_root"] = resourceRoot.Replace('\\', '/');
        lua.DoString(@"
            lib.load = function(mod)
                local f = assert(loadfile(__resource_root .. '/' .. mod:gsub('%.', '/') .. '.lua'))
                return f()
            end
            lib.table = {}
            lib.table.deepclone = function(t)
                if type(t) ~= 'table' then return t end
                local r = {}
                for k, v in pairs(t) do r[k] = lib.table.deepclone(v) end
                return r
            end
        ");

        foreach (var file in Directory.GetFiles(Path.Combine(dataDir, "emotes"), "*.lua"))
        {
            var root = Table(lua.DoFile(file).FirstOrDefault());
            if (root == null) continue;
            var category = Str(root["type"]) ?? Path.GetFileNameWithoutExtension(file);
            var options = Table(root["options"]);
            if (options == null) continue;
            foreach (var o in Array(options))
                if (o is LuaTable t) result.Add(ParseAnim(sourceId, category, t));
        }

        foreach (var (file, kind, field) in new[] {
            ("walks.lua", EmoteKind.Walk, "Walk"),
            ("scenarios.lua", EmoteKind.Scenario, "Scenario"),
            ("expressions.lua", EmoteKind.Expression, "Expression") })
        {
            var path = Path.Combine(dataDir, file);
            if (!File.Exists(path)) continue;
            var root = Table(lua.DoFile(path).FirstOrDefault());
            if (root == null) continue;
            foreach (var o in Array(root))
            {
                if (o is not LuaTable t) continue;
                var name = Str(t[field]);
                result.Add(new EmoteEntry
                {
                    Source = sourceId, Category = Path.GetFileNameWithoutExtension(file),
                    Command = Str(t["Command"]) ?? "", Label = Str(t["Label"]) ?? "",
                    Kind = kind, Name = name,
                    Dictionary = kind == EmoteKind.Walk ? name : null,
                    Clip = kind == EmoteKind.Walk && name != null ? EmoteEntry.WalkClip : null,
                    AnimFlag = kind == EmoteKind.Walk ? AnimFlags.Loop : 0,
                });
            }
        }
        return result;
    }

    static EmoteEntry ParseAnim(string sourceId, string category, LuaTable t)
    {
        var opts = Table(t["Options"]);
        var flags = opts != null ? Table(opts["Flags"]) : null;
        var props = new List<EmoteProp>();
        if (opts != null && Table(opts["Props"]) is { } pl)
            foreach (var po in Array(pl))
                if (po is LuaTable p)
                    props.Add(new EmoteProp(Str(p["Name"]) ?? "", Int(p["Bone"]) ?? 0, Vec6FromVec3Pair(Table(p["Placement"]))));

        var shared = opts != null ? Table(opts["Shared"]) : null;
        return new EmoteEntry
        {
            Source = sourceId, Category = category,
            Command = Str(t["Command"]) ?? "", Label = Str(t["Label"]) ?? "",
            Kind = EmoteKind.Animation,
            PartnerCommand = shared != null ? Str(shared["OtherEmote"]) ?? Str(t["Command"]) : null,
            Placement = ReadPlacement(shared),
            StartDelayMs = opts != null ? Int(opts["Delay"]) ?? 0 : 0,
            Dictionary = Str(t["Dictionary"]), Clip = Str(t["Animation"]),
            AnimFlag = ReadFlag(flags),
            DurationMs = opts != null ? Int(opts["Duration"]) : null,
            ExitEmote = opts != null ? Str(opts["ExitEmote"]) : null,
            Props = props,
            PedTypes = Table(t["PedTypes"]) is { } pt ? Array(pt).Select(Str).Where(s => s != null).Select(s => s!).ToList() : System.Array.Empty<string>(),
        };
    }

    /// <summary>The menu's <c>movementFlag = Flags.Stuck and 50 or Flags.Move and 51 or Flags.Loop and 1 or 0</c>.</summary>
    static int ReadFlag(LuaTable? flags)
    {
        if (flags == null) return 0;
        if (Bool(flags["Stuck"])) return AnimFlags.Stuck;
        if (Bool(flags["Move"])) return AnimFlags.Moving;
        if (Bool(flags["Loop"])) return AnimFlags.Loop;
        return 0;
    }

    /// <summary><c>Options.Shared</c>: <c>Attach</c> + <c>Bone</c> + <c>Placement</c>, or the four offsets.</summary>
    static SharedPlacement? ReadPlacement(LuaTable? shared)
    {
        if (shared == null) return null;
        if (Bool(shared["Attach"]))
            return SharedPlacement.Attach(Int(shared["Bone"]) ?? -1, Vec6FromVec3Pair(Table(shared["Placement"])));
        var side = FltOrNull(shared["SideOffset"]);
        var front = FltOrNull(shared["FrontOffset"]);
        var height = FltOrNull(shared["HeightOffset"]);
        var heading = FltOrNull(shared["HeadingOffset"]);
        if (side == null && front == null && height == null && heading == null) return null;
        return SharedPlacement.Offset(side ?? 0f, front ?? 1f, height ?? 0f, heading ?? 180f);
    }
}
