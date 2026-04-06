using PaceApp.Core.Abstractions;
using PaceApp.Core.Models;

namespace PaceApp.Analytics.Services;

public sealed class SignalPaceMetricsEngine : IPaceMetricsEngine
{
    private static readonly TimeSpan LiveWpmWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RollingWpmWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SpeechRatioWindow = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PauseWindow = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan TrendLookbackWindow = TimeSpan.FromSeconds(8);
    private const double SyllablesPerWord = 1.55;
    private const double MinimumPeakGapMilliseconds = 190;
    private const double MinimumBridgeSpeechSeconds = 0.6;
    private const int MinimumBridgePeakCount = 2;
    private const double MinimumResponsiveSpeechSeconds = 2.0;
    private const int MinimumResponsivePeakCount = 5;
    private const double MinimumRollingSpeechSeconds = 2.5;
    private const double MinimumAlertSpeechSeconds = 1.2;
    private const int MinimumAlertPeakCount = 3;
    private const double MaximumPlausibleWordsPerMinute = 250;
    private const int MinimumTranscriptCurrentWords = 2;
    private const double MinimumTranscriptCurrentSpeechSeconds = 0.5;
    private const int MinimumTranscriptRollingWords = 3;
    private const double MinimumTranscriptRollingSpeechSeconds = 1.5;

    private readonly object syncRoot = new();
    private readonly ITranscriptMetricsSource? transcriptMetricsSource;
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
    private bool peakReady = true;
    private double noiseFloor = 0.004;
    private double smoothedCurrentWpm;
    private double smoothedRollingWpm;

    public event EventHandler<LivePaceSnapshot>? SnapshotUpdated;

    public SignalPaceMetricsEngine(bool enableTranscriptRecognition = true)
        : this(enableTranscriptRecognition ? new VoskTranscriptAnalyzer() : null)
    {
    }

    public SignalPaceMetricsEngine(ITranscriptMetricsSource? transcriptMetricsSource)
    {
        this.transcriptMetricsSource = transcriptMetricsSource;
    }

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
            peakReady = true;
            noiseFloor = 0.004;
            smoothedCurrentWpm = 0;
            smoothedRollingWpm = 0;
            syllablePeaks.Clear();
            pauses.Clear();
            chunks.Clear();
            wpmHistory.Clear();
            trendPoints.Clear();

            var statusMessage = transcriptMetricsSource is not null
                ? (transcriptMetricsSource.TryStartLive()
                    ? "Listening for speech. Live WPM starts from audio first and tightens once transcript timing catches up."
                    : "Local speech recognition unavailable. Using audio-only pace estimation.")
                : "Listening for enough speech before estimating your pace.";

