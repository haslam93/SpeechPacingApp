using PaceApp.Core.Models;

namespace PaceApp.Core.Abstractions;

public interface IMicrophoneCaptureService : IDisposable
{
    event EventHandler<AudioFrame>? AudioFrameCaptured;

    event EventHandler<CaptureStatusUpdate>? StatusChanged;

    bool IsRunning { get; }

    string? CurrentDeviceName { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}