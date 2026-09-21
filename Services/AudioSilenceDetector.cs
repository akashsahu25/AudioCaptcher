using NAudio.Wave;
using System.Buffers.Binary;
namespace AudioStreaming.Backend.Services;

public sealed class AudioSilenceDetector
{
    private readonly WaveFormat _format;
    private readonly WaveFormatEncoding _encoding;
    private readonly double _silenceThreshold;
    public AudioSilenceDetector(WaveFormat format, double threshold)
    {
        _format = format;
        _silenceThreshold = threshold;
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71"))
                encoding = WaveFormatEncoding.Pcm;
            else if (extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"))
                encoding = WaveFormatEncoding.IeeeFloat;
        }
        _encoding = encoding;
    }
    public bool IsSilent(ReadOnlySpan<byte> data)
    {
        var width = _format.BitsPerSample / 8;
        if (width == 0) return false;
        for (var offset = 0; offset + width <= data.Length; offset += width)
        {
            var sample = data.Slice(offset, width);
            double value;
            if (_encoding == WaveFormatEncoding.IeeeFloat && width == 4)
                value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample));
            else if (_encoding == WaveFormatEncoding.IeeeFloat && width == 8)
                value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(sample));
            else if (_encoding == WaveFormatEncoding.Pcm)
                value = width switch
                {
                    1 => (sample[0] - 128) / 128d,
                    2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d,
                    3 => ((sample[0] << 8 | sample[1] << 16 | sample[2] << 24) >> 8) / 8388608d,
                    4 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d,
                    _ => double.NaN
                };
            else return false; // Unknown representation: do not discard it.
            if (!double.IsFinite(value) || Math.Abs(value) > _silenceThreshold) return false;
        }
        return true;
    }

}

