using PaceApp.Core.Abstractions;
using PaceApp.Core.Models;

namespace PaceApp.Analytics.Services;

public sealed class SignalPaceMetricsEngine : IPaceMetricsEngine
{
    private readonly object syncRoot = new();
    private readonly Queue<DateTimeOffset> syllablePeaks = new();
    private readonly Queue<(DateTimeOffset Timestamp, double DurationMs)> pauses = new();
    private readonly Queue<(DateTimeOffset Timestamp, double DurationSeconds, bool IsSpeech, double SignalLevel)> chunks = new();
    private readonly Queue<(DateTimeOffset Timestamp, double Wpm)> wpmHistory = new();
    private readonly List<SessionMetricPoint> trendPoints = [];

    private AppSettings settings = new();
    private LivePaceSnapshot currentSnapshot = LivePaceSnapshot.Idle();
    private string deviceName = "Waiting for microphone";
    private bool isMonitoring;
    private bool isSpeaking;
    private DateTimeOffset sessionStartedAt;
    private DateTimeOffset? silenceStartedAt;
    private DateTimeOffset lastPeakAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastTrendPointAt = DateTimeOffset.MinValue;
    private DateTimeOffset? lastSnapshotAt;
    private PaceAlertLevel lastAlertLevel = PaceAlertLevel.Calm;
    private double totalWeightedWpm;
    private double totalWeightedClarity;
    private double totalWeightedSpeechRatio;
    private double accumulatedSeconds;
    private double cautionSeconds;
    private double criticalSeconds;
    private double envelope;
    private double lastEnvelope;
    private bool envelopeRising;
    private double noiseFloor = 0.004;

    public event EventHandler<LivePaceSnapshot>? SnapshotUpdated;

    public LivePaceSnapshot CurrentSnapshot
    {
        get
        {
            lock (syncRoot)
            {
                return currentSnapshot;
            }
        }
    }

    public void StartSession(AppSettings settings, string deviceName)
    {
        lock (syncRoot)
        {
            this.settings = settings.Clone();
            this.deviceName = deviceName;
            isMonitoring = true;
            isSpeaking = false;
            sessionStartedAt = DateTimeOffset.UtcNow;
            silenceStartedAt = null;
            lastPeakAt = DateTimeOffset.MinValue;
            lastTrendPointAt = DateTimeOffset.MinValue;
            lastSnapshotAt = null;
            lastAlertLevel = PaceAlertLevel.Calm;
            totalWeightedWpm = 0;
            totalWeightedClarity = 0;
            totalWeightedSpeechRatio = 0;
            accumulatedSeconds = 0;
            cautionSeconds = 0;
            criticalSeconds = 0;
            envelope = 0;
            lastEnvelope = 0;
            envelopeRising = false;
            noiseFloor = 0.004;
            syllablePeaks.Clear();
            pauses.Clear();
            chunks.Clear();
            wpmHistory.Clear();
            trendPoints.Clear();

            currentSnapshot = new LivePaceSnapshot
            {
                IsMonitoring = true,
                DeviceName = deviceName,
                StatusMessage = "Listening for your pace...",
            };
        }

        SnapshotUpdated?.Invoke(this, CurrentSnapshot);
    }

