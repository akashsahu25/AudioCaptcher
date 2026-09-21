using AudioStreaming.Backend.Models;
using NAudio.Wave;


namespace AudioStreaming.Backend.Services;

// Single consumer. Each chunk begins on the first non-silent sample frame.
public sealed class PcmChunkWriter : IDisposable
{
    private readonly WaveFormat _format;
    private readonly string _folder;
    private readonly Guid _sessionId;
    private readonly string _deviceId;
    private readonly long _targetBytes;
    private readonly AudioSilenceDetector _detector;

    private readonly byte[] _onsetWindow;
    private readonly int _confirmationWindows;
    private readonly MemoryStream _candidate = new();
    private int _windowBytes;
    private int _activeWindows;
    private MemoryStream? _writer;
    private string? _path;
    private long _written;
    private long _startFrame;
    private int _sequence;
    public long BytesWritten { get; private set; }
    public long LeadingSilentFramesSkipped { get; private set; }
    public int SilentChunksSkipped => (int)(LeadingSilentFramesSkipped / (_targetBytes / _format.BlockAlign));

    public PcmChunkWriter(WaveFormat format, string folder, Guid sessionId,
        string deviceId, int seconds = 20, double silenceThreshold = 0.001, int startConfirmationMs = 0)
    {
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!double.IsFinite(silenceThreshold) || silenceThreshold < 0 || silenceThreshold >= 1)
            throw new ArgumentOutOfRangeException(nameof(silenceThreshold));

        if (startConfirmationMs < 0 || startConfirmationMs > 1000)
            throw new ArgumentOutOfRangeException(nameof(startConfirmationMs));
        _confirmationWindows = (startConfirmationMs + 9) / 10;
        _onsetWindow = new byte[Math.Max(1, format.SampleRate / 100) * format.BlockAlign];
        _format = format;
        _folder = folder;
        _sessionId = sessionId;
        _deviceId = deviceId;
        _targetBytes = checked((long)format.SampleRate * format.BlockAlign * seconds);
        _detector = new AudioSilenceDetector(format, silenceThreshold);
        Directory.CreateDirectory(folder);
    }

    public async Task WriteAsync(byte[] data, Func<AudioChunk, Task> completed)
    {
        if (data.Length % _format.BlockAlign != 0)
            throw new InvalidDataException("Audio buffer must contain complete sample frames.");
        var offset = 0;
        while (offset < data.Length)
        {
            if (_writer is null)
            {
                if (_confirmationWindows > 0)
                {
                    var take = Math.Min(data.Length - offset, _onsetWindow.Length - _windowBytes);
                    data.AsSpan(offset, take).CopyTo(_onsetWindow.AsSpan(_windowBytes));
                    offset += take;
                    _windowBytes += take;
                    if (_windowBytes < _onsetWindow.Length) continue;
                    _windowBytes = 0;
                    var firstSound = 0;
                    while (firstSound < _onsetWindow.Length &&
                           _detector.IsSilent(_onsetWindow.AsSpan(firstSound, _format.BlockAlign)))
                        firstSound += _format.BlockAlign;
                    if (firstSound == _onsetWindow.Length)
                    {
                        SkipWaitingBytes(_candidate.Length + _onsetWindow.Length);
                        _candidate.SetLength(0);
                        _activeWindows = 0;
                        continue;
                    }
                    // Preserve the original onset while waiting for sustained activity.
                    if (_activeWindows == 0) SkipWaitingBytes(firstSound);
                    else firstSound = 0;
                    _candidate.Write(_onsetWindow, firstSound, _onsetWindow.Length - firstSound);
                    if (++_activeWindows < _confirmationWindows) continue;
                    OpenWriter();
                    var confirmed = _candidate.ToArray();
                    _candidate.SetLength(0);
                    _activeWindows = 0;
                    await WriteAsync(confirmed, completed);
                    continue;
                }
                // Inspect whole frames (all channels). Never cut a sample or channel pair.
                var begin = offset;
                while (offset < data.Length &&
                       _detector.IsSilent(data.AsSpan(offset, _format.BlockAlign)))
                    offset += _format.BlockAlign;
                var skippedFrames = (offset - begin) / _format.BlockAlign;
                LeadingSilentFramesSkipped += skippedFrames;
                _startFrame += skippedFrames;
                if (offset == data.Length) break;

                OpenWriter();
            }
            var count = (int)Math.Min(data.Length - offset, _targetBytes - _written);
            _writer.Write(data, offset, count);
            BytesWritten += count;
            offset += count;
            _written += count;
            if (_written == _targetBytes) await completed(FinalizeChunk(false));
        }
    }

    public async Task CompleteAsync(Func<AudioChunk, Task> completed)
    {
        if (_written > 0) await completed(FinalizeChunk(true));
        SkipWaitingBytes(_candidate.Length + _windowBytes);
        _candidate.SetLength(0);
        _windowBytes = 0;
        _activeWindows = 0;
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_writer))]
    private void OpenWriter()
    {
        _path = Path.Combine(_folder, $"chunk-{_sequence + 1:D6}.webm");
        _writer = new MemoryStream();
    }

    private void SkipWaitingBytes(long count)
    {
        var frames = count / _format.BlockAlign;
        LeadingSilentFramesSkipped += frames;
        _startFrame += frames;
    }

    private AudioChunk FinalizeChunk(bool partial)
    {
        var pcm = _writer!.ToArray();
        _writer.Dispose();
        _writer = null;
        var frames = _written / _format.BlockAlign;
        var chunk = new AudioChunk(_sessionId, _deviceId, ++_sequence, _path!,
            _startFrame, frames, _format.SampleRate, partial, pcm, _format);
        _startFrame += frames;
        _written = 0;
        return chunk;
    }

    public void Dispose() { _writer?.Dispose(); _candidate.Dispose(); }
}


