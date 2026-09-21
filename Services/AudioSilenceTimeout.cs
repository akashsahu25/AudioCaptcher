using NAudio.Wave;

namespace AudioStreaming.Backend.Services;

// Shared by the capture callback and watchdog; no disk or encoding work here.
public sealed class AudioSilenceTimeout
{
    private readonly object _sync = new();
    private readonly AudioSilenceDetector _detector;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private long _lastSound;
    private bool _expired;

    public AudioSilenceTimeout(WaveFormat format, double threshold, TimeSpan timeout,
        TimeProvider? clock = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _detector = new AudioSilenceDetector(format, threshold);
        _timeout = timeout;
        _clock = clock ?? TimeProvider.System;
        _lastSound = _clock.GetTimestamp();
    }

    public void Observe(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            if (_clock.GetElapsedTime(_lastSound) >= _timeout) _expired = true;
            if (!_expired && !_detector.IsSilent(data)) _lastSound = _clock.GetTimestamp();
        }
    }

    public bool IsExpired()
    {
        lock (_sync)
        {
            if (_clock.GetElapsedTime(_lastSound) >= _timeout) _expired = true;
            return _expired;
        }
    }
}