    public SessionSummary? StopSession()
    {
        lock (syncRoot)
        {
            if (!isMonitoring)
            {
                currentSnapshot = LivePaceSnapshot.Idle("Monitoring paused.");
                return null;
            }

            var endedAt = DateTimeOffset.UtcNow;
            var averageWpm = accumulatedSeconds > 0 ? totalWeightedWpm / accumulatedSeconds : currentSnapshot.RollingAverageWordsPerMinute;
            var averageClarity = accumulatedSeconds > 0 ? totalWeightedClarity / accumulatedSeconds : currentSnapshot.ClarityScore;
            var averageSpeechRatio = accumulatedSeconds > 0 ? totalWeightedSpeechRatio / accumulatedSeconds : currentSnapshot.SpeechRatio;
            var averagePause = GetAveragePause(endedAt, TimeSpan.FromSeconds(60));
            var pauseRate = GetPauseRate(endedAt, TimeSpan.FromSeconds(60));

            var summary = new SessionSummary
            {
                StartedAt = sessionStartedAt,
                EndedAt = endedAt,
                DeviceName = deviceName,
                AverageWordsPerMinute = averageWpm,
                PeakWordsPerMinute = trendPoints.Count == 0 ? currentSnapshot.EstimatedWordsPerMinute : trendPoints.Max(point => point.EstimatedWordsPerMinute),
                AverageClarityScore = averageClarity,
                AverageSpeechRatio = averageSpeechRatio,
                AveragePauseMilliseconds = averagePause,
                PauseRatePerMinute = pauseRate,
                CautionSeconds = cautionSeconds,
                CriticalSeconds = criticalSeconds,
                TrendPoints = [.. trendPoints],
            };

            isMonitoring = false;
            currentSnapshot = LivePaceSnapshot.Idle("Monitoring paused. You can reopen the app from the tray.");

            return summary;
        }
    }

    public void ProcessAudio(AudioFrame frame)
    {
        LivePaceSnapshot? snapshotToRaise = null;

        lock (syncRoot)
        {
            if (!isMonitoring || frame.Samples.Length == 0 || frame.SampleRate <= 0)
            {
                return;
            }

            var chunkDurationSeconds = frame.Samples.Length / (double)frame.SampleRate;
            if (chunkDurationSeconds <= 0)
            {
                return;
            }

            var now = frame.Timestamp;
            var chunkStart = now - TimeSpan.FromSeconds(chunkDurationSeconds);
            var rms = frame.SignalLevel;
            if (rms < noiseFloor * 1.5)
            {
                noiseFloor = (noiseFloor * 0.995) + (rms * 0.005);
            }

            var speechThreshold = Math.Max(0.012, noiseFloor * 3.2);
            var isSpeech = rms >= speechThreshold;

            UpdateSpeechState(chunkStart, isSpeech);
            DetectSyllablePeaks(frame.Samples, frame.SampleRate, chunkStart, speechThreshold);

            chunks.Enqueue((now, chunkDurationSeconds, isSpeech, rms));
            TrimQueues(now);

            var currentWpm = EstimateWordsPerMinute(now, TimeSpan.FromSeconds(20));
            var averageWpm = EstimateWordsPerMinute(now, TimeSpan.FromSeconds(60));
            wpmHistory.Enqueue((now, currentWpm));
            TrimHistory(now);

            var speechRatio = GetSpeechRatio(now, TimeSpan.FromSeconds(30));
            var pauseRate = GetPauseRate(now, TimeSpan.FromSeconds(60));
            var averagePause = GetAveragePause(now, TimeSpan.FromSeconds(60));
            var trend = GetTrend(now, currentWpm);
            var clarity = CalculateClarity(currentWpm, speechRatio, averagePause);
            var alertLevel = DetermineAlert(currentWpm, speechRatio);

            if (lastSnapshotAt is DateTimeOffset lastSnapshot)
            {
                var elapsed = (now - lastSnapshot).TotalSeconds;
                if (elapsed > 0)
                {
                    accumulatedSeconds += elapsed;
                    totalWeightedWpm += averageWpm * elapsed;
                    totalWeightedClarity += clarity * elapsed;
                    totalWeightedSpeechRatio += speechRatio * elapsed;

                    if (alertLevel == PaceAlertLevel.Caution)
                    {
                        cautionSeconds += elapsed;
                    }

                    if (alertLevel == PaceAlertLevel.Critical)
                    {
                        criticalSeconds += elapsed;
                    }
                }
            }

            lastSnapshotAt = now;

            if (lastTrendPointAt == DateTimeOffset.MinValue || (now - lastTrendPointAt).TotalSeconds >= 5)
            {
                trendPoints.Add(new SessionMetricPoint
                {
                    Timestamp = now,
                    EstimatedWordsPerMinute = currentWpm,
                    AlertLevel = alertLevel,
                    ClarityScore = clarity,
                });
                lastTrendPointAt = now;
            }

            currentSnapshot = new LivePaceSnapshot
            {
                Timestamp = now,
                IsMonitoring = true,
                DeviceName = deviceName,
                EstimatedWordsPerMinute = currentWpm,
                RollingAverageWordsPerMinute = averageWpm,
                TrendWordsPerMinute = trend,
                SpeechRatio = speechRatio,
                PauseRatePerMinute = pauseRate,
                AveragePauseMilliseconds = averagePause,
                ClarityScore = clarity,
                EnglishConfidence = null,
                AlertLevel = alertLevel,
                SignalLevel = rms,
                StatusMessage = BuildStatusMessage(alertLevel, currentWpm, trend, averagePause),
            };

            snapshotToRaise = currentSnapshot;
        }

        SnapshotUpdated?.Invoke(this, snapshotToRaise);
    }

