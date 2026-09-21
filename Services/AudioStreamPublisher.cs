using AudioStreaming.Backend.Hubs;
using AudioStreaming.Backend.Models;
using Microsoft.AspNetCore.SignalR;

namespace AudioStreaming.Backend.Services;

public sealed record AudioChunkMessage(Guid SessionId, int Sequence, string ContentType,
    int SampleRate, int Bitrate, int Channels, double DurationSeconds, long StartFrame,
    int SourceSampleRate, bool IsPartial, string DownloadPath, byte[] Data);

public sealed class AudioStreamPublisher(IHubContext<AudioHub> hub, ILogger<AudioStreamPublisher> logger)
{
    public Task ChunkAsync(AudioChunk chunk, byte[] bytes) => SendAsync(chunk.SessionId, "AudioChunk", new AudioChunkMessage(
        chunk.SessionId, chunk.Sequence, WebmOpusMuxer.ContentType, OpusChunkEncoder.SampleRate,
        OpusChunkEncoder.Bitrate, OpusChunkEncoder.Channels, chunk.FrameCount / (double)chunk.SampleRate,
        chunk.StartFrame, chunk.SampleRate, chunk.IsPartial,
        $"/api/audio/sessions/{chunk.SessionId}/chunks/{chunk.Sequence}", bytes));

    public Task EndAsync(CaptureStatus status) => SendAsync(status.SessionId, "SessionEnded", status);

    private async Task SendAsync(Guid sessionId, string name, object payload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await hub.Clients.Group(AudioHub.Group(sessionId)).SendAsync(name, payload, timeout.Token);
        }
        catch (Exception error)
        {
            // Disk is the recovery source. A disconnected/slow browser must not terminate capture.
            logger.LogWarning(error, "SignalR {Event} delivery failed for {Session}; client can recover saved chunks", name, sessionId);
        }
    }
}
