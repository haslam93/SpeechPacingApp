using PaceApp.Core.Models;

namespace PaceApp.Core.Abstractions;

public interface IPaceMetricsEngine
{
    event EventHandler<LivePaceSnapshot>? SnapshotUpdated;

    LivePaceSnapshot CurrentSnapshot { get; }

    void StartSession(AppSettings settings, string deviceName);

    SessionSummary? StopSession();

    void ProcessAudio(AudioFrame frame);
}