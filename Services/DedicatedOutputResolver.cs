using AudioStreaming.Backend.Models;

namespace AudioStreaming.Backend.Services;

public sealed class AudioOutputUnavailableException(string message) : Exception(message);

public static class DedicatedOutputResolver
{
    public static AudioDevice Resolve(string key, IReadOnlyDictionary<string, string> configured,
        IReadOnlyList<AudioDevice> activeOutputs)
    {
        if (!configured.TryGetValue(key, out var name) || string.IsNullOrWhiteSpace(name))
            throw new AudioOutputUnavailableException(
                $"Configure AudioCapture:DedicatedOutputs:{key} with the dedicated playback device's exact name on this VM.");
        var matches = activeOutputs.Where(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            throw new AudioOutputUnavailableException($"Dedicated output '{key}' is unavailable. No default output will be captured.");
        if (matches.Length > 1)
            throw new AudioOutputUnavailableException($"Dedicated output '{key}' is ambiguous. Give its Windows playback device a unique name.");
        return matches[0];
    }
}
