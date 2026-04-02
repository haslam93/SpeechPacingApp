namespace PaceApp.Core.Models;

public sealed class SessionMetricPoint
{
    public DateTimeOffset Timestamp { get; init; }

    public double EstimatedWordsPerMinute { get; init; }

    public PaceAlertLevel AlertLevel { get; init; }

    public double ClarityScore { get; init; }
}