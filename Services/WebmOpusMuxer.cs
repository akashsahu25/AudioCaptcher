using System.Buffers.Binary;
using System.Text;

namespace AudioStreaming.Backend.Services;

// Minimal finite audio-only WebM writer: one mono Opus track, 20ms packets, 1s clusters.
// No external muxer process. Not a general-purpose Matroska/video writer.
public static class WebmOpusMuxer
{
    public const string ContentType = "audio/webm; codecs=opus";

    public static byte[] Write(IReadOnlyList<byte[]> packets, int sampleCount, int delay)
    {
        const int rate = OpusChunkEncoder.SampleRate;
        const int frame = rate / 50;
        if (sampleCount <= 0 || delay < 0 || packets.Count != (sampleCount + delay + frame - 1) / frame)
            throw new ArgumentException("Invalid Opus packet count or duration.");
        var privateData = new byte[19];
        "OpusHead"u8.CopyTo(privateData);
        privateData[8] = 1;
        privateData[9] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(privateData.AsSpan(10), checked((ushort)(delay * 3)));
        BinaryPrimitives.WriteInt32LittleEndian(privateData.AsSpan(12), rate);

        var header = Master(0x1A45DFA3,
            UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4), UInt(0x42F3, 8),
            Text(0x4282, "webm"), UInt(0x4287, 4), UInt(0x4285, 2));
        var info = Master(0x1549A966, UInt(0x2AD7B1, 1_000_000),
            Float(0x4489, sampleCount * 1000d / rate),
            Text(0x4D80, "AudioStreaming"), Text(0x5741, "AudioStreaming"));
        var tracks = Master(0x1654AE6B, Master(0xAE,
            UInt(0xD7, 1), UInt(0x73C5, 1), UInt(0x83, 2), UInt(0x9C, 0),
            Text(0x86, "A_OPUS"), Element(0x63A2, privateData),
            UInt(0x56AA, (ulong)delay * 1_000_000_000 / rate), UInt(0x56BB, 80_000_000),
            Master(0xE1, Float(0xB5, rate), UInt(0x9F, 1))));
        var segmentElements = new List<byte[]> { info, tracks };
        var cues = new List<byte[]>();
        long position = info.Length + tracks.Length;
        for (var first = 0; first < packets.Count; first += 50)
        {
            var cluster = new List<byte[]> { UInt(0xE7, (ulong)first * 20) };
            for (var i = first; i < Math.Min(first + 50, packets.Count); i++)
            {
                var block = new byte[4 + packets[i].Length];
                block[0] = 0x81; // Track 1 (EBML vint).
                BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(1), (short)((i - first) * 20));
                packets[i].CopyTo(block, 4);
                if (i == packets.Count - 1)
                {
                    var padding = (long)packets.Count * frame - sampleCount - delay;
                    var signed = new byte[8];
                    BinaryPrimitives.WriteInt64BigEndian(signed, padding * 1_000_000_000 / rate);
                    cluster.Add(Master(0xA0, Element(0xA1, block), Element(0x75A2, signed)));
                }
                else
                {
                    block[3] = 0x80; // Audio keyframe, no lacing.
                    cluster.Add(Element(0xA3, block));
                }
            }
            var encodedCluster = Master(0x1F43B675, cluster.ToArray());
            cues.Add(Master(0xBB, UInt(0xB3, (ulong)first * 20),
                Master(0xB7, UInt(0xF7, 1), UInt(0xF1, (ulong)position))));
            segmentElements.Add(encodedCluster);
            position += encodedCluster.Length;
        }
        segmentElements.Add(Master(0x1C53BB6B, cues.ToArray()));
        return Join(header, Master(0x18538067, segmentElements.ToArray()));
    }

    private static byte[] UInt(uint id, ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        var start = 0;
        while (start < 7 && bytes[start] == 0) start++;
        return Element(id, bytes[start..]);
    }
    private static byte[] Float(uint id, double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        return Element(id, bytes);
    }
    private static byte[] Text(uint id, string value) => Element(id, Encoding.UTF8.GetBytes(value));
    private static byte[] Master(uint id, params byte[][] children) => Element(id, Join(children));
    private static byte[] Join(params byte[][] items)
    {
        using var stream = new MemoryStream();
        foreach (var item in items) stream.Write(item);
        return stream.ToArray();
    }
    private static byte[] Element(uint id, byte[] data)
    {
        var idBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(idBytes, id);
        var start = 0;
        while (start < 3 && idBytes[start] == 0) start++;
        var width = 1;
        while ((ulong)data.Length >= (1UL << (7 * width)) - 1) width++;
        var size = new byte[width];
        var value = (ulong)(uint)data.Length | (1UL << (7 * width));
        for (var i = width - 1; i >= 0; i--) { size[i] = (byte)value; value >>= 8; }
        return Join(idBytes[start..], size, data);
    }
}

