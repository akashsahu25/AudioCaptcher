namespace AudioStreaming.Backend.Services;

public sealed class AudioCaptureOptions
{
    // Peak sample level required to start a new chunk. Not applied inside an active chunk.
    public double StartThresholdDbfs { get; set; } = -60;
    public int StartConfirmationMs { get; set; } = 100;
    public int SilenceTimeoutSeconds { get; set; } = 10;
}

