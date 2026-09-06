using System.Buffers.Binary;
using System.Numerics;

namespace EmotePreviewer.Core.Rage.Anim;

/// <summary>
/// One decoded sequence block of an animation (spec §3–§4). Holds the channel layout and gives per-frame
/// track values (spec §5.1). Frame indices are local to the block and wrap modulo <see cref="NumFrames"/>.
/// </summary>
public sealed class DecodedSequence
{
    public SequenceHeader Header { get; }
    public int NumFrames => Header.NumFrames;
    public int TrackCount => _tracks.Length;

    readonly ReadOnlyMemory<byte> _data;
    readonly Channel[] _channels;
    readonly TrackChannels[] _tracks;

    sealed class Channel
    {
        public ChannelType Type;
        public int Track;
        public int Slot;
        public int QuatIndex;          // type 7 only
        // frame-record channels (types 3, 4, 5)
        public long FrameBitOffset;    // bit offset inside a frame record
        public int FrameBits;
        // static values (types 0, 1, 2): up to 4 components
        public float S0, S1, S2, S3;
        // QuantizeFloat / IndirectQuantizeFloat
        public float Quantum, Offset;
        public float[]? Table;         // IndirectQuantizeFloat value table
        public float[]? Linear;        // LinearFloat decoded per-frame values
    }

    readonly struct TrackChannels
    {
        public TrackChannels(int[] slotOrder, int[] valueChannels, int cachedQuatIndex)
        { SlotOrder = slotOrder; ValueChannels = valueChannels; CachedQuatIndex = cachedQuatIndex; }
        public readonly int[] SlotOrder;      // all channel indices sorted by slot (includes 7/8)
        public readonly int[] ValueChannels;  // channels that carry values (excludes 7/8), slot order
        public readonly int CachedQuatIndex;  // -1 = no CachedQuaternion1; otherwise the component position of the reconstructed value
    }

    DecodedSequence(SequenceHeader header, ReadOnlyMemory<byte> data, Channel[] channels, TrackChannels[] tracks)
    {
        Header = header; _data = data; _channels = channels; _tracks = tracks;
    }

