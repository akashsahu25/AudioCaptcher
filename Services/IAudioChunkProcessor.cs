using AudioStreaming.Backend.Models;

namespace AudioStreaming.Backend.Services;

public interface IAudioChunkProcessor
{
    // Owns a complete PCM buffer; returns persisted WebM byte count. Supports concurrent sessions.
    Task<long> ProcessAsync(AudioChunk chunk);
}
