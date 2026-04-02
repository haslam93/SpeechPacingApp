namespace PaceApp.Core.Models;

public sealed class AudioFrame
{
    public DateTimeOffset Timestamp { get; init; }

    public int SampleRate { get; init; }

    public float[] Samples { get; init; } = [];

    public double SignalLevel { get; init; }

    public string DeviceName { get; init; } = "Unknown microphone";
}