    /// <summary>
    /// Parses the data part of a sequence block. <paramref name="data"/> must start right after the 32-byte header and
    /// have at least <see cref="SequenceHeader.DataLength"/> bytes.
    /// </summary>
    public static DecodedSequence Parse(SequenceHeader header, ReadOnlyMemory<byte> data, IReadOnlyList<TrackDef> tracks)
    {
        var span = data.Span;
        if (span.Length < header.DataLength) throw new InvalidDataException($"sequence data is {span.Length} bytes, header says {header.DataLength}");
        int numFrames = header.NumFrames;
        int channelListOffset = checked((int)(header.FrameOffset + (long)header.FrameLength * numFrames));
        if (channelListOffset + 18 > span.Length) throw new InvalidDataException("channel count table lies outside the sequence data");

        Span<int> counts = stackalloc int[9];
        for (int t = 0; t < 9; t++) counts[t] = BinaryPrimitives.ReadUInt16LittleEndian(span[(channelListOffset + t * 2)..]);

        var channels = new List<Channel>();
        int descPos = channelListOffset + 18;
        int paramPos = 0;
        long frameBitPos = 0;
        for (int type = 0; type < 9; type++)
        {
            int count = counts[type];
            for (int k = 0; k < count; k++)
            {
                ushort desc = BinaryPrimitives.ReadUInt16LittleEndian(span[descPos..]);
                descPos += 2;
                var ch = new Channel { Type = (ChannelType)type, Track = desc >> 2, Slot = desc & 3 };
                switch (ch.Type)
                {
                    case ChannelType.StaticQuaternion:
                    {
                        float x = ReadF32(span, paramPos), y = ReadF32(span, paramPos + 4), z = ReadF32(span, paramPos + 8);
                        ch.S0 = x; ch.S1 = y; ch.S2 = z;
                        ch.S3 = MathF.Sqrt(MathF.Max(1f - (x * x + y * y + z * z), 0f));
                        paramPos += 12;
                        break;
                    }
                    case ChannelType.StaticVector3:
                        ch.S0 = ReadF32(span, paramPos); ch.S1 = ReadF32(span, paramPos + 4); ch.S2 = ReadF32(span, paramPos + 8);
                        paramPos += 12;
                        break;
                    case ChannelType.StaticFloat:
                        ch.S0 = ReadF32(span, paramPos);
                        paramPos += 4;
                        break;
                    case ChannelType.RawFloat:
                        ch.FrameBitOffset = frameBitPos; ch.FrameBits = 32;
                        frameBitPos += 32;
                        break;
                    case ChannelType.QuantizeFloat:
                    {
                        int valueBits = ReadI32(span, paramPos);
                        ch.Quantum = ReadF32(span, paramPos + 4); ch.Offset = ReadF32(span, paramPos + 8);
                        ch.FrameBitOffset = frameBitPos; ch.FrameBits = valueBits;
                        frameBitPos += valueBits;
                        paramPos += 12;
                        break;
                    }
                    case ChannelType.IndirectQuantizeFloat:
                    {
                        int frameBits = ReadI32(span, paramPos);
                        int valueBits = ReadI32(span, paramPos + 4);
                        int numInts = ReadI32(span, paramPos + 8);
                        ch.Quantum = ReadF32(span, paramPos + 12); ch.Offset = ReadF32(span, paramPos + 16);
                        int tableStart = paramPos + 20;
                        long maxByBits = frameBits >= 31 ? int.MaxValue : (1L << frameBits) - 1;
                        long maxByInts = valueBits > 0 ? (long)numInts * 32 / valueBits : maxByBits;
                        int numValues = (int)Math.Max(0, Math.Min(maxByBits, maxByInts));
                        var table = new float[numValues];
                        long bit = (long)tableStart * 8;
                        for (int j = 0; j < numValues; j++, bit += valueBits)
                            table[j] = BitReader.ReadBits(span, bit, valueBits) * ch.Quantum + ch.Offset;
                        ch.Table = table;
                        ch.FrameBitOffset = frameBitPos; ch.FrameBits = frameBits;
                        frameBitPos += frameBits;
                        paramPos = tableStart + numInts * 4;
                        break;
                    }
                    case ChannelType.LinearFloat:
                    {
                        int numInts = ReadI32(span, paramPos);
                        int countsWord = ReadI32(span, paramPos + 4);
                        ch.Quantum = ReadF32(span, paramPos + 8); ch.Offset = ReadF32(span, paramPos + 12);
                        ch.Linear = DecodeLinearFloat(span, paramPos, numInts, countsWord, ch.Quantum, ch.Offset, numFrames, header.ChunkSize);
                        paramPos += numInts * 4;
                        break;
                    }
                    case ChannelType.CachedQuaternion1:
                        ch.QuatIndex = desc & 3; ch.Slot = 3;
                        break;
                    case ChannelType.CachedQuaternion2:
                        ch.Slot = 4;
                        break;
                }
                channels.Add(ch);
            }
            int rem = count & 3;
            if (rem != 0) descPos += (4 - rem) * 2;
        }

        // Group channels by track, slot order.
        var perTrack = new List<int>[tracks.Count];
        for (int i = 0; i < channels.Count; i++)
        {
            var ch = channels[i];
            if (ch.Track < 0 || ch.Track >= tracks.Count)
                throw new InvalidDataException($"channel refers to track {ch.Track} but the animation has {tracks.Count} tracks");
            (perTrack[ch.Track] ??= new List<int>()).Add(i);
        }
        var trackChannels = new TrackChannels[tracks.Count];
        for (int t = 0; t < tracks.Count; t++)
        {
            var list = perTrack[t];
            if (list == null) { trackChannels[t] = new TrackChannels(Array.Empty<int>(), Array.Empty<int>(), -1); continue; }
            var ordered = list.OrderBy(i => channels[i].Slot).ToArray();
            int cachedQuat = -1;
            var values = new List<int>(ordered.Length);
            foreach (var i in ordered)
            {
                var ch = channels[i];
                if (ch.Type == ChannelType.CachedQuaternion1) cachedQuat = ch.QuatIndex;
                else if (ch.Type != ChannelType.CachedQuaternion2) values.Add(i);
            }
            trackChannels[t] = new TrackChannels(ordered, values.ToArray(), cachedQuat);
        }
        return new DecodedSequence(header, data, channels.ToArray(), trackChannels);
    }