    private void UpdateSpeechState(DateTimeOffset chunkStart, bool isSpeech)
    {
        if (isSpeech)
        {
            if (!isSpeaking)
            {
                isSpeaking = true;
                if (silenceStartedAt is DateTimeOffset silenceStarted)
                {
                    var pauseMs = (chunkStart - silenceStarted).TotalMilliseconds;
                    if (pauseMs >= 180)
                    {
                        pauses.Enqueue((chunkStart, pauseMs));
                    }

                    silenceStartedAt = null;
                }
            }

            return;
        }

        if (isSpeaking)
        {
            isSpeaking = false;
            silenceStartedAt = chunkStart;
        }
        else if (silenceStartedAt is null)
        {
            silenceStartedAt = chunkStart;
        }
    }

    private void DetectSyllablePeaks(float[] samples, int sampleRate, DateTimeOffset chunkStart, double speechThreshold)
    {
        var peakThreshold = Math.Max(0.018, speechThreshold * 1.65);

        for (var index = 0; index < samples.Length; index++)
        {
            var amplitude = Math.Abs(samples[index]);
            envelope = (envelope * 0.92) + (amplitude * 0.08);

            if (envelope > lastEnvelope)
            {
                envelopeRising = true;
            }
            else if (envelopeRising && lastEnvelope > peakThreshold)
            {
                var sampleTime = chunkStart + TimeSpan.FromSeconds(index / (double)sampleRate);
                if ((sampleTime - lastPeakAt).TotalMilliseconds >= 95)
                {
                    syllablePeaks.Enqueue(sampleTime);
                    lastPeakAt = sampleTime;
                }

                envelopeRising = false;
            }

            lastEnvelope = envelope;
        }
    }

    private void TrimQueues(DateTimeOffset now)
    {
        while (syllablePeaks.Count > 0 && (now - syllablePeaks.Peek()).TotalSeconds > 60)
        {
            syllablePeaks.Dequeue();
        }

        while (pauses.Count > 0 && (now - pauses.Peek().Timestamp).TotalSeconds > 60)
        {
            pauses.Dequeue();
        }

        while (chunks.Count > 0 && (now - chunks.Peek().Timestamp).TotalSeconds > 60)
        {
            chunks.Dequeue();
        }
    }

    private void TrimHistory(DateTimeOffset now)
    {
        while (wpmHistory.Count > 0 && (now - wpmHistory.Peek().Timestamp).TotalSeconds > 15)
        {
            wpmHistory.Dequeue();
        }
    }

    private double EstimateWordsPerMinute(DateTimeOffset now, TimeSpan window)
    {
        var threshold = now - window;
        var peaks = syllablePeaks.Count(peak => peak >= threshold);
        if (peaks == 0)
        {
            return 0;
        }

        var syllablesPerMinute = peaks * (60d / window.TotalSeconds);
        return syllablesPerMinute / 1.45;
    }

