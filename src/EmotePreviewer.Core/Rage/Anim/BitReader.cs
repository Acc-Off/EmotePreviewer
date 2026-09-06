namespace EmotePreviewer.Core.Rage.Anim;

/// <summary>
/// LSB-first bit access over a byte span (spec §4.5): bit b lives in byte b/8, bit (b mod 8) with 0 = least significant.
/// Reads past the end of the data yield zero bits.
/// </summary>
internal static class BitReader
{
    /// <summary>Reads <paramref name="count"/> bits (0..32) starting at absolute bit position <paramref name="bitPos"/>.</summary>
    public static uint ReadBits(ReadOnlySpan<byte> data, long bitPos, int count)
    {
        if (count <= 0) return 0;
        long byteIndex = bitPos >> 3;
        int bitInByte = (int)(bitPos & 7);
        // Gather up to 8 bytes (enough for 32 bits + 7 bits of misalignment).
        ulong acc = 0;
        int need = bitInByte + count;
        int bytes = (need + 7) >> 3;
        for (int i = 0; i < bytes; i++)
        {
            long bi = byteIndex + i;
            if (bi < data.Length) acc |= (ulong)data[(int)bi] << (i * 8);
        }
        acc >>= bitInByte;
        return count >= 32 ? (uint)acc : (uint)(acc & ((1UL << count) - 1));
    }

    public static bool ReadBit(ReadOnlySpan<byte> data, long bitPos)
    {
        long bi = bitPos >> 3;
        if (bi >= data.Length) return false;
        return ((data[(int)bi] >> (int)(bitPos & 7)) & 1) != 0;
    }
}
