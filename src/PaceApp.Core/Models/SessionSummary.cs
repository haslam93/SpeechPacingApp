namespace PaceApp.Core.Models;

public sealed class SessionSummary
{
    public Guid SessionId { get; init; } = Guid.NewGuid();

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset EndedAt { get; init; }

    public string DeviceName { get; init; } = "Unknown microphone";

    public double AverageWordsPerMinute { get; init; }

    public double PeakWordsPerMinute { get; init; }

    public double AverageClarityScore { get; init; }

    public double AverageSpeechRatio { get; init; }

    public double AveragePauseMilliseconds { get; init; }

    public double PauseRatePerMinute { get; init; }

    public double CautionSeconds { get; init; }

    public double CriticalSeconds { get; init; }

    public List<SessionMetricPoint> TrendPoints { get; init; } = [];
}