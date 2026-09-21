using System.Security.Cryptography;
using System.Text;

namespace AudioStreaming.Backend.Services;

public sealed class DeviceBusyException(string deviceId)
    : Exception($"Audio source '{deviceId}' already has an active recording session.");

// Acquire and dispose on the SAME thread (Windows mutex ownership is thread-affine).
public sealed class DeviceRecordingLock : IDisposable
{
    private readonly Mutex _mutex;

    public DeviceRecordingLock(string deviceId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(deviceId.ToUpperInvariant())));
        _mutex = new Mutex(false, @"Global\AudioStreaming.Capture." + hash);
        try
        {
            try
            {
                if (!_mutex.WaitOne(0)) throw new DeviceBusyException(deviceId);
            }
            catch (AbandonedMutexException) { /* Previous owner died; ownership is now ours. */ }
        }
        catch { _mutex.Dispose(); throw; }
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
