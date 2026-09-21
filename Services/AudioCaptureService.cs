using System.Diagnostics;
using Microsoft.Extensions.Options;
using AudioStreaming.Backend.Models;
using NAudio.CoreAudioApi;

namespace AudioStreaming.Backend.Services;

public sealed class AudioCaptureService(IWebHostEnvironment environment,
    IAudioChunkProcessor processor, ILogger<AudioCaptureService> logger, IOptions<AudioCaptureOptions> options,
    AudioStreamPublisher publisher) : IHostedService
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, AudioCaptureSession> _sessions = new();
    private bool _shuttingDown;

    public IReadOnlyList<AudioDevice> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device) devices.Add(new(device.ID, device.FriendlyName));
        return devices;
    }

    public IReadOnlyList<CaptureStatus> GetSessions()
    { lock (_sync) return _sessions.Values.Select(s => s.Status).ToArray(); }

    public CaptureStatus? GetStatus(Guid id)
    { lock (_sync) return _sessions.TryGetValue(id, out var session) ? session.Status : null; }

    public IReadOnlyList<AudioProcess> GetProcesses()
    {
        var result = new List<AudioProcess>();
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.SessionId == current.SessionId && !process.HasExited)
                        result.Add(new(process.Id, process.ProcessName));
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        return result.OrderBy(p => p.ProcessName).ThenBy(p => p.ProcessId).ToArray();
    }

    public async Task<CaptureStatus> StartCaptureAsync(int processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            throw new PlatformNotSupportedException("Process loopback requires Windows build 20348 or later.");
        if (processId <= 0 || processId == Environment.ProcessId)
            throw new ArgumentException("Select the audio application's process ID.");
        using var target = Process.GetProcessById(processId);
        using var current = Process.GetCurrentProcess();
        if (target.HasExited || target.SessionId != current.SessionId)
            throw new ArgumentException("Select a running application in the backend's Windows login session.");
        var startedAt = target.StartTime.ToUniversalTime().Ticks;
        var sourceId = $"process:{processId}:{startedAt}";
        AudioCaptureSession session;
        lock (_sync)
        {
            if (_shuttingDown) throw new InvalidOperationException("Application is stopping.");
            if (_sessions.Values.Any(s => !s.Finished.IsCompleted &&
                string.Equals(s.Status.DeviceId, sourceId, StringComparison.OrdinalIgnoreCase)))
                throw new DeviceBusyException(sourceId);
            foreach (var id in _sessions.Where(s => s.Value.Finished.IsCompleted)
                         .Select(s => s.Key).Take(Math.Max(0, _sessions.Count - 99)).ToArray())
                _sessions.Remove(id);
            session = new(sourceId, target.ProcessName, processId, startedAt);
            _sessions.Add(session.Status.SessionId, session);
            session.Start(Path.Combine(environment.ContentRootPath, "Output", "Recordings"), processor, logger, options.Value.StartThresholdDbfs, options.Value.StartConfirmationMs, options.Value.SilenceTimeoutSeconds);
        }
        await session.Ready;
        _ = NotifyFinishedAsync(session);
        return session.Status;
    }

    private async Task NotifyFinishedAsync(AudioCaptureSession session)
    {
        await session.Finished;
        await publisher.EndAsync(session.Status);
    }

    public async Task<CaptureStatus?> StopCaptureAsync(Guid id)
    {
        AudioCaptureSession? session;
        lock (_sync) _sessions.TryGetValue(id, out session);
        if (session is null) return null;
        session.RequestStop();
        await session.Finished;
        return session.Status;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        AudioCaptureSession[] sessions;
        lock (_sync)
        {
            _shuttingDown = true;
            sessions = _sessions.Values.ToArray();
            foreach (var session in sessions) session.RequestStop();
        }
        await Task.WhenAll(sessions.Select(s => s.Finished)).WaitAsync(cancellationToken);
    }
}






