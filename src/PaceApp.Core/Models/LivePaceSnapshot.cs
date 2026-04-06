namespace PaceApp.Core.Models;

public sealed class LivePaceSnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public bool IsMonitoring { get; init; }

    public string DeviceName { get; init; } = "Waiting for microphone";

    public double EstimatedWordsPerMinute { get; init; }

    public double RollingAverageWordsPerMinute { get; init; }

    public double TrendWordsPerMinute { get; init; }

    public double SpeechRatio { get; init; }

    public double PauseRatePerMinute { get; init; }

    public double AveragePauseMilliseconds { get; init; }

    public double ClarityScore { get; init; }

    public double? EnglishConfidence { get; init; }

    public double TranscriptWordsPerMinute { get; init; }

    public PaceAlertLevel AlertLevel { get; init; } = PaceAlertLevel.Calm;

    public double SignalLevel { get; init; }

    public string RecognizedText { get; init; } = string.Empty;

    public string StatusMessage { get; init; } = "Ready to monitor your next Teams call.";

    public static LivePaceSnapshot Idle(string statusMessage = "Ready to monitor your next Teams call.") =>
        new()
        {
            IsMonitoring = false,
            StatusMessage = statusMessage,
        };
}