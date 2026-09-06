using System.Buffers.Binary;

namespace EmotePreviewer.Core.Textures;

/// <summary>Pixel formats the viewer can consume. Compressed ones are uploaded as-is (S3TC / BPTC), the rest as RGBA8.</summary>
public enum TextureFormat
{
    Unknown,
    /// <summary>DXT1 (BC1): 8 bytes per 4×4 block, opaque or 1-bit alpha.</summary>
    Bc1,
    /// <summary>DXT3 (BC2): 16 bytes per block, explicit alpha.</summary>
    Bc2,
    /// <summary>DXT5 (BC3): 16 bytes per block, interpolated alpha.</summary>
    Bc3,
    /// <summary>ATI1 (BC4): single channel.</summary>
    Bc4,
    /// <summary>ATI2 (BC5): two channels (normal maps).</summary>
    Bc5,
    /// <summary>BC7: 16 bytes per block, high quality RGBA.</summary>
    Bc7,
    /// <summary>8 bits per channel, R G B A byte order.</summary>
    Rgba8,
}

/// <summary>
/// One 2D texture with its mip chain, in a format the browser can upload directly. Uncompressed sources are converted
/// to RGBA8 on extraction so the only byte orders that leave the server are the BC block layouts and plain RGBA.
/// </summary>
public sealed class TextureImage
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required TextureFormat Format { get; init; }
    /// <summary>Number of mip levels stored in <see cref="Data"/> (the game keeps compressed chains down to 4×4, not 1×1).</summary>
    public required int Levels { get; init; }
    /// <summary>Mip levels back to back, largest first, each tightly packed.</summary>
    public required byte[] Data { get; init; }

    public bool IsCompressed => Format is TextureFormat.Bc1 or TextureFormat.Bc2 or TextureFormat.Bc3 or TextureFormat.Bc4 or TextureFormat.Bc5 or TextureFormat.Bc7;
    public bool HasAlpha => Format is TextureFormat.Bc2 or TextureFormat.Bc3 or TextureFormat.Bc7 or TextureFormat.Rgba8;

    public static string FormatName(TextureFormat f) => f switch
    {
        TextureFormat.Bc1 => "bc1",
        TextureFormat.Bc2 => "bc2",
        TextureFormat.Bc3 => "bc3",
        TextureFormat.Bc4 => "bc4",
        TextureFormat.Bc5 => "bc5",
        TextureFormat.Bc7 => "bc7",
        TextureFormat.Rgba8 => "rgba8",
        _ => "unknown",
    };

    /// <summary>Bytes per 4×4 block for compressed formats, 0 otherwise.</summary>
    public static int BlockBytes(TextureFormat f) => f switch
    {
        TextureFormat.Bc1 or TextureFormat.Bc4 => 8,
        TextureFormat.Bc2 or TextureFormat.Bc3 or TextureFormat.Bc5 or TextureFormat.Bc7 => 16,
        _ => 0,
    };

    /// <summary>Byte size of one mip level of the given dimensions.</summary>
    public static int LevelSize(TextureFormat f, int width, int height)
    {
        var block = BlockBytes(f);
        if (block > 0) return Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * block;
        return f == TextureFormat.Rgba8 ? width * height * 4 : 0;
    }

    /// <summary>Levels a complete chain down to 1×1 would have.</summary>
    public int FullChainLevels => 1 + (int)Math.Floor(Math.Log2(Math.Max(Width, Height)));

    /// <summary>Whether a texture of this format can be decoded to RGBA on the server (for browsers without S3TC).</summary>
    public bool CanDecode => Format is TextureFormat.Bc1 or TextureFormat.Bc3 or TextureFormat.Rgba8;

    /// <summary>
    /// Writes a DDS file (128-byte header, DX10 extension for BC7). Compressed chains that stop at 4×4 are padded down
    /// to 1×1 by repeating the last block, so the browser can enable mipmap filtering (WebGL needs a complete chain).
    /// </summary>
    public byte[] ToDds()
    {
        var block = BlockBytes(Format);
        var levels = new List<ReadOnlyMemory<byte>>();
        int offset = 0, w = Width, h = Height;
        int total = block > 0 ? FullChainLevels : Levels;
        ReadOnlyMemory<byte> last = default;
        for (int i = 0; i < total; i++)
        {
            var size = LevelSize(Format, w, h);
            if (i < Levels && offset + size <= Data.Length)
            {
                last = Data.AsMemory(offset, size);
                levels.Add(last);
                offset += size;
            }
            else if (block > 0 && last.Length >= block)
            {
                levels.Add(last[^block..]);
            }
            else break;
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        bool dx10 = Format == TextureFormat.Bc7;
        var headerSize = 4 + 124 + (dx10 ? 20 : 0);
        var bytes = new byte[headerSize + levels.Sum(l => l.Length)];
        var s = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x20534444); // "DDS "
        var hdr = s[4..];
        const uint DDSD_CAPS = 0x1, DDSD_HEIGHT = 0x2, DDSD_WIDTH = 0x4, DDSD_PITCH = 0x8, DDSD_PIXELFORMAT = 0x1000, DDSD_MIPMAPCOUNT = 0x20000, DDSD_LINEARSIZE = 0x80000;
        uint flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT | (block > 0 ? DDSD_LINEARSIZE : DDSD_PITCH);
        if (levels.Count > 1) flags |= DDSD_MIPMAPCOUNT;
        BinaryPrimitives.WriteUInt32LittleEndian(hdr, 124);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[8..], (uint)Height);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[12..], (uint)Width);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[16..], block > 0 ? (uint)levels[0].Length : (uint)(Width * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[24..], (uint)levels.Count);
        // dwReserved1[11] stays zero. Pixel format at offset 72.
        var pf = hdr[72..];
        BinaryPrimitives.WriteUInt32LittleEndian(pf, 32);
        if (block > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pf[4..], 0x4); // DDPF_FOURCC
            BinaryPrimitives.WriteUInt32LittleEndian(pf[8..], dx10 ? 0x30315844u : FourCC(Format));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pf[4..], 0x40 | 0x1); // DDPF_RGB | DDPF_ALPHAPIXELS
            BinaryPrimitives.WriteUInt32LittleEndian(pf[12..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(pf[16..], 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(pf[20..], 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(pf[24..], 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(pf[28..], 0xFF000000);
        }
        const uint DDSCAPS_COMPLEX = 0x8, DDSCAPS_TEXTURE = 0x1000, DDSCAPS_MIPMAP = 0x400000;
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[104..], DDSCAPS_TEXTURE | (levels.Count > 1 ? DDSCAPS_COMPLEX | DDSCAPS_MIPMAP : 0));
        int pos = 128;
        if (dx10)
        {
            var ext = s[pos..];
            BinaryPrimitives.WriteUInt32LittleEndian(ext, 98); // DXGI_FORMAT_BC7_UNORM
            BinaryPrimitives.WriteUInt32LittleEndian(ext[4..], 3); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
            BinaryPrimitives.WriteUInt32LittleEndian(ext[8..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(ext[12..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(ext[16..], 0);
            pos += 20;
        }
        foreach (var level in levels)
        {
            level.Span.CopyTo(s[pos..]);
            pos += level.Length;
        }
        return bytes;
    }

    static uint FourCC(TextureFormat f) => f switch
    {
        TextureFormat.Bc1 => 0x31545844, // DXT1
        TextureFormat.Bc2 => 0x33545844, // DXT3
        TextureFormat.Bc3 => 0x35545844, // DXT5
        TextureFormat.Bc4 => 0x31495441, // ATI1
        TextureFormat.Bc5 => 0x32495441, // ATI2
        _ => 0,
    };

    bool? _intensityMap;

    /// <summary>
    /// Whether the texture is a palette intensity map rather than a colour image: the game's ped shaders always list a
    /// palette sampler, so the diffuse content decides. Intensity maps (hair) keep the strand luminance in green and a
    /// highlight mask in red with the blue channel empty; real diffuses have colour in every channel. Decodes the top
    /// level once (cached).
    /// </summary>
    public bool IsIntensityMap
    {
        get
        {
            if (_intensityMap is { } known) return known;
            if (!CanDecode) return (_intensityMap = false).Value;
            var rgba = DecodeToRgba().Data;
            long r = 0, g = 0, b = 0; int samples = 0;
            for (int i = 0; i < rgba.Length; i += 4 * 7) { r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; samples++; }
            _intensityMap = samples > 0 && Math.Max(r, g) > 0 && b * 20 < Math.Max(r, g);
            return _intensityMap.Value;
        }
    }

    /// <summary>
    /// Palette-shaded textures (hair, tinted clothes) keep the strand / cloth intensity in the green channel (plus a
    /// highlight mask in red) and pick the colour from a palette in-game. This turns that intensity into a grey RGBA8
    /// texture (alpha kept) so the viewer can tint it with a flat colour instead of showing the raw green.
    /// </summary>
    public TextureImage ToGray()
    {
        var rgba = DecodeToRgba();
        var data = rgba.Data.ToArray();
        for (int i = 0; i < data.Length; i += 4) data[i] = data[i + 1] = data[i + 2] = Math.Max(data[i], data[i + 1]);
        return new TextureImage { Name = Name, Width = rgba.Width, Height = rgba.Height, Format = TextureFormat.Rgba8, Levels = 1, Data = data };
    }

    /// <summary>Decodes the top level to RGBA8 (BC1 / BC3 / RGBA8 only; see <see cref="CanDecode"/>). Returns a single-level texture.</summary>
    public TextureImage DecodeToRgba()
    {
        if (Format == TextureFormat.Rgba8) return this;
        if (!CanDecode) throw new NotSupportedException($"{Format} cannot be decoded on the server");
        var rgba = new byte[Width * Height * 4];
        var block = BlockBytes(Format);
        int blocksX = Math.Max(1, (Width + 3) / 4);
        int blocksY = Math.Max(1, (Height + 3) / 4);
        var pixel = new byte[64];
        for (int by = 0; by < blocksY; by++)
            for (int bx = 0; bx < blocksX; bx++)
            {
                var src = Data.AsSpan((by * blocksX + bx) * block, block);
                if (Format == TextureFormat.Bc1) BcDecoder.DecodeBc1Block(src, pixel, opaqueOnly: false);
                else BcDecoder.DecodeBc3Block(src, pixel);
                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= Height) break;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= Width) break;
                        Array.Copy(pixel, (py * 4 + px) * 4, rgba, (y * Width + x) * 4, 4);
                    }
                }
            }
        return new TextureImage { Name = Name, Width = Width, Height = Height, Format = TextureFormat.Rgba8, Levels = 1, Data = rgba };
    }
}

/// <summary>Software decoders for the two block formats nearly every game texture uses.</summary>
public static class BcDecoder
{
    /// <summary>Decodes one BC1 block into 16 RGBA pixels (row-major).</summary>
    public static void DecodeBc1Block(ReadOnlySpan<byte> src, Span<byte> rgba, bool opaqueOnly)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(src);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(src[2..]);
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(src[4..]);
        Span<byte> palette = stackalloc byte[16];
        Expand565(c0, palette);
        Expand565(c1, palette[4..]);
        if (c0 > c1 || opaqueOnly)
        {
            for (int i = 0; i < 3; i++)
            {
                palette[8 + i] = (byte)((2 * palette[i] + palette[4 + i] + 1) / 3);
                palette[12 + i] = (byte)((palette[i] + 2 * palette[4 + i] + 1) / 3);
            }
            palette[11] = 255; palette[15] = 255;
        }
        else
        {
            for (int i = 0; i < 3; i++) palette[8 + i] = (byte)((palette[i] + palette[4 + i]) / 2);
            palette[11] = 255;
            palette[12] = palette[13] = palette[14] = 0; palette[15] = 0;
        }
        for (int p = 0; p < 16; p++)
        {
            int idx = (int)((bits >> (p * 2)) & 3);
            palette.Slice(idx * 4, 4).CopyTo(rgba.Slice(p * 4, 4));
        }
    }

    /// <summary>Decodes one BC3 block (BC1 colour + interpolated alpha) into 16 RGBA pixels.</summary>
    public static void DecodeBc3Block(ReadOnlySpan<byte> src, Span<byte> rgba)
    {
        DecodeBc1Block(src[8..], rgba, opaqueOnly: true);
        byte a0 = src[0], a1 = src[1];
        Span<byte> alpha = stackalloc byte[8];
        alpha[0] = a0; alpha[1] = a1;
        if (a0 > a1)
            for (int i = 1; i < 7; i++) alpha[1 + i] = (byte)(((7 - i) * a0 + i * a1 + 3) / 7);
        else
        {
            for (int i = 1; i < 5; i++) alpha[1 + i] = (byte)(((5 - i) * a0 + i * a1 + 2) / 5);
            alpha[6] = 0; alpha[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)src[2 + i] << (8 * i);
        for (int p = 0; p < 16; p++) rgba[p * 4 + 3] = alpha[(int)((bits >> (p * 3)) & 7)];
    }

    static void Expand565(ushort c, Span<byte> rgba)
    {
        int r = (c >> 11) & 0x1F, g = (c >> 5) & 0x3F, b = c & 0x1F;
        rgba[0] = (byte)((r << 3) | (r >> 2));
        rgba[1] = (byte)((g << 2) | (g >> 4));
        rgba[2] = (byte)((b << 3) | (b >> 2));
        rgba[3] = 255;
    }
}
