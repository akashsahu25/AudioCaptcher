using System.Buffers.Binary;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioStreaming.Backend.Services;

public sealed class OpusChunkEncoder
{
    public const int SampleRate = 16000;
    public const int Bitrate = 16000;
    public const int Channels = 1;

    public byte[] Encode(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        return Encode(reader);
    }

    public byte[] Encode(byte[] pcmData, WaveFormat format)
    {
        using var reader = new RawSourceWaveStream(new MemoryStream(pcmData, writable: false), format);
        return Encode(reader);
    }

    private static byte[] Encode(WaveStream reader)
    {
        ISampleProvider samples = reader.ToSampleProvider();
        samples = samples.WaveFormat.Channels switch
        {
            1 => samples,
            2 => new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f },
            _ => throw new NotSupportedException("Opus conversion currently supports mono/stereo capture.")
        };
        if (samples.WaveFormat.SampleRate != SampleRate)
            samples = new WdlResamplingSampleProvider(samples, SampleRate);

        var sourceFrames = reader.Length / reader.WaveFormat.BlockAlign;
        var sampleCount = checked((int)Math.Round(sourceFrames * (double)SampleRate /
            reader.WaveFormat.SampleRate, MidpointRounding.AwayFromZero));
        if (sampleCount == 0) throw new InvalidDataException("Cannot encode an empty audio chunk.");

        // Explicit managed encoder: no external executable or native Opus library required.
        var encoder = new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP)
        { Bitrate = Bitrate, UseVBR = false, UseDTX = false };
        var delay = encoder.Lookahead;
        const int frameSamples = SampleRate / 50; // 20 ms
        var packetCount = checked((sampleCount + delay + frameSamples - 1) / frameSamples);
        var pcm = new short[checked(packetCount * frameSamples)];
        var buffer = new float[4096];
        var offset = 0;
        while (offset < sampleCount)
        {
            var count = samples.Read(buffer.AsSpan(0, Math.Min(buffer.Length, sampleCount - offset)));
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                var value = float.IsFinite(buffer[i]) ? Math.Clamp(buffer[i], -1f, 1f) : 0f;
                pcm[offset + i] = (short)Math.Clamp((int)Math.Round(value * 32768f), -32768, 32767);
            }
            offset += count;
        }
        var packets = new List<byte[]>(packetCount);
        var packet = new byte[1275];
        for (var i = 0; i < packetCount; i++)
        {
            var count = encoder.Encode(pcm.AsSpan(i * frameSamples, frameSamples),
                frameSamples, packet.AsSpan(), packet.Length);
            packets.Add(packet.AsSpan(0, count).ToArray());
        }
        return WebmOpusMuxer.Write(packets, sampleCount, delay);
    }
}
