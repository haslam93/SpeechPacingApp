using System.Globalization;

namespace PaceApp.Analytics.Services;

public interface ITranscriptMetricsSource
{
    bool TryStartLive(CultureInfo? preferredCulture = null);

    void Stop();

    void FeedAudio(float[] monoSamples, int sampleRate);

    TranscriptMetricsSnapshot GetMetrics(
        DateTimeOffset now,
        TimeSpan liveWindow,
        TimeSpan rollingWindow,
        int minimumCurrentWords,
        double minimumCurrentSpeechSeconds,
        int minimumRollingWords,
        double minimumRollingSpeechSeconds);
}

public readonly record struct TranscriptMetricsSnapshot(
    bool IsAvailable,
    bool HasStableEstimate,
    double CurrentWordsPerMinute,
    double RollingWordsPerMinute,
    double? LatestConfidence,
    string StatusMessage,
    string LatestRecognizedText = "")
{
    public static TranscriptMetricsSnapshot Unavailable(string statusMessage) => new(false, false, 0, 0, null, statusMessage);
}