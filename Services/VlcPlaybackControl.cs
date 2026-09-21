using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioStreaming.Backend.Services;

public sealed class PlaybackControlRouter(HttpPlaybackControl http, VlcPlaybackControl vlc) : IPlaybackControl
{
    private IPlaybackControl For(PlaybackTargetOptions target) =>
        target.Kind.Equals("Vlc", StringComparison.OrdinalIgnoreCase) ? vlc : http;
    public Task<int> PrepareAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken token) => For(target).PrepareAsync(target, jobId, token);
    public Task PlayAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken token) => For(target).PlayAsync(target, jobId, token);
    public Task StopAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken token) => For(target).StopAsync(target, jobId, token);
}

// Each job owns the Process handle it launched. Never discover or kill VLC by process name.
public sealed class VlcPlaybackControl : IPlaybackControl
{
    private sealed record Job(Process Process, HttpClient Client, string MediaUri);
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();

    public static string ResolveExecutable(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
                throw new InvalidOperationException("VLC ExecutablePath must name an existing absolute executable path.");
            return configured;
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var candidate = Path.Combine(Environment.GetFolderPath(folder), "VideoLAN", "VLC", "vlc.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("Install VLC on D2 or configure its ExecutablePath.");
    }

    public static void Validate(PlaybackTargetOptions target)
    {
        _ = ResolveExecutable(target.ExecutablePath);
        if (string.IsNullOrWhiteSpace(target.MediaPath) || !Path.IsPathFullyQualified(target.MediaPath) || !File.Exists(target.MediaPath))
            throw new InvalidOperationException("Configure VLC MediaPath to an existing absolute audio-file path on D2.");
        if (target.RequestTimeoutSeconds is < 1 or > 30)
            throw new InvalidOperationException("VLC RequestTimeoutSeconds must be between 1 and 30.");
    }

    public async Task<int> PrepareAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken token)
    {
        Validate(target);
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(target.RequestTimeoutSeconds));
        // VLC cannot inherit this listener. If the port is claimed in the gap, authenticated readiness fails closed.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var info = new ProcessStartInfo(ResolveExecutable(target.ExecutablePath))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--ignore-config", "--no-one-instance", "--no-one-instance-when-started-from-file",
            "--intf=dummy", "--extraintf=http", "--http-host=127.0.0.1", $"--http-port={port}",
            $"--http-password={secret}", "--no-video", "--no-media-library", "--no-loop", "--no-repeat",
            "--no-random", "--no-metadata-network-access" }) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new InvalidOperationException("VLC launch failed.");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + secret)));
        var job = new Job(process, client, new Uri(Path.GetFullPath(target.MediaPath)).AbsoluteUri);
        if (!_jobs.TryAdd(jobId, job))
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            finally { client.Dispose(); process.Dispose(); }
            throw new InvalidOperationException("VLC job already exists.");
        }
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("VLC exited before its control interface became ready.");
                try
                {
                    using var status = await StatusAsync(job, "", deadline.Token);
                    if (status.RootElement.GetProperty("state").GetString() != "stopped")
                        throw new InvalidOperationException("VLC must be idle before capture starts.");
                    return process.Id;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch { await StopAsync(target, jobId, CancellationToken.None); throw; }
    }

    public async Task PlayAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken token)
    {
        if (!_jobs.TryGetValue(jobId, out var job) || job.Process.HasExited)
            throw new InvalidOperationException("Prepared VLC instance is no longer running.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(target.RequestTimeoutSeconds));
        using (var status = await StatusAsync(job, "?command=in_play&input=" + Uri.EscapeDataString(job.MediaUri), deadline.Token))
            if (status.RootElement.GetProperty("state").GetString() == "playing") return;
        // VLC decodes asynchronously. Confirm playback started, without waiting for the whole media file.
        while (true)
        {
            await Task.Delay(50, deadline.Token);
            if (job.Process.HasExited) throw new InvalidOperationException("VLC exited before playback started.");
            using var status = await StatusAsync(job, "", deadline.Token);
            if (status.RootElement.GetProperty("state").GetString() == "playing") return;
        }
    }

    private static async Task<JsonDocument> StatusAsync(Job job, string query, CancellationToken token)
    {
        using var response = await job.Client.GetAsync("requests/status.json" + query, token);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
    }

    public async Task StopAsync(PlaybackTargetOptions target, Guid jobId, CancellationToken token)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        try
        {
            if (!job.Process.HasExited)
            {
                try { using var status = await StatusAsync(job, "?command=pl_stop", token); }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException) { }
                // This is an instance created exclusively for this job, not the user's existing player.
                if (!job.Process.HasExited) job.Process.Kill(entireProcessTree: true);
                await job.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            _jobs.TryRemove(jobId, out _);
            job.Client.Dispose();
            job.Process.Dispose();
        }
        catch { throw; } // Keep ownership available for retry if termination failed.
    }
}
