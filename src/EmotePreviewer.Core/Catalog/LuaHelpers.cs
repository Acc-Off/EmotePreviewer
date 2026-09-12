using NLua;

namespace EmotePreviewer.Core.Catalog;

internal static class LuaHelpers
{
    public static Lua CreateSandbox()
    {
        var lua = new Lua();
        lua.State.Encoding = System.Text.Encoding.UTF8;
        // Common FiveM / ox_lib globals referenced by data files.
        lua.DoString(@"
            -- rpemotes-reborn defines these in types.lua; the legacy rpemotes and dpemotes have no such file.
            AnimFlag = { MOVING = 51, LOOP = 1, STUCK = 50 }
            ScenarioType = { MALE = 'MaleScenario', SCENARIO = 'Scenario', OBJECT = 'ScenarioObject' }
            -- Legacy lists read menu strings from the resource config (Config.Languages[Config.MenuLanguage]['pee']);
            -- any lookup on this stub yields the stub again, so such expressions evaluate without the real config.lua.
            local any = {}
            setmetatable(any, { __index = function() return any end, __call = function() return any end })
            Config = any
            locale = function(k, ...) return k end
            Translate = function(k, ...) return k end
            vec3 = function(x, y, z) return { x = x, y = y, z = z } end
            vector3 = vec3
            vec4 = function(x, y, z, w) return { x = x, y = y, z = z, w = w } end
            vector4 = vec4
            GetConvar = function(name, default) return default end
            GetConvarInt = function(name, default) return default end
            GetResourceKvpString = function() return nil end
            lib = setmetatable({}, { __index = function() return function() end end })
            joaat = function(s) return 0 end
        ");
        return lua;
    }

    public static LuaTable? Table(object? o) => o as LuaTable;
    public static string? Str(object? o) => o switch { null => null, string s => s, _ => o.ToString() };
    public static bool Bool(object? o) => o is bool b && b;
    public static int? Int(object? o) => o switch { long l => (int)l, double d => (int)d, int i => i, _ => null };
    public static float Flt(object? o) => o switch { double d => (float)d, long l => l, int i => i, float f => f, _ => 0f };
    public static float? FltOrNull(object? o) => o is double or long or int or float ? Flt(o) : null;
    public static float[] Vec3(LuaTable? t) => t == null ? new float[3] : new[] { Flt(t["x"]), Flt(t["y"]), Flt(t["z"]) };

    /// <summary>Reads a Lua array part (1..n) as a list of objects.</summary>
    public static List<object> Array(LuaTable t)
    {
        var list = new List<object>();
        for (int i = 1; ; i++)
        {
            var v = t[i];
            if (v == null) break;
            list.Add(v);
        }
        return list;
    }

    public static float[] Vec6(LuaTable? t)
    {
        var r = new float[6];
        if (t == null) return r;
        var arr = Array(t);
        for (int i = 0; i < Math.Min(6, arr.Count); i++) r[i] = Flt(arr[i]);
        return r;
    }

    /// <summary>scully Placement = { vec3(pos), vec3(rot) } to [x,y,z,rx,ry,rz]</summary>
    public static float[] Vec6FromVec3Pair(LuaTable? t)
    {
        var r = new float[6];
        if (t == null) return r;
        var arr = Array(t);
        if (arr.Count > 0 && arr[0] is LuaTable p) { r[0] = Flt(p["x"]); r[1] = Flt(p["y"]); r[2] = Flt(p["z"]); }
        if (arr.Count > 1 && arr[1] is LuaTable q) { r[3] = Flt(q["x"]); r[4] = Flt(q["y"]); r[5] = Flt(q["z"]); }
        return r;
    }
}