    static float ReadF32(ReadOnlySpan<byte> s, int pos) => BinaryPrimitives.ReadSingleLittleEndian(s[pos..]);
    static int ReadI32(ReadOnlySpan<byte> s, int pos) => BinaryPrimitives.ReadInt32LittleEndian(s[pos..]);

    /// <summary>LinearFloat (spec §4.6): per-chunk second-order delta coding with an Elias-gamma-like zero run.</summary>
    static float[] DecodeLinearFloat(ReadOnlySpan<byte> data, int channelStart, int numInts, int counts, float quantum, float offset, int numFrames, int chunkSize)
    {
        var result = new float[numFrames];
        if (numFrames == 0) return result;
        if (chunkSize <= 0) chunkSize = 1;
        int c1 = counts & 0xFF, c2 = (counts >> 8) & 0xFF, c3 = (counts >> 16) & 0xFF;
        int numChunks = (numFrames + chunkSize - 1) / chunkSize;
        long startBit = (long)(channelStart + 16) * 8;
        long endBit = (long)(channelStart + numInts * 4) * 8;
        if (endBit > (long)data.Length * 8) endBit = (long)data.Length * 8;

        long deltaBase = startBit + (long)numChunks * (c1 + c2);
        for (int i = 0; i < numChunks; i++)
        {
            uint chunkOffset = c1 == 0 ? 0 : BitReader.ReadBits(data, startBit + (long)i * c1, c1);
            uint chunkValue = c2 == 0 ? 0 : BitReader.ReadBits(data, startBit + (long)numChunks * c1 + (long)i * c2, c2);
            long bitpos = deltaBase + chunkOffset;
            int value = (int)chunkValue;
            int inc = 0;
            for (int j = 0; j < chunkSize; j++)
            {
                int frame = i * chunkSize + j;
                if (frame >= numFrames) break;
                result[frame] = value * quantum + offset;
                if (j + 1 >= chunkSize) break;
                int delta = c3 == 0 ? 0 : (int)BitReader.ReadBits(data, bitpos, c3);
                bitpos += c3;
                int k = 0;
                while (bitpos < endBit && !BitReader.ReadBit(data, bitpos)) { k++; bitpos++; }
                bitpos++; // the terminating 1 bit
                delta |= k << c3;
                if (delta != 0)
                {
                    if (BitReader.ReadBit(data, bitpos)) delta = -delta;
                    bitpos++;
                }
                inc += delta;
                value += inc;
            }
        }
        return result;
    }

    /// <summary>Channel types of a track in slot order (spec §4.3), e.g. [4,4,4,7]. Used to validate descriptor parsing.</summary>
    public IReadOnlyList<ChannelType> GetChannelTypes(int track)
    {
        var order = _tracks[track].SlotOrder;
        var result = new ChannelType[order.Length];
        for (int i = 0; i < order.Length; i++) result[i] = _channels[order[i]].Type;
        return result;
    }

    // ------------------------------------------------------------------ evaluation (spec §5.1)

