using NAudio.CoreAudioApi;

namespace PaceApp.Audio.Services;

public sealed class AudioDeviceWatcher : IDisposable
{
    private readonly MMDeviceEnumerator deviceEnumerator = new();
    private readonly TimeSpan pollInterval;
    private readonly object syncRoot = new();

    private Timer? timer;
    private string? currentDeviceId;
    private bool disposed;

    public AudioDeviceWatcher(TimeSpan? pollInterval = null)
    {
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public event EventHandler<string>? DefaultCaptureDeviceChanged;

    public void Start()
    {
        ThrowIfDisposed();

        lock (syncRoot)
        {
            currentDeviceId = GetDefaultCaptureDeviceId();
            timer ??= new Timer(CheckForChanges, null, this.pollInterval, this.pollInterval);
            timer.Change(this.pollInterval, this.pollInterval);
        }
    }

    public void Stop()
    {
        lock (syncRoot)
        {
            timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lock (syncRoot)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
            deviceEnumerator.Dispose();
        }
    }

    private void CheckForChanges(object? state)
    {
        var nextDeviceId = GetDefaultCaptureDeviceId();
        if (string.IsNullOrWhiteSpace(nextDeviceId))
        {
            return;
        }

        lock (syncRoot)
        {
            if (string.Equals(currentDeviceId, nextDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            currentDeviceId = nextDeviceId;
        }

        DefaultCaptureDeviceChanged?.Invoke(this, nextDeviceId);
    }

    private string? GetDefaultCaptureDeviceId()
    {
        try
        {
            using var device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return device.ID;
        }
        catch
        {
            try
            {
                using var fallback = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                return fallback.ID;
            }
            catch
            {
                return null;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}