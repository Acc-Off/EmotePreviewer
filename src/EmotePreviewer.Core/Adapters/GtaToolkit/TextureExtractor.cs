using EmotePreviewer.Core.Textures;
using RageLib.Resources.Common;
using RageLib.Resources.GTA5.PC.Drawables;
using RageLib.Resources.GTA5.PC.Textures;

namespace EmotePreviewer.Core.Adapters.GtaToolkit;

/// <summary>
/// Converts gta-toolkit textures to <see cref="TextureImage"/> and reads which texture a shader uses as its diffuse.
/// Only the diffuse sampler is looked at; normal / specular / palette samplers are ignored.
/// </summary>
public static class TextureExtractor
{
    /// <summary>Shader parameter hash of the diffuse sampler.</summary>
    public const uint DiffuseSampler = 0xF1FE2B71;
    /// <summary>Shader parameter hash of the palette sampler (tinted hair / clothes: the diffuse is an intensity map).</summary>
    public const uint PaletteSampler = 0xAB98831E;

    /// <summary>Maps the game's format code (D3DFORMAT value or FourCC) to a <see cref="TextureFormat"/>.</summary>
    public static TextureFormat MapFormat(uint format) => format switch
    {
        0x31545844 => TextureFormat.Bc1, // DXT1
        0x33545844 => TextureFormat.Bc2, // DXT3
        0x35545844 => TextureFormat.Bc3, // DXT5
        0x31495441 => TextureFormat.Bc4, // ATI1
        0x32495441 => TextureFormat.Bc5, // ATI2
        0x20374342 or 0x00374342 => TextureFormat.Bc7, // "BC7 " / "BC7\0"
        21 or 32 or 28 or 50 => TextureFormat.Rgba8, // A8R8G8B8, A8B8G8R8, A8, L8 (converted)
        _ => TextureFormat.Unknown,
    };

    /// <summary>Converts a texture block; null when the format is unknown or the data is missing.</summary>
    public static TextureImage? Convert(TextureDX11 t)
    {
        var data = t.Data?.FullData;
        if (data == null || t.Width == 0 || t.Height == 0) return null;
        var format = MapFormat(t.Format);
        var name = t.Name?.Value ?? "";
        switch (format)
        {
            case TextureFormat.Unknown:
                return null;
            case TextureFormat.Rgba8:
            {
                int w = t.Width, h = t.Height;
                var rgba = new byte[w * h * 4];
                switch (t.Format)
                {
                    case 21: // A8R8G8B8: B G R A in memory
                        if (data.Length < rgba.Length) return null;
                        for (int i = 0; i < w * h; i++)
                        {
                            rgba[i * 4] = data[i * 4 + 2]; rgba[i * 4 + 1] = data[i * 4 + 1]; rgba[i * 4 + 2] = data[i * 4]; rgba[i * 4 + 3] = data[i * 4 + 3];
                        }
                        break;
                    case 32: // A8B8G8R8: R G B A in memory
                        if (data.Length < rgba.Length) return null;
                        Array.Copy(data, rgba, rgba.Length);
                        break;
                    case 28: // A8
                        if (data.Length < w * h) return null;
                        for (int i = 0; i < w * h; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = data[i]; }
                        break;
                    default: // L8
                        if (data.Length < w * h) return null;
                        for (int i = 0; i < w * h; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = data[i]; rgba[i * 4 + 3] = 255; }
                        break;
                }
                return new TextureImage { Name = name, Width = w, Height = h, Format = TextureFormat.Rgba8, Levels = 1, Data = rgba };
            }
            default:
            {
                // Count the levels the data actually covers (the reader sizes levels as stride × height / 4ⁿ, which
                // under-counts below 4×4; those levels are dropped here and padded again when the DDS is written).
                int levels = 0, offset = 0, w = t.Width, h = t.Height;
                for (int i = 0; i < Math.Max(1, (int)t.Levels); i++)
                {
                    var size = TextureImage.LevelSize(format, w, h);
                    if (offset + size > data.Length) break;
                    offset += size; levels++;
                    w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                }
                if (levels == 0) return null;
                var bytes = offset == data.Length ? data : data[..offset];
                return new TextureImage { Name = name, Width = t.Width, Height = t.Height, Format = format, Levels = levels, Data = bytes };
            }
        }
    }

    /// <summary>Every texture embedded in the drawable's shader group (props usually carry their diffuse this way).</summary>
    public static List<TextureImage> Embedded(Drawable drawable)
    {
        var result = new List<TextureImage>();
        var entries = drawable.ShaderGroup?.TextureDictionary?.Values?.Entries;
        if (entries == null) return result;
        foreach (var t in entries)
        {
            var img = Convert(t);
            if (img != null) result.Add(img);
        }
        return result;
    }

    /// <summary>Finds the texture in the dictionary whose name matches (case-insensitive).</summary>
    public static TextureImage? Find(PgDictionary64<TextureDX11> dictionary, string name)
    {
        var entries = dictionary.Values?.Entries;
        if (entries == null) return null;
        foreach (var t in entries)
            if (string.Equals(t.Name?.Value, name, StringComparison.OrdinalIgnoreCase)) return Convert(t);
        return null;
    }

    /// <summary>First texture of a dictionary (ped texture dictionaries hold exactly one).</summary>
    public static TextureImage? First(PgDictionary64<TextureDX11> dictionary)
    {
        var entries = dictionary.Values?.Entries;
        return entries == null || entries.Count == 0 ? null : Convert(entries[0]);
    }

    /// <summary>Shader parameter hash of <c>orderNumber</c>: hair shaders use 1 on a hull geometry drawn in a secondary pass.</summary>
    public const uint OrderNumberParam = 0x6063CE32;

    /// <summary>The first component of a numeric shader parameter, or null when the shader lacks it.</summary>
    public static float? NumericParameter(ShaderFX? shader, uint hash)
    {
        var ps = shader?.ParametersList;
        if (ps?.Parameters == null || ps.Hashes == null) return null;
        for (int i = 0; i < ps.Parameters.Count && i < ps.Hashes.Count; i++)
        {
            if (ps.Hashes[i] != hash || ps.Parameters[i].DataType == 0) continue;
            return ps.Parameters[i].Data is SimpleArray<System.Numerics.Vector4> arr && arr.Count > 0 ? arr[0].X : null;
        }
        return null;
    }

    /// <summary>Whether the shader tints its diffuse through a palette texture.</summary>
    public static bool HasPalette(ShaderFX? shader)
    {
        var ps = shader?.ParametersList;
        if (ps?.Parameters == null || ps.Hashes == null) return false;
        for (int i = 0; i < ps.Parameters.Count && i < ps.Hashes.Count; i++)
            if (ps.Hashes[i] == PaletteSampler && ps.Parameters[i].DataType == 0) return true;
        return false;
    }

    /// <summary>Name of the diffuse texture of a shader and whether it is embedded in the drawable (null when the shader has no diffuse sampler).</summary>
    public static (string name, bool embedded)? Diffuse(ShaderFX? shader)
    {
        var ps = shader?.ParametersList;
        if (ps?.Parameters == null || ps.Hashes == null) return null;
        for (int i = 0; i < ps.Parameters.Count && i < ps.Hashes.Count; i++)
        {
            if (ps.Hashes[i] != DiffuseSampler || ps.Parameters[i].DataType != 0) continue;
            var tex = ps.Parameters[i].Data as Texture;
            var name = tex?.Name?.Value;
            if (string.IsNullOrEmpty(name)) return null;
            return (name, tex is TextureDX11);
        }
        return null;
    }
}