            currentSnapshot = new LivePaceSnapshot
            {
                IsMonitoring = true,
                DeviceName = deviceName,
                StatusMessage = statusMessage,
            };
        }

        SnapshotUpdated?.Invoke(this, CurrentSnapshot);
    }

    public SessionSummary? StopSession()
    {
        lock (syncRoot)
        {
            if (transcriptMetricsSource is not null)
            {
                transcriptMetricsSource.Stop();
            }

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

            var rawSignalBridgeWpm = EstimateResponsiveWordsPerMinute(
                now,
                LiveWpmWindow,
                MinimumBridgeSpeechSeconds,
                MinimumBridgePeakCount,
                1.5);
            var rawSignalStableWpm = EstimateResponsiveWordsPerMinute(
                now,
                LiveWpmWindow,
                MinimumResponsiveSpeechSeconds,
                MinimumResponsivePeakCount,
                2.5);
            var signalHasStableEstimate = rawSignalStableWpm > 0;
            var rawSignalCurrentWpm = signalHasStableEstimate
                ? rawSignalStableWpm
                : rawSignalBridgeWpm;
            var rawSignalAverageWpm = EstimateRollingWordsPerMinute(now, RollingWpmWindow);
            var speechRatio = GetSpeechRatio(now, SpeechRatioWindow);
            var pauseRate = GetPauseRate(now, PauseWindow);
            var averagePause = GetAveragePause(now, PauseWindow);

            transcriptMetricsSource?.FeedAudio(frame.Samples, frame.SampleRate);

            var transcriptMetrics = transcriptMetricsSource is not null
                ? transcriptMetricsSource.GetMetrics(
                    now,
                    LiveWpmWindow,
                    RollingWpmWindow,
                    MinimumTranscriptCurrentWords,
                    MinimumTranscriptCurrentSpeechSeconds,
                    MinimumTranscriptRollingWords,
                    MinimumTranscriptRollingSpeechSeconds)
                : TranscriptMetricsSnapshot.Unavailable("Using audio-only pace estimation.");

            var useTranscriptEstimate = transcriptMetrics.IsAvailable && transcriptMetrics.HasStableEstimate;

            // When transcript recognition is available, use its WPM — it measures
            // actual words and timing, which is more accurate than audio peak counting.
            // Use signal only as a fallback when transcript hasn't stabilized yet.
            var rawCurrentWpm = useTranscriptEstimate
                ? transcriptMetrics.CurrentWordsPerMinute
                : rawSignalCurrentWpm;
            var rawAverageWpm = useTranscriptEstimate && transcriptMetrics.RollingWordsPerMinute > 0
                ? transcriptMetrics.RollingWordsPerMinute
                : rawSignalAverageWpm;

            // Asymmetric smoothing: rise fast, fall gradually.
            var isRising = rawCurrentWpm > smoothedCurrentWpm;
            var currentAlpha = isRising ? 0.55 : 0.25;

            // Urgency: when raw WPM exceeds caution, boost alpha so the displayed
            // number catches up quickly and the alert can fire.
            if (rawCurrentWpm >= settings.CautionWordsPerMinute)
            {
                currentAlpha = isRising ? 0.75 : 0.35;
            }

            var currentWpm = SmoothWpm(smoothedCurrentWpm, rawCurrentWpm, currentAlpha);
            var averageAlpha = rawAverageWpm >= smoothedRollingWpm ? 0.22 : 0.18;
            var averageWpm = SmoothWpm(smoothedRollingWpm, rawAverageWpm, averageAlpha);
            smoothedCurrentWpm = currentWpm;
            smoothedRollingWpm = averageWpm;

            wpmHistory.Enqueue((now, currentWpm));
            TrimHistory(now);

            var trend = GetTrend(now, currentWpm);
            var clarity = CalculateClarity(currentWpm, speechRatio, averagePause);
            var hasStableEstimate = useTranscriptEstimate || signalHasStableEstimate;
            var hasPreliminaryEstimate = !hasStableEstimate && rawSignalCurrentWpm > 0;

            // Alert using only reliable sources: transcript when available, stable signal otherwise.
            var rawAlertWpm = useTranscriptEstimate
                ? transcriptMetrics.CurrentWordsPerMinute
                : rawSignalStableWpm;
            var alertLevel = DetermineAlert(now, currentWpm, rawAlertWpm, averageWpm);

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
                EnglishConfidence = transcriptMetrics.LatestConfidence,
                TranscriptWordsPerMinute = transcriptMetrics.CurrentWordsPerMinute,
                AlertLevel = alertLevel,
                SignalLevel = rms,
                RecognizedText = transcriptMetrics.LatestRecognizedText,
                StatusMessage = BuildStatusMessage(alertLevel, currentWpm, trend, averagePause, hasStableEstimate, hasPreliminaryEstimate, transcriptMetrics),
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
        var peakThreshold = Math.Max(0.026, speechThreshold * 2.25);
        var rearmThreshold = Math.Max(0.014, peakThreshold * 0.58);

        for (var index = 0; index < samples.Length; index++)
        {
            var amplitude = Math.Abs(samples[index]);
            envelope = (envelope * 0.92) + (amplitude * 0.08);

            if (!peakReady && envelope <= rearmThreshold)
            {
                peakReady = true;
            }

            if (envelope > lastEnvelope)
            {
                envelopeRising = true;
            }
            else if (peakReady && envelopeRising && lastEnvelope > peakThreshold)
            {
                var sampleTime = chunkStart + TimeSpan.FromSeconds(index / (double)sampleRate);
                if ((sampleTime - lastPeakAt).TotalMilliseconds >= MinimumPeakGapMilliseconds)
                {
                    syllablePeaks.Enqueue(sampleTime);
                    lastPeakAt = sampleTime;
                    peakReady = false;
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
        while (wpmHistory.Count > 0 && (now - wpmHistory.Peek().Timestamp).TotalSeconds > 30)
        {
            wpmHistory.Dequeue();
        }
    }

    private double EstimateResponsiveWordsPerMinute(DateTimeOffset now, TimeSpan window, double minimumSpeechSeconds, int minimumPeakCount, double minimumAnalysisSpanSeconds)
    {
        var recentPeaks = GetRecentPeaks(now, window);
        var recentSpeechSeconds = GetSpeechSeconds(now, window);
        if (recentPeaks.Count < minimumPeakCount || recentSpeechSeconds < minimumSpeechSeconds)
        {
            return 0;
        }

        var activeSpanSeconds = Math.Max(
            (recentPeaks[^1] - recentPeaks[0]).TotalSeconds + 0.3,
            recentSpeechSeconds);

        var analysisSpanSeconds = Math.Clamp(activeSpanSeconds, minimumAnalysisSpanSeconds, window.TotalSeconds);

        // Primary: words per minute based on speech-active time.
        var activeWordsPerMinute = (recentPeaks.Count * (60d / analysisSpanSeconds)) / SyllablesPerWord;

        // Use the active rate directly when speech fills enough of the window.
        // Only blend with the diluted window rate for very sparse speech.
        var speechFill = recentSpeechSeconds / window.TotalSeconds;
        double wordsPerMinute;
        if (speechFill >= 0.35)
        {
            wordsPerMinute = activeWordsPerMinute;
        }
        else
        {
            var fixedWindowWordsPerMinute = (recentPeaks.Count * (60d / window.TotalSeconds)) / SyllablesPerWord;
            var activeWeight = Math.Clamp(speechFill + 0.55, 0.55, 0.95);
            wordsPerMinute = (activeWordsPerMinute * activeWeight) + (fixedWindowWordsPerMinute * (1 - activeWeight));
        }

        // Decay only after extended silence; natural inter-sentence pauses
        // of up to 1.5 seconds should not reduce the estimate.
        var silenceSeconds = (now - recentPeaks[^1]).TotalSeconds;
        if (silenceSeconds > 1.5)
        {
            var fadeSeconds = Math.Max(2.0, window.TotalSeconds * 0.4);
            var decay = 1 - Math.Clamp((silenceSeconds - 1.5) / fadeSeconds, 0, 1);
            wordsPerMinute *= decay;
        }

        return Math.Clamp(wordsPerMinute, 0, MaximumPlausibleWordsPerMinute);
    }

    private double EstimateRollingWordsPerMinute(DateTimeOffset now, TimeSpan window)
    {
        var speechSeconds = GetSpeechSeconds(now, window);
        if (speechSeconds < MinimumRollingSpeechSeconds)
        {
            return 0;
        }

        var peaks = GetPeakCount(now, window);
        if (peaks == 0)
        {
            return 0;
        }

        var syllablesPerMinute = peaks * (60d / window.TotalSeconds);
        return Math.Clamp(syllablesPerMinute / SyllablesPerWord, 0, MaximumPlausibleWordsPerMinute);
    }

    private List<DateTimeOffset> GetRecentPeaks(DateTimeOffset now, TimeSpan window)
    {
        var threshold = now - window;
        return syllablePeaks.Where(peak => peak >= threshold).ToList();
    }

    private bool HasEnoughSpeechEvidence(DateTimeOffset now, TimeSpan window, double minimumSpeechSeconds, int minimumPeakCount)
    {
        return GetSpeechSeconds(now, window) >= minimumSpeechSeconds
            && GetPeakCount(now, window) >= minimumPeakCount;
    }

    private int GetPeakCount(DateTimeOffset now, TimeSpan window)
    {
        return GetRecentPeaks(now, window).Count;
    }

    private double GetSpeechSeconds(DateTimeOffset now, TimeSpan window)
    {
        var threshold = now - window;
        return chunks
            .Where(chunk => chunk.Timestamp >= threshold && chunk.IsSpeech)
            .Sum(chunk => chunk.DurationSeconds);
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
        // Compare current WPM to the average of historical values 6-10 seconds ago.
        var lookbackStart = now - TrendLookbackWindow - TimeSpan.FromSeconds(2);
        var lookbackEnd = now - TrendLookbackWindow + TimeSpan.FromSeconds(2);
        var baselineEntries = wpmHistory
            .Where(entry => entry.Timestamp >= lookbackStart && entry.Timestamp <= lookbackEnd)
            .ToList();
        if (baselineEntries.Count == 0)
        {
            return 0;
        }

        var baseline = baselineEntries.Average(entry => entry.Wpm);
        return currentWpm - baseline;
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

    private PaceAlertLevel DetermineAlert(DateTimeOffset now, double currentWpm, double rawAlertWpm, double averageWpm)
    {
        // Immediate triggers on raw alert WPM (stable signal + transcript only,
        // NOT bridge) so fast speech is detected the instant reliable evidence
        // arrives, without waiting for EMA to catch up.
        var recentSpeechSeconds = GetSpeechSeconds(now, LiveWpmWindow);
        if (recentSpeechSeconds >= MinimumAlertSpeechSeconds && rawAlertWpm > 0)
        {
            if (rawAlertWpm >= settings.CriticalWordsPerMinute)
            {
                lastAlertLevel = PaceAlertLevel.Critical;
                return lastAlertLevel;
            }

            if (rawAlertWpm >= settings.CautionWordsPerMinute)
            {
                lastAlertLevel = PaceAlertLevel.Caution;
                return lastAlertLevel;
            }
        }

        // Without reliable evidence from stable signal or transcript,
        // only allow de-escalation based on the displayed WPM.
        if (rawAlertWpm <= 0)
        {
            if (currentWpm < settings.CautionWordsPerMinute - settings.HysteresisWordsPerMinute)
            {
                lastAlertLevel = PaceAlertLevel.Calm;
            }
            else if (lastAlertLevel == PaceAlertLevel.Critical
                && currentWpm < settings.CriticalWordsPerMinute - settings.HysteresisWordsPerMinute)
            {
                lastAlertLevel = PaceAlertLevel.Caution;
            }

            return lastAlertLevel;
        }

        // If WPM is very low, reset to Calm immediately.
        if (currentWpm < 60 && rawAlertWpm < 60)
        {
            lastAlertLevel = PaceAlertLevel.Calm;
            return lastAlertLevel;
        }

        // Hysteresis: use rawAlertWpm for de-escalation decisions so alerts don't
        // flicker from noisy bridge estimates.
        var effectiveWpm = (rawAlertWpm * 0.75) + (averageWpm * 0.25);
        var cautionFloor = settings.CautionWordsPerMinute;
        var criticalFloor = settings.CriticalWordsPerMinute;
        var hysteresis = settings.HysteresisWordsPerMinute;

        lastAlertLevel = lastAlertLevel switch
        {
            PaceAlertLevel.Critical when effectiveWpm >= criticalFloor - hysteresis => PaceAlertLevel.Critical,
            PaceAlertLevel.Critical when effectiveWpm >= cautionFloor - hysteresis => PaceAlertLevel.Caution,
            PaceAlertLevel.Caution when effectiveWpm >= criticalFloor => PaceAlertLevel.Critical,
            PaceAlertLevel.Caution when effectiveWpm >= cautionFloor - hysteresis => PaceAlertLevel.Caution,
            _ when effectiveWpm >= criticalFloor => PaceAlertLevel.Critical,
            _ when effectiveWpm >= cautionFloor => PaceAlertLevel.Caution,
            _ => PaceAlertLevel.Calm,
        };

        return lastAlertLevel;
    }

    private static string BuildStatusMessage(PaceAlertLevel alertLevel, double currentWpm, double trend, double averagePause, bool hasStableEstimate, bool hasPreliminaryEstimate, TranscriptMetricsSnapshot transcriptMetrics)
    {
        if (hasPreliminaryEstimate)
        {
            return transcriptMetrics.IsAvailable
                ? "Live WPM is warming up from audio while transcript timing catches up."
                : "Live WPM is warming up from audio. Keep talking for a steadier reading.";
        }

        if (transcriptMetrics.IsAvailable && !hasStableEstimate)
        {
            return transcriptMetrics.StatusMessage;
        }

        if (!hasStableEstimate)
        {
            return "Listening for speech before estimating your pace.";
        }

        return alertLevel switch
        {
            PaceAlertLevel.Critical => $"You are moving too fast at about {currentWpm:N0} WPM. Finish the sentence, then pause.",
            PaceAlertLevel.Caution when trend > 12 => "Your pace is climbing. Leave a deliberate beat before the next point.",
            PaceAlertLevel.Caution => "You are getting quick. Slow the next sentence down by a notch.",
            _ when averagePause > 0 && averagePause < 260 => "Your words are clear, but the pauses are getting short.",
            _ => "Steady pace. Keep the same rhythm.",
        };
    }

    private static double SmoothWpm(double previousValue, double nextValue, double alpha)
    {
        return previousValue <= 0
            ? nextValue
            : (previousValue * (1 - alpha)) + (nextValue * alpha);
    }
}