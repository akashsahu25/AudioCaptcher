using AudioStreaming.Backend.Models;
using NAudio.Wave;

namespace AudioStreaming.Backend.Services;

public sealed class AudioChunkProcessor(ILogger<AudioChunkProcessor> logger, OpusChunkEncoder encoder,
    AudioStreamPublisher? publisher = null) : IAudioChunkProcessor
{
    public async Task<long> ProcessAsync(AudioChunk chunk)
    {
        if (chunk.PcmData.LongLength != chunk.FrameCount * chunk.Format.BlockAlign)
            throw new InvalidDataException("PCM length does not match chunk metadata.");
        var bytes = encoder.Encode(chunk.PcmData, chunk.Format);
        var outputPath = chunk.FilePath;
        var temporaryPath = outputPath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, bytes);
        File.Move(temporaryPath, outputPath, overwrite: true);
        if (publisher is not null) await publisher.ChunkAsync(chunk, bytes);
        logger.LogInformation("Encoded session {Session}, chunk {Sequence}: {File}, {Bytes} bytes; 16000 Hz mono, 16000 bps CBR",
            chunk.SessionId, chunk.Sequence, outputPath, bytes.Length);
        return bytes.LongLength;
    }
}

