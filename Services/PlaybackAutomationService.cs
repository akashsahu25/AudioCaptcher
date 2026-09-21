using System.Collections.Concurrent;
using System.Net.Http.Json;
using AudioStreaming.Backend.Models;
using Microsoft.Extensions.Options;

namespace AudioStreaming.Backend.Services;

public sealed class PlaybackAutomationOptions
{
    public Dictionary<string, PlaybackTargetOptions> Targets { get; set; } = new();
}

public sealed class PlaybackTargetOptions
{
    public string Kind { get; set; } = "Http";
    public string ExecutablePath { get; set; } = "";
    public string MediaPath { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public int RequestTimeoutSeconds { get; set; } = 5;
}

public sealed record StartPlaybackRequest(string TargetKey, Guid? MediaId = null);
public sealed record PreparedPlayback(int ProcessId);

public interface IPlaybackControl
{
    Task<int> PrepareAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken cancellationToken);
    Task PlayAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken cancellationToken);
    Task StopAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken cancellationToken);
}

// The adapter owns application-specific automation. The browser never supplies commands or URLs.
public sealed class HttpPlaybackControl(IHttpClientFactory clients) : IPlaybackControl
{
    public async Task<int> PrepareAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(target, "prepare", jobId, cancellationToken);
        var prepared = await response.Content.ReadFromJsonAsync<PreparedPlayback>(cancellationToken);
        if (prepared is null || prepared.ProcessId <= 0)
            throw new InvalidOperationException("Playback adapter must return the local audio application's processId.");
        return prepared.ProcessId;
    }

    public async Task PlayAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken cancellationToken)
    { using var response = await SendAsync(target, "play", jobId, cancellationToken); }

    public async Task StopAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken cancellationToken)
    { using var response = await SendAsync(target, "stop", jobId, cancellationToken); }

    private async Task<HttpResponseMessage> SendAsync(PlaybackTargetOptions target, string action,
        Guid jobId, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient("PlaybackControl");
        client.Timeout = TimeSpan.FromSeconds(target.RequestTimeoutSeconds);
        using var request = new HttpRequestMessage(HttpMethod.Post, target.BaseUrl.TrimEnd('/') + "/" + action)
        { Content = JsonContent.Create(new { jobId }) };
        var response = await client.SendAsync(request, cancellationToken);
        try { response.EnsureSuccessStatusCode(); return response; }
        catch { response.Dispose(); throw; }
    }
}

public interface IAutomatedCapture
{
    Task<CaptureStatus> StartCaptureAsync(StartCaptureRequest request);
    Task<CaptureStatus?> StopCaptureAsync(Guid id);
    Task WaitForCompletionAsync(Guid id);
    CaptureStatus? GetStatus(Guid id);
}

public sealed class PlaybackBusyException(string key)
    : Exception($"Playback target '{key}' is already in use or awaits cleanup. Restart only after its source is stopped.");

public sealed class PlaybackAutomationService(IAutomatedCapture capture, IPlaybackControl control,
    IOptions<PlaybackAutomationOptions> options, IHostApplicationLifetime lifetime,
    ILogger<PlaybackAutomationService> logger, MediaInputStore? media = null) : IHostedService
{
    private readonly ConcurrentDictionary<string, Guid> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Task> _monitors = new();

    public async Task<CaptureStatus> StartAsync(string key, CancellationToken cancellationToken, Guid? mediaId = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("targetKey is required.");
        var match = options.Value.Targets.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
        if (match.Value is not { } target) throw new ArgumentException("Unknown playback targetKey.");
        string reservation;
        if (target.Kind.Equals("Vlc", StringComparison.OrdinalIgnoreCase))
        {
            // Snapshot per-request media; never mutate shared target configuration.
            target = new PlaybackTargetOptions { Kind = target.Kind, ExecutablePath = target.ExecutablePath,
                MediaPath = mediaId.HasValue ? (media ?? throw new InvalidOperationException("Media store unavailable.")).Resolve(mediaId.Value) : target.MediaPath,
                RequestTimeoutSeconds = target.RequestTimeoutSeconds };
            VlcPlaybackControl.Validate(target);
            reservation = "vlc:" + match.Key;
        }
        else if (target.Kind.Equals("Http", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri) &&
            uri.IsLoopback && uri.Scheme is "http" or "https" &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment) && target.RequestTimeoutSeconds is >= 1 and <= 30)
            reservation = uri.AbsoluteUri.TrimEnd('/');
        else throw new InvalidOperationException("Configure a Vlc target or a local HTTP adapter with a 1-30 second timeout.");
        if (mediaId.HasValue && !target.Kind.Equals("Vlc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("mediaId is supported only for VLC targets.");

        var jobId = Guid.NewGuid();
        // Aliases for the same adapter must share a reservation.
        if (!_active.TryAdd(reservation, jobId)) throw new PlaybackBusyException(key);
        Guid? sessionId = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        try
        {
            var pid = await control.PrepareAsync(target, jobId, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var status = await capture.StartCaptureAsync(new(ProcessId: pid));
            sessionId = status.SessionId;
            linked.Token.ThrowIfCancellationRequested();
            if (capture.GetStatus(sessionId.Value)?.State != "Running")
                throw new InvalidOperationException("Capture stopped before playback could start.");
            await control.PlayAsync(target, jobId, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (capture.GetStatus(sessionId.Value)?.State != "Running")
                throw new InvalidOperationException("Capture ended while playback was starting.");
            var monitor = FinishAsync(sessionId.Value, target, jobId, reservation);
            _monitors[sessionId.Value] = monitor;
            _ = ForgetWhenFinishedAsync(sessionId.Value, monitor);
            return capture.GetStatus(sessionId.Value)!;
        }
        catch
        {
            // Prepare/Play might have succeeded remotely even when their HTTP response was lost.
            await CleanupAsync(sessionId, target, jobId, reservation);
            throw;
        }
    }

    private async Task ForgetWhenFinishedAsync(Guid id, Task monitor)
    {
        try { await monitor; }
        finally { _monitors.TryRemove(id, out _); }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var running = _monitors.ToArray();
        foreach (var entry in running)
            await capture.StopCaptureAsync(entry.Key).WaitAsync(cancellationToken);
        await Task.WhenAll(running.Select(entry => entry.Value)).WaitAsync(cancellationToken);
    }

    private async Task FinishAsync(Guid sessionId, PlaybackTargetOptions target, Guid jobId, string reservation)
    {
        try { await capture.WaitForCompletionAsync(sessionId); }
        catch (Exception error) { logger.LogError(error, "Capture completion failed for {Session}", sessionId); }
        await CleanupAsync(sessionId, target, jobId, reservation);
    }

    private async Task CleanupAsync(Guid? sessionId, PlaybackTargetOptions target, Guid jobId, string reservation)
    {
        var cleaned = true;
        // Stop only the adapter-owned job; never kill an arbitrary user process.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(target.RequestTimeoutSeconds));
            await control.StopAsync(target, jobId, timeout.Token);
        }
        catch (Exception error)
        {
            cleaned = false;
            logger.LogError(error, "Playback stop failed for job {Job}; target remains reserved", jobId);
        }
        try { if (sessionId.HasValue) await capture.StopCaptureAsync(sessionId.Value); }
        catch (Exception error)
        {
            cleaned = false;
            logger.LogError(error, "Capture cleanup failed for {Session}; target remains reserved", sessionId);
        }
        if (cleaned) _active.TryRemove(reservation, out _);
    }
}