    /// <summary>Appends the channel components for local frame <paramref name="frame"/> (already wrapped) to <paramref name="dst"/>.</summary>
    void ReadChannel(Channel ch, int frame, Span<float> dst, ref int n)
    {
        if (n >= dst.Length) return;
        switch (ch.Type)
        {
            case ChannelType.StaticQuaternion:
                dst[n++] = ch.S0; if (n < dst.Length) dst[n++] = ch.S1; if (n < dst.Length) dst[n++] = ch.S2; if (n < dst.Length) dst[n++] = ch.S3;
                return;
            case ChannelType.StaticVector3:
                dst[n++] = ch.S0; if (n < dst.Length) dst[n++] = ch.S1; if (n < dst.Length) dst[n++] = ch.S2;
                return;
            case ChannelType.StaticFloat:
                dst[n++] = ch.S0;
                return;
            case ChannelType.LinearFloat:
                dst[n++] = ch.Linear![frame];
                return;
            case ChannelType.RawFloat:
            {
                uint bits = BitReader.ReadBits(_data.Span, FrameBit(frame) + ch.FrameBitOffset, 32);
                dst[n++] = BitConverter.UInt32BitsToSingle(bits);
                return;
            }
            case ChannelType.QuantizeFloat:
            {
                uint q = BitReader.ReadBits(_data.Span, FrameBit(frame) + ch.FrameBitOffset, ch.FrameBits);
                dst[n++] = q * ch.Quantum + ch.Offset;
                return;
            }
            case ChannelType.IndirectQuantizeFloat:
            {
                uint j = BitReader.ReadBits(_data.Span, FrameBit(frame) + ch.FrameBitOffset, ch.FrameBits);
                var table = ch.Table!;
                dst[n++] = j < (uint)table.Length ? table[j] : 0f;
                return;
            }
            default:
                return;
        }
    }

    long FrameBit(int frame) => ((long)Header.FrameOffset + (long)Header.FrameLength * frame) * 8;

    int Wrap(int frame)
    {
        int nf = Header.NumFrames;
        if (nf <= 0) return 0;
        frame %= nf;
        return frame < 0 ? frame + nf : frame;
    }

    /// <summary>Vector/float track value at a local frame (0 ≤ frame ≤ NumFrames; NumFrames wraps to 0). Missing components are 0.</summary>
    public Vector4 GetVector(int track, int frame)
    {
        Span<float> c = stackalloc float[4];
        c.Clear();
        int n = 0;
        int f = Wrap(frame);
        foreach (var i in _tracks[track].ValueChannels)
        {
            ReadChannel(_channels[i], f, c, ref n);
            if (n >= 4) break;
        }
        return new Vector4(c[0], c[1], c[2], c[3]);
    }

    /// <summary>Quaternion track value at a local frame, without normalization (spec §5.1 / §4.7).</summary>
    public Quaternion GetQuaternion(int track, int frame)
    {
        ref readonly var tc = ref _tracks[track];
        if (tc.SlotOrder.Length == 0) return Quaternion.Identity; // spec §4.3: a track without channels is the identity
        Span<float> c = stackalloc float[4];
        c.Clear();
        int f = Wrap(frame);
        if (tc.CachedQuatIndex >= 0)
        {
            Span<float> abc = stackalloc float[3];
            abc.Clear();
            int m = 0;
            foreach (var i in tc.ValueChannels)
            {
                ReadChannel(_channels[i], f, abc, ref m);
                if (m >= 3) break;
            }
            float a = abc[0], b = abc[1], cc = abc[2];
            float w = MathF.Sqrt(MathF.Max(1f - (a * a + b * b + cc * cc), 0f));
            int qi = tc.CachedQuatIndex;
            int src = 0;
            for (int k = 0; k < 4; k++) c[k] = k == qi ? w : abc[src++];
            return new Quaternion(c[0], c[1], c[2], c[3]);
        }
        int n = 0;
        foreach (var i in tc.ValueChannels)
        {
            ReadChannel(_channels[i], f, c, ref n);
            if (n >= 4) break;
        }
        return new Quaternion(c[0], c[1], c[2], c[3]);
    }
}
