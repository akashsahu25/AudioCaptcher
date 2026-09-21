using System.Diagnostics;
using System.Threading.Channels;
using AudioStreaming.Backend.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioStreaming.Backend.Services;

internal sealed class AudioCaptureSession
{
    private readonly object _sync = new();
    private CaptureStatus _status;
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly int? _processId;
    private readonly long _processStartedAt;
    public AudioCaptureSession(string id, string name, int? processId, long startedAt)
    {
        _processId = processId;
        _processStartedAt = startedAt;
        _status = new(Guid.NewGuid(), id, name, "Starting", null, 0, 0, 0, null, null, ProcessId: processId, CaptureMode: processId.HasValue ? "Process" : "DedicatedOutput");
    }

    public CaptureStatus Status { get { lock (_sync) return _status; } }
    public Task Ready => _ready.Task;
    public Task Finished => _finished.Task;

    public void RequestStop(string reason = "Requested")
    {
        lock (_sync)
        {
            if (!_finished.Task.IsCompleted && !_stop.Task.IsCompleted) _status = _status with { State = "Stopping", StopReason = reason };
            _stop.TrySetResult();
        }
    }

    public void Start(string outputRoot, IAudioChunkProcessor processor, ILogger logger, double thresholdDbfs, int confirmationMs, int silenceTimeoutSeconds)
    {
        // The owner thread blocks on async work so the mutex is released on its acquiring thread.
        new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                using var ownership = new DeviceRecordingLock(Status.DeviceId);
                RunAsync(outputRoot, processor, thresholdDbfs, confirmationMs, silenceTimeoutSeconds).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                failure = error;
                try { logger.LogError(error, "Capture session {Session} failed", Status.SessionId); }
                catch { /* Logging failure must not strand ownership/completion. */ }
            }
            finally
            {
                lock (_sync) _status = _status with
                { State = failure is null ? "Stopped" : "Faulted", Error = failure?.Message };
                if (failure is not null) _ready.TrySetException(failure);
                _finished.TrySetResult();
            }
        }) { IsBackground = true, Name = $"Audio-{Status.SessionId}" }.Start();
    }

    private async Task RunAsync(string root, IAudioChunkProcessor processor, double thresholdDbfs, int confirmationMs, int silenceTimeoutSeconds)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = _processId is null ? enumerator.GetDevice(Status.DeviceId) : null;
        var builder = new WasapiRecorderBuilder().WithFormat(new WaveFormat(48000, 16, 2));
        if (_processId is { } processId)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                throw new PlatformNotSupportedException("Process loopback requires Windows build 20348 or later.");
            using var target = Process.GetProcessById(processId);
            if (target.StartTime.ToUniversalTime().Ticks != _processStartedAt || target.HasExited)
                throw new InvalidOperationException("Selected application exited or its PID was reused.");
            builder.WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree);
        }
        else
        {
            if (device!.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
                throw new InvalidOperationException("Dedicated playback output is not active.");
            builder.WithDevice(device).WithLoopbackCapture();
        }
        using var capture = await builder.BuildAsync();
        var raw = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var completed = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(2)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var format = capture.WaveFormat;
        lock (_sync) _status = _status with { Format = format.ToString() };

        AudioSilenceTimeout? silence = null;
        capture.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
        {
            if (buffer.IsEmpty) return;
            // WASAPI can mark a packet silent even when its memory is not zero-filled.
            var bytes = (flags & AudioClientBufferFlags.Silent) != 0 ? new byte[buffer.Length] : buffer.ToArray();
            silence?.Observe(bytes);
            if (!raw.Writer.TryWrite(bytes)) overflow.TrySetResult();
        };
        capture.RecordingStopped += (_, args) => stopped.TrySetResult(args.Exception);

        var processing = Task.Run(async () =>
        {
            await foreach (var chunk in completed.Reader.ReadAllAsync())
            {
                var savedBytes = await processor.ProcessAsync(chunk);
                lock (_sync) _status = _status with { ChunksProcessed = _status.ChunksProcessed + 1, ChunksSaved = _status.ChunksSaved + 1, BytesWritten = _status.BytesWritten + savedBytes, LastFileName = Path.GetFileName(chunk.FilePath) };
            }
        });

        Task PublishAsync(AudioChunk chunk)
        {

            if (processing.IsFaulted) return Task.CompletedTask; // Stop path drains capture after a processing failure.
            if (!completed.Writer.TryWrite(chunk))
                throw new IOException("Completed-chunk queue is full: processing cannot keep up.");
            return Task.CompletedTask;
        }

        var writing = Task.Run(async () =>
        {
            try
            {
                using var chunker = new PcmChunkWriter(format,
                    Path.Combine(root, Status.SessionId.ToString("N")), Status.SessionId, Status.DeviceId, silenceThreshold: Math.Pow(10, thresholdDbfs / 20), startConfirmationMs: confirmationMs);
                await foreach (var bytes in raw.Reader.ReadAllAsync())
                {
                    await chunker.WriteAsync(bytes, PublishAsync);
                    lock (_sync) _status = _status with { SilentChunksSkipped = chunker.SilentChunksSkipped, LeadingSilentFramesSkipped = chunker.LeadingSilentFramesSkipped };
                }
                await chunker.CompleteAsync(PublishAsync);
                lock (_sync) _status = _status with { SilentChunksSkipped = chunker.SilentChunksSkipped, LeadingSilentFramesSkipped = chunker.LeadingSilentFramesSkipped };
            }
            finally { completed.Writer.TryComplete(); }
        });

        var started = false;
        Timer? silenceWatchdog = null;
        try
        {
            silence = new AudioSilenceTimeout(format, Math.Pow(10, thresholdDbfs / 20),
                TimeSpan.FromSeconds(silenceTimeoutSeconds));
            capture.StartRecording(); // Exactly once for the entire session.
            started = true;
            silenceWatchdog = new Timer(_ =>
            {
                if (silence.IsExpired()) RequestStop("SilenceTimeout");
            }, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
            lock (_sync) if (!_stop.Task.IsCompleted) _status = _status with { State = "Running" };
            _ready.TrySetResult();
            await Task.WhenAny(_stop.Task, stopped.Task, overflow.Task, writing, processing);
        }
        finally
        {
            try
            {
                if (silenceWatchdog is not null) await silenceWatchdog.DisposeAsync();
                if (started)
                {
                    capture.StopRecording();
                    await stopped.Task;
                }
            }
            finally
            {
                raw.Writer.TryComplete();
                await Task.WhenAll(writing, processing);
            }
        }
        if (overflow.Task.IsCompleted)
            throw new IOException("Audio queue overflow: recording stopped; continuity is not guaranteed.");
        if (started && await stopped.Task is { } failure) throw failure;
    }
}








