using System.Buffers.Binary;
using EmotePreviewer.Core.Textures;

namespace EmotePreviewer.Core.Tests.Textures;

public sealed class TextureImageTests
{
    static TextureImage Bc1(int width, int height, int levels)
    {
        var size = 0;
        int w = width, h = height;
        for (int i = 0; i < levels; i++) { size += TextureImage.LevelSize(TextureFormat.Bc1, w, h); w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
        var data = new byte[size];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
        return new TextureImage { Name = "t", Width = width, Height = height, Format = TextureFormat.Bc1, Levels = levels, Data = data };
    }

    [Fact]
    public void DdsHeaderDescribesTheImage()
    {
        var img = Bc1(16, 8, 3); // 16×8, 8×4, 4×2 stored → padded with 2×1 and 1×1
        var dds = img.ToDds();
        Assert.Equal(0x20534444u, BinaryPrimitives.ReadUInt32LittleEndian(dds));
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(4)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(12)));  // height
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(16))); // width
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(28)));  // mip count: full chain down to 1×1
        Assert.Equal(0x31545844u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(84))); // "DXT1"
        // 4×2 blocks + 2×1 + 1×1 (8 bytes each) + 2 padded levels (copies of the last block)
        Assert.Equal(128 + (8 + 2 + 1) * 8 + 2 * 8, dds.Length);
        Assert.Equal(dds.AsSpan(128 + 10 * 8, 8).ToArray(), dds.AsSpan(128 + 11 * 8, 8).ToArray());
        Assert.Equal(img.Data.AsSpan(0, 64).ToArray(), dds.AsSpan(128, 64).ToArray());
    }

    [Fact]
    public void Bc7UsesTheDx10Header()
    {
        var img = new TextureImage { Name = "t", Width = 4, Height = 4, Format = TextureFormat.Bc7, Levels = 1, Data = new byte[16] };
        var dds = img.ToDds();
        Assert.Equal(0x30315844u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(84))); // "DX10"
        Assert.Equal(98u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(128)));       // DXGI_FORMAT_BC7_UNORM
        Assert.Equal(148 + 16 * 3, dds.Length); // 4×4, 2×2, 1×1
    }

    [Fact]
    public void Rgba8IsWrittenUncompressedWithRgbaMasks()
    {
        var img = new TextureImage { Name = "t", Width = 2, Height = 2, Format = TextureFormat.Rgba8, Levels = 1, Data = new byte[16] };
        var dds = img.ToDds();
        Assert.Equal(0x41u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(80)));       // DDPF_RGB | DDPF_ALPHAPIXELS
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(88)));
        Assert.Equal(0x000000FFu, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(92)));
        Assert.Equal(0xFF000000u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(104)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(28)));
        Assert.Equal(128 + 16, dds.Length);
    }

    [Fact]
    public void Bc1BlockDecodesEndpointsAndInterpolants()
    {
        // c0 = pure red (0xF800), c1 = pure blue (0x001F); c0 > c1 → 4-colour mode. Pixel indices 0,1,2,3 repeated.
        var block = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block, 0xF800);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), 0x001F);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 0b11100100_11100100_11100100_11100100);
        var rgba = new byte[64];
        BcDecoder.DecodeBc1Block(block, rgba, opaqueOnly: false);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[0..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba[4..8]);
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, rgba[8..12]);
        Assert.Equal(new byte[] { 85, 0, 170, 255 }, rgba[12..16]);
    }

    [Fact]
    public void Bc3BlockDecodesAlpha()
    {
        var block = new byte[16];
        block[0] = 255; block[1] = 0; // a0 > a1 → 8-value ramp
        // alpha indices: pixel 0 → 0 (255), pixel 1 → 1 (0), pixel 2 → 2 (219), others 0
        ulong bits = 0b010_001_000;
        for (int i = 0; i < 6; i++) block[2 + i] = (byte)(bits >> (8 * i));
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(10), 0xFFFF);
        var rgba = new byte[64];
        BcDecoder.DecodeBc3Block(block, rgba);
        Assert.Equal(255, rgba[3]);
        Assert.Equal(0, rgba[7]);
        Assert.Equal(219, rgba[11]);
        Assert.Equal(new byte[] { 255, 255, 255 }, rgba[0..3]);
    }

    [Fact]
    public void IntensityMapsAreDetectedByContent()
    {
        static TextureImage Rgba(byte r, byte g, byte b)
        {
            var data = new byte[8 * 8 * 4];
            for (int i = 0; i < data.Length; i += 4) { data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = 255; }
            return new TextureImage { Name = "t", Width = 8, Height = 8, Format = TextureFormat.Rgba8, Levels = 1, Data = data };
        }
        Assert.True(Rgba(20, 200, 0).IsIntensityMap);   // hair: intensity in G, no B
        Assert.True(Rgba(120, 90, 0).IsIntensityMap);   // two-tone hair: highlight mask in R, still no B
        Assert.Equal(new byte[] { 120, 120, 120, 255 }, Rgba(120, 90, 0).ToGray().Data[0..4]);
        Assert.False(Rgba(180, 140, 120).IsIntensityMap); // skin
        Assert.False(Rgba(40, 40, 40).IsIntensityMap);    // dark cloth
        Assert.False(Rgba(0, 0, 0).IsIntensityMap);
    }

    [Fact]
    public void DecodeToRgbaCoversTheWholeImage()
    {
        var img = Bc1(8, 8, 1);
        var rgba = img.DecodeToRgba();
        Assert.Equal(TextureFormat.Rgba8, rgba.Format);
        Assert.Equal(8 * 8 * 4, rgba.Data.Length);
        Assert.Equal(1, rgba.Levels);
        Assert.Equal(4, img.FullChainLevels);
    }
}
