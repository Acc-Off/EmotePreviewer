using System.Buffers.Binary;

namespace EmotePreviewer.Core.Rage.Anim;

/// <summary>Sequence block header (spec §3.1, 32 bytes). Offsets in <see cref="FrameOffset"/> are relative to the data part.</summary>
public readonly record struct SequenceHeader(
    uint DataLength,
    uint FrameOffset,
    uint RootMotionRefsOffset,
    ushort NumFrames,
    ushort FrameLength,
    ushort IndirectQuantizeFloatNumInts,
    ushort QuantizeFloatValueBits,
    byte ChunkSize,
    byte RootMotionRefCounts)
{
    public const int Size = 32;

    public int RootMotionPositionRefs => RootMotionRefCounts >> 4;
    public int RootMotionRotationRefs => RootMotionRefCounts & 0xF;

    /// <summary>Parses the 32-byte header that precedes the data part of a sequence block.</summary>
    public static SequenceHeader Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length < Size) throw new ArgumentException("sequence header needs 32 bytes", nameof(header));
        return new SequenceHeader(
            DataLength: BinaryPrimitives.ReadUInt32LittleEndian(header[0x04..]),
            FrameOffset: BinaryPrimitives.ReadUInt32LittleEndian(header[0x0C..]),
            RootMotionRefsOffset: BinaryPrimitives.ReadUInt32LittleEndian(header[0x10..]),
            NumFrames: BinaryPrimitives.ReadUInt16LittleEndian(header[0x16..]),
            FrameLength: BinaryPrimitives.ReadUInt16LittleEndian(header[0x18..]),
            IndirectQuantizeFloatNumInts: BinaryPrimitives.ReadUInt16LittleEndian(header[0x1A..]),
            QuantizeFloatValueBits: BinaryPrimitives.ReadUInt16LittleEndian(header[0x1C..]),
            ChunkSize: header[0x1E],
            RootMotionRefCounts: header[0x1F]);
    }
}

/// <summary>One entry of the animation's track table (spec §2.1).</summary>
/// <param name="BoneId">Bone tag (matches <c>SkeletonDef.Bones[i].Tag</c>); 0 for the root.</param>
/// <param name="Format">0 = Vector3, 1 = Quaternion, 2 = Float.</param>
/// <param name="TrackId">Kind of quantity (0 = position, 1 = rotation, 2 = scale, 5/6 = root motion, ...).</param>
public readonly record struct TrackDef(ushort BoneId, byte Format, byte TrackId)
{
    public const byte FormatVector3 = 0;
    public const byte FormatQuaternion = 1;
    public const byte FormatFloat = 2;

    public bool IsQuaternion => Format == FormatQuaternion;
}

/// <summary>Channel types (spec §4.1).</summary>
public enum ChannelType : byte
{
    StaticQuaternion = 0,
    StaticVector3 = 1,
    StaticFloat = 2,
    RawFloat = 3,
    QuantizeFloat = 4,
    IndirectQuantizeFloat = 5,
    LinearFloat = 6,
    CachedQuaternion1 = 7,
    CachedQuaternion2 = 8,
}
