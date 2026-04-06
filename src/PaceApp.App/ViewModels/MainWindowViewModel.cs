using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using PaceApp.App.Services;
using PaceApp.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PaceApp.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly Brush CalmBackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(9, 34, 44)));
    private static readonly Brush CalmBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(45, 212, 191)));
    private static readonly Brush CalmBadgeBrush = Freeze(new SolidColorBrush(Color.FromRgb(94, 234, 212)));
    private static readonly Brush CautionBackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(54, 35, 10)));
    private static readonly Brush CautionBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(251, 191, 36)));
    private static readonly Brush CautionBadgeBrush = Freeze(new SolidColorBrush(Color.FromRgb(253, 224, 71)));
    private static readonly Brush CriticalBackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(62, 18, 24)));
    private static readonly Brush CriticalBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(248, 113, 113)));
    private static readonly Brush CriticalBadgeBrush = Freeze(new SolidColorBrush(Color.FromRgb(252, 165, 165)));

    private readonly PaceMonitorController controller;
    private readonly AppDiagnosticsService diagnosticsService;
    private readonly StartupRegistrationService startupRegistrationService;
    private readonly AccuracyLogService accuracyLogService = new();
    private readonly AsyncRelayCommand startMonitoringCommand;
    private readonly AsyncRelayCommand stopMonitoringCommand;

    private bool alwaysOnTop;
    private bool canCloseWindow;
    private double clarityScore;
    private double currentWpm;
    private string deviceName = "Waiting for microphone";
    private string alertHeadline = "Ready";
    private Brush alertBackgroundBrush = CalmBackgroundBrush;
    private Brush alertBorderBrush = CalmBorderBrush;
    private Brush alertBadgeBrush = CalmBadgeBrush;
    private bool isBusy;
    private bool isMonitoring;
    private string paceNarrative = "Start monitoring before your next Teams call.";
    private double pauseRatePerMinute;
    private double averagePauseMilliseconds;
    private double rollingWpm;
    private double speechRatioPercent;
    private bool startWithWindows;
    private string statusMessage = "Ready to monitor your next Teams call.";
    private double trendWpm;
    private double transcriptWpm;
    private bool suppressSettingSave;
    private string launchModeLabel = "Visible startup";
    private bool isRecordingAccuracy;
    private DateTimeOffset monitoringStartTime;
    private string monitoringButtonLabel = "▶  Start monitoring";

    public MainWindowViewModel(
        PaceMonitorController controller,
        StartupRegistrationService startupRegistrationService,
        AppDiagnosticsService diagnosticsService)
    {
        this.controller = controller;
        this.startupRegistrationService = startupRegistrationService;
        this.diagnosticsService = diagnosticsService;

        startMonitoringCommand = new AsyncRelayCommand(StartMonitoringAsync, () => !IsMonitoring && !IsBusy);
        stopMonitoringCommand = new AsyncRelayCommand(StopMonitoringAsync, () => IsMonitoring && !IsBusy);
        HideCommand = new RelayCommand(() => HideRequested?.Invoke(this, EventArgs.Empty));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        OpenDiagnosticsCommand = new RelayCommand(OpenDiagnostics);
        OpenAccuracyLogCommand = new RelayCommand(OpenAccuracyLogFolder);

        controller.SnapshotUpdated += OnSnapshotUpdated;
        controller.StatusChanged += OnStatusChanged;
        controller.SessionsUpdated += OnSessionsUpdated;

        RecentSessions = [];
        DiagnosticMessages = [];
        LoadInitialState();
    }

    public event EventHandler? HideRequested;

    public event EventHandler? ExitRequested;

    public ObservableCollection<SessionSummaryItemViewModel> RecentSessions { get; }

    public ObservableCollection<string> DiagnosticMessages { get; }

    public AsyncRelayCommand StartMonitoringCommand => startMonitoringCommand;

    public AsyncRelayCommand StopMonitoringCommand => stopMonitoringCommand;

    public RelayCommand HideCommand { get; }

    public RelayCommand ExitCommand { get; }

    public RelayCommand CopyDiagnosticsCommand { get; }

    public RelayCommand OpenDiagnosticsCommand { get; }

    public RelayCommand OpenAccuracyLogCommand { get; }

    public bool CanCloseWindow => canCloseWindow;

    public bool IsMonitoring
    {
        get => isMonitoring;
        private set
        {
            if (SetProperty(ref isMonitoring, value))
            {
                NotifyCommandState();
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                NotifyCommandState();
            }
        }
    }

    public bool AlwaysOnTop
    {
        get => alwaysOnTop;
        set
        {
            if (SetProperty(ref alwaysOnTop, value) && !suppressSettingSave)
            {
                _ = PersistSettingsAsync();
            }
        }
    }

    public bool StartWithWindows
    {
        get => startWithWindows;
        set
        {
            if (SetProperty(ref startWithWindows, value) && !suppressSettingSave)
            {
                try
                {
                    startupRegistrationService.SetEnabled(value);
                    _ = PersistSettingsAsync();
                }
                catch (Exception exception)
                {
                    StatusMessage = $"Could not update startup registration. {exception.Message}";
                }
            }
        }
    }

    public string DeviceName
    {
        get => deviceName;
        private set => SetProperty(ref deviceName, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string AlertHeadline
    {
        get => alertHeadline;
        private set => SetProperty(ref alertHeadline, value);
    }

    public double CurrentWpm
    {
        get => currentWpm;
        private set => SetProperty(ref currentWpm, value);
    }

    public double RollingWpm
    {
        get => rollingWpm;
        private set => SetProperty(ref rollingWpm, value);
    }

    public double TrendWpm
    {
        get => trendWpm;
        private set
        {
            if (SetProperty(ref trendWpm, value))
            {
                OnPropertyChanged(nameof(TrendLabel));
            }
        }
    }

    public double PauseRatePerMinute
    {
        get => pauseRatePerMinute;
        private set => SetProperty(ref pauseRatePerMinute, value);
    }

    public double AveragePauseMilliseconds
    {
        get => averagePauseMilliseconds;
        private set => SetProperty(ref averagePauseMilliseconds, value);
    }

    public double ClarityScore
    {
        get => clarityScore;
        private set => SetProperty(ref clarityScore, value);
    }

    public double SpeechRatioPercent
    {
        get => speechRatioPercent;
        private set => SetProperty(ref speechRatioPercent, value);
    }

    public Brush AlertBackgroundBrush
    {
        get => alertBackgroundBrush;
        private set => SetProperty(ref alertBackgroundBrush, value);
    }

    public Brush AlertBorderBrush
    {
        get => alertBorderBrush;
        private set => SetProperty(ref alertBorderBrush, value);
    }

    public Brush AlertBadgeBrush
    {
        get => alertBadgeBrush;
        private set => SetProperty(ref alertBadgeBrush, value);
    }

    public string PaceNarrative
    {
        get => paceNarrative;
        private set => SetProperty(ref paceNarrative, value);
    }

    public string TrendLabel => TrendWpm switch
    {
        > 3 => $"⬆ Rising +{TrendWpm:N0}",
        < -3 => $"⬇ Settling {TrendWpm:N0}",
        _ => "― Steady",
    };

    public string ThresholdSummary =>
        $"Target around {controller.Settings.TargetWordsPerMinute:N0} WPM. Caution from {controller.Settings.CautionWordsPerMinute:N0}. Red from {controller.Settings.CriticalWordsPerMinute:N0}.";

    public string SessionCountLabel => RecentSessions.Count == 0 ? "No saved sessions yet" : $"{RecentSessions.Count} recent";

    public bool IsRecordingAccuracy
    {
        get => isRecordingAccuracy;
        set
        {
            if (SetProperty(ref isRecordingAccuracy, value))
            {
                if (value)
                {
                    accuracyLogService.Start();
                    AppendDiagnostic($"Accuracy log started: {accuracyLogService.CurrentLogPath}");
                }
                else
                {
                    accuracyLogService.Stop();
                    AppendDiagnostic("Accuracy log stopped.");
                }
            }
        }
    }

    public double TranscriptWpm
    {
        get => transcriptWpm;
        private set => SetProperty(ref transcriptWpm, value);
    }

    public string MonitoringButtonLabel
    {
        get => monitoringButtonLabel;
        private set => SetProperty(ref monitoringButtonLabel, value);
    }

    public string LaunchModeLabel
    {
        get => launchModeLabel;
        private set => SetProperty(ref launchModeLabel, value);
    }

    public string WindowTitle => IsMonitoring ? "Pace Coach • live" : "Pace Coach";

    public async Task ToggleMonitoringAsync()
    {
        if (IsMonitoring)
        {
            await StopMonitoringAsync();
            return;
        }

        await StartMonitoringAsync();
    }

    public void AllowCloseWindow()
    {
        canCloseWindow = true;
    }

    public void SetLaunchMode(string launchMode)
    {
        LaunchModeLabel = launchMode;
    }

    public void AppendDiagnostic(string message)
    {
        diagnosticsService.Write(message);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            DiagnosticMessages.Add(message);
            while (DiagnosticMessages.Count > 25)
            {
                DiagnosticMessages.RemoveAt(0);
            }
        });
    }

    public void Dispose()
    {
        controller.SnapshotUpdated -= OnSnapshotUpdated;
        controller.StatusChanged -= OnStatusChanged;
        controller.SessionsUpdated -= OnSessionsUpdated;
        accuracyLogService.Stop();
    }

    private void LoadInitialState()
    {
        suppressSettingSave = true;
        AlwaysOnTop = controller.Settings.AlwaysOnTop;
        var shouldStartWithWindows = startupRegistrationService.IsEnabled() || controller.Settings.StartWithWindows;
        try
        {
            startupRegistrationService.SetEnabled(shouldStartWithWindows);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Could not update startup registration. {exception.Message}";
        }

        StartWithWindows = shouldStartWithWindows;
        suppressSettingSave = false;

        UpdateRecentSessions(controller.RecentSessions);
        ApplySnapshot(controller.CurrentSnapshot);
        StatusMessage = controller.CurrentSnapshot.StatusMessage;
        foreach (var entry in diagnosticsService.GetRecentEntries())
        {
            DiagnosticMessages.Add(entry);
        }
    }

    private async Task StartMonitoringAsync()
    {
        IsBusy = true;
        StatusMessage = "Starting microphone capture...";

        try
        {
            monitoringStartTime = DateTimeOffset.UtcNow;
            await controller.StartMonitoringAsync();
            IsMonitoring = controller.IsMonitoring;
            MonitoringButtonLabel = controller.IsMonitoring ? "🎙  Listening..." : "▶  Start monitoring";
            AppendDiagnostic(controller.IsMonitoring
                ? "Monitoring started."
                : "Monitoring did not start. Check microphone status above.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopMonitoringAsync()
    {
        IsBusy = true;
        StatusMessage = "Saving your session summary...";

        try
        {
            await controller.StopMonitoringAsync();
            IsMonitoring = controller.IsMonitoring;
            MonitoringButtonLabel = "▶  Start monitoring";
            AppendDiagnostic("Monitoring stopped.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PersistSettingsAsync()
    {
        var updated = controller.Settings.Clone();
        updated.AlwaysOnTop = AlwaysOnTop;
        updated.StartWithWindows = StartWithWindows;

        await controller.SaveSettingsAsync(updated);
        OnPropertyChanged(nameof(ThresholdSummary));
    }

    private void OnSnapshotUpdated(object? sender, LivePaceSnapshot snapshot)
    {
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ApplySnapshot(snapshot);
            StatusMessage = snapshot.StatusMessage;
            DeviceName = snapshot.DeviceName;
            IsMonitoring = snapshot.IsMonitoring || controller.IsMonitoring;
        });
    }

    private void OnStatusChanged(object? sender, CaptureStatusUpdate update)
    {
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusMessage = update.Message;
            if (!string.IsNullOrWhiteSpace(update.DeviceName))
            {
                DeviceName = update.DeviceName;
            }

            IsMonitoring = update.IsRunning || controller.IsMonitoring;
            AppendDiagnostic(update.Message);
        });
    }

    private void OnSessionsUpdated(object? sender, IReadOnlyList<SessionSummary> sessions)
    {
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => UpdateRecentSessions(sessions));
    }

    private void ApplySnapshot(LivePaceSnapshot snapshot)
    {
        CurrentWpm = snapshot.EstimatedWordsPerMinute;
        RollingWpm = snapshot.RollingAverageWordsPerMinute;
        TranscriptWpm = snapshot.TranscriptWordsPerMinute;
        TrendWpm = snapshot.TrendWordsPerMinute;
        PauseRatePerMinute = snapshot.PauseRatePerMinute;
        AveragePauseMilliseconds = snapshot.AveragePauseMilliseconds;
        ClarityScore = snapshot.ClarityScore;
        SpeechRatioPercent = snapshot.SpeechRatio * 100;
        PaceNarrative = BuildNarrative(snapshot);
        ApplyAlertPalette(snapshot.AlertLevel);

        if (accuracyLogService.IsRecording)
        {
            accuracyLogService.RecordSnapshot(snapshot, monitoringStartTime, snapshot.TranscriptWordsPerMinute);
        }
    }

    private void ApplyAlertPalette(PaceAlertLevel alertLevel)
    {
        switch (alertLevel)
        {
            case PaceAlertLevel.Critical:
                AlertHeadline = "🔴 Too fast";
                AlertBackgroundBrush = CriticalBackgroundBrush;
                AlertBorderBrush = CriticalBorderBrush;
                AlertBadgeBrush = CriticalBadgeBrush;
                break;

            case PaceAlertLevel.Caution:
                AlertHeadline = "🟡 Ease back";
                AlertBackgroundBrush = CautionBackgroundBrush;
                AlertBorderBrush = CautionBorderBrush;
                AlertBadgeBrush = CautionBadgeBrush;
                break;

            default:
                AlertHeadline = "🟢 Steady";
                AlertBackgroundBrush = CalmBackgroundBrush;
                AlertBorderBrush = CalmBorderBrush;
                AlertBadgeBrush = CalmBadgeBrush;
                break;
        }
    }

    private string BuildNarrative(LivePaceSnapshot snapshot)
    {
        if (!snapshot.IsMonitoring)
        {
            return "Keep this window docked beside Teams and start monitoring right before the call.";
        }

        if (snapshot.AlertLevel == PaceAlertLevel.Critical)
        {
            return "Finish the current point, then leave a deliberate pause before you continue.";
        }

        if (snapshot.AlertLevel == PaceAlertLevel.Caution)
        {
            return "You are edging upward. Slow the next sentence down and separate ideas more clearly.";
        }

        return snapshot.AveragePauseMilliseconds > 0 && snapshot.AveragePauseMilliseconds < controller.Settings.MinimumPauseMilliseconds
            ? "Your speed is fine. Add slightly longer pauses to keep the message landing cleanly."
            : "Your pace is holding. Keep this rhythm while you explain the next step.";
    }

    private void UpdateRecentSessions(IReadOnlyList<SessionSummary> sessions)
    {
        RecentSessions.Clear();
        foreach (var session in sessions)
        {
            RecentSessions.Add(new SessionSummaryItemViewModel(session));
        }

        OnPropertyChanged(nameof(SessionCountLabel));
    }

    private void NotifyCommandState()
    {
        startMonitoringCommand.NotifyCanExecuteChanged();
        stopMonitoringCommand.NotifyCanExecuteChanged();
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void CopyDiagnostics()
    {
        var text = string.Join(Environment.NewLine, DiagnosticMessages);
        System.Windows.Clipboard.SetText(text);
        AppendDiagnostic("Copied diagnostics to the clipboard.");
    }

    private void OpenDiagnostics()
    {
        diagnosticsService.OpenLogFolder();
        AppendDiagnostic("Opened the diagnostics log folder.");
    }

    private void OpenAccuracyLogFolder()
    {
        var logPath = accuracyLogService.GetLatestLogPath();
        if (logPath is not null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetDirectoryName(logPath)!,
                UseShellExecute = true
            });
            AppendDiagnostic("Opened accuracy log folder.");
        }
        else
        {
            AppendDiagnostic("No accuracy log recorded yet. Enable the toggle and speak first.");
        }
    }
}