    private double GetSpeechRatio(DateTimeOffset now, TimeSpan window)
    {
        var threshold = now - window;
        var relevantChunks = chunks.Where(chunk => chunk.Timestamp >= threshold).ToList();
        var totalSeconds = relevantChunks.Sum(chunk => chunk.DurationSeconds);
        if (totalSeconds <= 0)
        {
            return 0;
        }

        return relevantChunks.Where(chunk => chunk.IsSpeech).Sum(chunk => chunk.DurationSeconds) / totalSeconds;
    }

    private double GetPauseRate(DateTimeOffset now, TimeSpan window)
    {
        var threshold = now - window;
        var relevantPauseCount = pauses.Count(pause => pause.Timestamp >= threshold);
        return relevantPauseCount * (60d / window.TotalSeconds);
    }

    private double GetAveragePause(DateTimeOffset now, TimeSpan window)
    {
        var threshold = now - window;
        var relevantPauses = pauses.Where(pause => pause.Timestamp >= threshold).ToList();
        return relevantPauses.Count == 0 ? 0 : relevantPauses.Average(pause => pause.DurationMs);
    }

    private double GetTrend(DateTimeOffset now, double currentWpm)
    {
        var baseline = wpmHistory.LastOrDefault(entry => (now - entry.Timestamp).TotalSeconds >= 5);
        return baseline == default ? 0 : currentWpm - baseline.Wpm;
    }

    private double CalculateClarity(double currentWpm, double speechRatio, double averagePause)
    {
        var clarity = 100d;
        clarity -= Math.Max(0, currentWpm - settings.TargetWordsPerMinute) * 0.18;
        clarity -= speechRatio > 0.72 ? (speechRatio - 0.72) * 110 : 0;
        clarity -= averagePause > 0 && averagePause < settings.MinimumPauseMilliseconds
            ? (settings.MinimumPauseMilliseconds - averagePause) / 8
            : 0;

        return Math.Clamp(clarity, 35, 100);
    }

    private PaceAlertLevel DetermineAlert(double currentWpm, double speechRatio)
    {
        if (speechRatio < 0.12 || currentWpm < 80)
        {
            lastAlertLevel = PaceAlertLevel.Calm;
            return lastAlertLevel;
        }

        var cautionFloor = settings.CautionWordsPerMinute;
        var criticalFloor = settings.CriticalWordsPerMinute;
        var hysteresis = settings.HysteresisWordsPerMinute;

        lastAlertLevel = lastAlertLevel switch
        {
            PaceAlertLevel.Critical when currentWpm >= criticalFloor - hysteresis => PaceAlertLevel.Critical,
            PaceAlertLevel.Critical when currentWpm >= cautionFloor - hysteresis => PaceAlertLevel.Caution,
            PaceAlertLevel.Caution when currentWpm >= criticalFloor => PaceAlertLevel.Critical,
            PaceAlertLevel.Caution when currentWpm >= cautionFloor - hysteresis => PaceAlertLevel.Caution,
            _ when currentWpm >= criticalFloor => PaceAlertLevel.Critical,
            _ when currentWpm >= cautionFloor => PaceAlertLevel.Caution,
            _ => PaceAlertLevel.Calm,
        };

        return lastAlertLevel;
    }

    private static string BuildStatusMessage(PaceAlertLevel alertLevel, double currentWpm, double trend, double averagePause)
    {
        return alertLevel switch
        {
            PaceAlertLevel.Critical => $"You are moving too fast at about {currentWpm:N0} WPM. Finish the sentence, then pause.",
            PaceAlertLevel.Caution when trend > 12 => "Your pace is climbing. Leave a deliberate beat before the next point.",
            PaceAlertLevel.Caution => "You are getting quick. Slow the next sentence down by a notch.",
            _ when averagePause > 0 && averagePause < 260 => "Your words are clear, but the pauses are getting short.",
            _ => "Steady pace. Keep the same rhythm.",
        };
    }
}