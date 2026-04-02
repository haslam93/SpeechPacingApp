using PaceApp.Core.Abstractions;
using PaceApp.Core.Models;

namespace PaceApp.App.Services;

public sealed class PaceMonitorController : IDisposable
{
    private readonly IMicrophoneCaptureService microphoneCaptureService;
    private readonly IPaceMetricsEngine paceMetricsEngine;
    private readonly IAppStateRepository appStateRepository;

    public PaceMonitorController(
        IMicrophoneCaptureService microphoneCaptureService,
        IPaceMetricsEngine paceMetricsEngine,
        IAppStateRepository appStateRepository)
    {
        this.microphoneCaptureService = microphoneCaptureService;
        this.paceMetricsEngine = paceMetricsEngine;
        this.appStateRepository = appStateRepository;

        this.microphoneCaptureService.AudioFrameCaptured += OnAudioFrameCaptured;
        this.microphoneCaptureService.StatusChanged += OnCaptureStatusChanged;
        this.paceMetricsEngine.SnapshotUpdated += OnSnapshotUpdated;
    }

    public event EventHandler<LivePaceSnapshot>? SnapshotUpdated;

    public event EventHandler<CaptureStatusUpdate>? StatusChanged;

    public event EventHandler<IReadOnlyList<SessionSummary>>? SessionsUpdated;

    public AppSettings Settings { get; private set; } = new();

    public IReadOnlyList<SessionSummary> RecentSessions { get; private set; } = [];

    public LivePaceSnapshot CurrentSnapshot { get; private set; } = LivePaceSnapshot.Idle();

    public bool IsMonitoring => microphoneCaptureService.IsRunning;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Settings = await appStateRepository.LoadSettingsAsync(cancellationToken);
        RecentSessions = (await appStateRepository.LoadSessionsAsync(cancellationToken)).Take(12).ToList();
        CurrentSnapshot = LivePaceSnapshot.Idle();
    }

    public async Task StartMonitoringAsync(CancellationToken cancellationToken = default)
    {
        if (microphoneCaptureService.IsRunning)
        {
            return;
        }

        await microphoneCaptureService.StartAsync(cancellationToken);
        if (!microphoneCaptureService.IsRunning)
        {
            return;
        }

        paceMetricsEngine.StartSession(Settings, microphoneCaptureService.CurrentDeviceName ?? "Communications microphone");
    }

    public async Task StopMonitoringAsync(CancellationToken cancellationToken = default)
    {
        if (microphoneCaptureService.IsRunning)
        {
            await microphoneCaptureService.StopAsync(cancellationToken);
        }

        var summary = paceMetricsEngine.StopSession();
        CurrentSnapshot = paceMetricsEngine.CurrentSnapshot;
        SnapshotUpdated?.Invoke(this, CurrentSnapshot);

        if (summary is null)
        {
            return;
        }

        await appStateRepository.SaveSessionAsync(summary, cancellationToken);
        RecentSessions = (await appStateRepository.LoadSessionsAsync(cancellationToken)).Take(12).ToList();
        SessionsUpdated?.Invoke(this, RecentSessions);
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings.Clone();
        return appStateRepository.SaveSettingsAsync(Settings, cancellationToken);
    }

    public void Dispose()
    {
        microphoneCaptureService.AudioFrameCaptured -= OnAudioFrameCaptured;
        microphoneCaptureService.StatusChanged -= OnCaptureStatusChanged;
        paceMetricsEngine.SnapshotUpdated -= OnSnapshotUpdated;
        microphoneCaptureService.Dispose();
    }

    private void OnAudioFrameCaptured(object? sender, AudioFrame frame)
    {
        paceMetricsEngine.ProcessAudio(frame);
    }

    private void OnCaptureStatusChanged(object? sender, CaptureStatusUpdate update)
    {
        StatusChanged?.Invoke(this, update);
    }

    private void OnSnapshotUpdated(object? sender, LivePaceSnapshot snapshot)
    {
        CurrentSnapshot = snapshot;
        SnapshotUpdated?.Invoke(this, snapshot);
    }
}