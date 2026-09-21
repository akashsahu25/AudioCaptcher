namespace AudioStreaming.Backend.Models;

public sealed record AudioDevice(string Id, string Name);
public sealed record AudioProcess(int ProcessId, string ProcessName);
public sealed record StartCaptureRequest(int? ProcessId = null, string? OutputKey = null);
public sealed record CaptureStatus(Guid SessionId, string DeviceId, string DeviceName,
    string State, string? Format, long BytesWritten, int ChunksSaved,
    int ChunksProcessed, string? LastFileName, string? Error, int SilentChunksSkipped = 0, long LeadingSilentFramesSkipped = 0,
    string? StopReason = null, int? ProcessId = null, string CaptureMode = "Process");
public sealed record AudioChunk(Guid SessionId, string DeviceId, int Sequence,
    string FilePath, long StartFrame, long FrameCount, int SampleRate, bool IsPartial,
    byte[] PcmData, NAudio.Wave.WaveFormat Format);


