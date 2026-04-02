namespace PaceApp.Core.Models;

public sealed class CaptureStatusUpdate
{
    public bool IsRunning { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? DeviceName { get; init; }
}