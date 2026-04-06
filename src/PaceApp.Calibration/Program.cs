using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text.RegularExpressions;
using NAudio.Wave;
using PaceApp.Analytics.Services;
using PaceApp.Core.Models;

var outputDirectory = ResolveCalibrationOutputDirectory();
Directory.CreateDirectory(outputDirectory);

var baselineScript = "Hello, my name is Hammad Aslam. Today I am explaining a small app that helps people pace their speech during online calls. The goal is to stay clear, calm, and easy to follow. I want to finish each idea, leave a short pause, and then move to the next point without rushing.";
var repeatedBurstScript = "Very very fast very very fast very very fast very very fast very very fast very very fast very very fast very very fast.";
var variedFastScript = "I need to cover the roadmap, the demo flow, the rollout timing, the open risks, and the next action items before this meeting ends, so I am pushing through every point too quickly and barely leaving space to breathe.";

var scenarios = new[]
{
    new CalibrationScenario("slow-deliberate", "Slow deliberate speech", 110, baselineScript, PaceAlertLevel.Calm),
    new CalibrationScenario("normal-conversation", "Normal conversation pace", 140, baselineScript, PaceAlertLevel.Calm),
    new CalibrationScenario("fast-explainer", "Fast explainer pace", 190, baselineScript, PaceAlertLevel.Caution),
    new CalibrationScenario("fast-varied-passage", "Fast varied passage", 210, variedFastScript, PaceAlertLevel.Critical),
    new CalibrationScenario("fast-repeated-burst", "Fast repeated burst phrase", 220, repeatedBurstScript, PaceAlertLevel.Critical),
};

Console.WriteLine("PaceApp calibration baseline");
Console.WriteLine("Research baseline: conversational speech is commonly around 120-150 WPM, comfortable presentation pace around 100-150 WPM, and 170+ WPM is typically perceived as fast.");
Console.WriteLine($"Output directory: {outputDirectory}");
Console.WriteLine();

var settings = new AppSettings();
settings.ApplyRecommendedPaceBand();

var results = new List<CalibrationResult>();
foreach (var scenario in scenarios)
{
    var generatedSample = GenerateSampleForTargetRate(scenario, outputDirectory);
    var analysis = await AnalyzeSampleAsync(generatedSample.FilePath, scenario, settings, outputDirectory);
    results.Add(analysis);
}

foreach (var result in results)
{
    Console.WriteLine(result.FormatReport());
}

var reportPath = Path.Combine(outputDirectory, "calibration-report.txt");
await File.WriteAllLinesAsync(reportPath, results.Select(result => result.FormatReport()));
Console.WriteLine($"Saved report to {reportPath}");

return;

static string ResolveCalibrationOutputDirectory()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "PaceApp.slnx")))
        {
            return Path.Combine(current.FullName, "artifacts", "calibration");
        }

        current = current.Parent;
    }

    return Path.Combine(AppContext.BaseDirectory, "calibration-output");
}

static GeneratedSample GenerateSampleForTargetRate(CalibrationScenario scenario, string outputDirectory)
{
    GeneratedSample? bestSample = null;
    var candidateFiles = new List<string>();

    foreach (var rate in Enumerable.Range(-6, 15))
    {
        var tempPath = Path.Combine(outputDirectory, $"{scenario.Slug}-r{rate}.wav");
        GenerateSpeechToWave(tempPath, scenario.Script, rate);
        candidateFiles.Add(tempPath);

        using var reader = new AudioFileReader(tempPath);
        var actualWpm = scenario.WordCount / reader.TotalTime.TotalMinutes;
        var sample = new GeneratedSample(tempPath, rate, actualWpm, scenario.WordCount, reader.TotalTime);

        if (bestSample is null || Math.Abs(sample.ActualWordsPerMinute - scenario.TargetWordsPerMinute) < Math.Abs(bestSample.ActualWordsPerMinute - scenario.TargetWordsPerMinute))
        {
            bestSample = sample;
        }
    }

    if (bestSample is null)
    {
        throw new InvalidOperationException($"Could not generate a sample for {scenario.Name}.");
    }

    var finalPath = Path.Combine(outputDirectory, $"{scenario.Slug}.wav");
    foreach (var candidateFile in candidateFiles)
    {
        if (string.Equals(candidateFile, bestSample.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        File.Delete(candidateFile);
    }

    if (!string.Equals(bestSample.FilePath, finalPath, StringComparison.OrdinalIgnoreCase))
    {
        File.Move(bestSample.FilePath, finalPath, true);
        bestSample = bestSample with { FilePath = finalPath };
    }

    return bestSample;
}

static void GenerateSpeechToWave(string outputPath, string script, int rate)
{
    var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("Windows SAPI voice is not available.");
    var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream") ?? throw new InvalidOperationException("Windows SAPI file stream is not available.");

    object? voice = null;
    object? stream = null;

    try
    {
        voice = Activator.CreateInstance(voiceType);
        stream = Activator.CreateInstance(streamType);
        dynamic dynamicVoice = voice ?? throw new InvalidOperationException("Could not create SAPI voice instance.");
        dynamic dynamicStream = stream ?? throw new InvalidOperationException("Could not create SAPI stream instance.");

        dynamicStream.Open(outputPath, 3, false);
        dynamicVoice.AudioOutputStream = dynamicStream;
        dynamicVoice.Rate = rate;
        dynamicVoice.Volume = 100;
        dynamicVoice.Speak(script, 0);
        dynamicStream.Close();
    }
    finally
    {
        if (stream is not null)
        {
            Marshal.FinalReleaseComObject(stream);
        }

        if (voice is not null)
        {
            Marshal.FinalReleaseComObject(voice);
        }
    }
}

static async Task<CalibrationResult> AnalyzeSampleAsync(string filePath, CalibrationScenario scenario, AppSettings settings, string outputDirectory)
{
    var engine = new SignalPaceMetricsEngine(enableTranscriptRecognition: false);
    var snapshots = new List<LivePaceSnapshot>();
    engine.SnapshotUpdated += (_, snapshot) => snapshots.Add(snapshot);
    engine.StartSession(settings, Path.GetFileName(filePath));

    using var reader = new AudioFileReader(filePath);
    var sampleRate = reader.WaveFormat.SampleRate;
    var channels = reader.WaveFormat.Channels;
    var frameSampleCount = Math.Max(sampleRate / 10, 1);
    var buffer = new float[frameSampleCount * channels];
    var timestamp = DateTimeOffset.UtcNow;

    while (true)
    {
        var samplesRead = reader.Read(buffer, 0, buffer.Length);
        if (samplesRead == 0)
        {
            break;
        }

        var monoSamples = ToMono(buffer, samplesRead, channels);
        var chunkDurationSeconds = monoSamples.Length / (double)sampleRate;
        timestamp = timestamp.AddSeconds(chunkDurationSeconds);

        engine.ProcessAudio(new AudioFrame
        {
            Timestamp = timestamp,
            SampleRate = sampleRate,
            Samples = monoSamples,
            SignalLevel = CalculateRms(monoSamples),
            DeviceName = Path.GetFileName(filePath),
        });
    }

    var summary = engine.StopSession() ?? throw new InvalidOperationException("Calibration analysis did not produce a session summary.");
    var peakDisplayedWpm = snapshots.Count == 0 ? 0 : snapshots.Max(snapshot => snapshot.EstimatedWordsPerMinute);
    var maxAlert = snapshots.Count == 0 ? PaceAlertLevel.Calm : snapshots.Max(snapshot => snapshot.AlertLevel);
    var transcriptAnalysis = await AnalyzeTranscriptAsync(filePath);
    var hybridLiveAnalysis = await AnalyzeHybridLiveAsync(filePath, scenario, settings, outputDirectory);

    return new CalibrationResult(
        scenario,
        filePath,
        summary.EndedAt - summary.StartedAt,
        scenario.WordCount / reader.TotalTime.TotalMinutes,
        summary.AverageWordsPerMinute,
        summary.PeakWordsPerMinute,
        peakDisplayedWpm,
        summary.CautionSeconds,
        summary.CriticalSeconds,
        maxAlert,
        transcriptAnalysis,
        hybridLiveAnalysis);
}

static async Task<HybridLiveSimulationResult> AnalyzeHybridLiveAsync(string filePath, CalibrationScenario scenario, AppSettings settings, string outputDirectory)
{
    var sessionStart = DateTimeOffset.UtcNow;
    var transcriptSource = await ReplayTranscriptMetricsSource.CreateFromWaveFileAsync(filePath, sessionStart);
    var engine = new SignalPaceMetricsEngine(transcriptSource);
    var snapshots = new List<LivePaceSnapshot>();
    engine.SnapshotUpdated += (_, snapshot) => snapshots.Add(snapshot);
    engine.StartSession(settings, Path.GetFileName(filePath));

    using var reader = new AudioFileReader(filePath);
    var sampleRate = reader.WaveFormat.SampleRate;
    var channels = reader.WaveFormat.Channels;
    var frameSampleCount = Math.Max(sampleRate / 10, 1);
    var buffer = new float[frameSampleCount * channels];
    var timestamp = sessionStart;

    while (true)
    {
        var samplesRead = reader.Read(buffer, 0, buffer.Length);
        if (samplesRead == 0)
        {
            break;
        }

        var monoSamples = ToMono(buffer, samplesRead, channels);
        var chunkDurationSeconds = monoSamples.Length / (double)sampleRate;
        timestamp = timestamp.AddSeconds(chunkDurationSeconds);

        engine.ProcessAudio(new AudioFrame
        {
            Timestamp = timestamp,
            SampleRate = sampleRate,
            Samples = monoSamples,
            SignalLevel = CalculateRms(monoSamples),
            DeviceName = Path.GetFileName(filePath),
        });
    }

    var summary = engine.StopSession() ?? throw new InvalidOperationException("Hybrid live simulation did not produce a session summary.");
    var peakDisplayedWpm = snapshots.Count == 0 ? 0 : snapshots.Max(snapshot => snapshot.EstimatedWordsPerMinute);
    var maxAlert = snapshots.Count == 0 ? PaceAlertLevel.Calm : snapshots.Max(snapshot => snapshot.AlertLevel);
    var firstVisibleWpmAt = snapshots.FirstOrDefault(snapshot => snapshot.EstimatedWordsPerMinute >= 20)?.Timestamp - sessionStart;
    var firstCautionAt = snapshots.FirstOrDefault(snapshot => snapshot.AlertLevel >= PaceAlertLevel.Caution)?.Timestamp - sessionStart;
    var firstCriticalAt = snapshots.FirstOrDefault(snapshot => snapshot.AlertLevel >= PaceAlertLevel.Critical)?.Timestamp - sessionStart;
    var csvPath = Path.Combine(outputDirectory, $"{scenario.Slug}-hybrid-live.csv");
    await WriteSnapshotsCsvAsync(csvPath, sessionStart, snapshots);

    return new HybridLiveSimulationResult(
        AverageWordsPerMinute: summary.AverageWordsPerMinute,
        PeakDisplayedWordsPerMinute: peakDisplayedWpm,
        MaxAlert: maxAlert,
        FirstVisibleWpmAt: firstVisibleWpmAt,
        FirstCautionAt: firstCautionAt,
        FirstCriticalAt: firstCriticalAt,
        SnapshotCsvPath: csvPath);
}

static async Task<TranscriptCalibrationResult> AnalyzeTranscriptAsync(string filePath)
{
    try
    {
        var recognizerInfo = SpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(info => info.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault()
            ?? throw new InvalidOperationException("No local Windows speech recognizer is installed.");

        using var recognizer = new SpeechRecognitionEngine(recognizerInfo);
        var segments = new List<(int Words, double Start, double End, double Confidence, string Text)>();
        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        recognizer.UnloadAllGrammars();
        recognizer.LoadGrammar(new DictationGrammar());
        recognizer.MaxAlternates = 1;
        recognizer.BabbleTimeout = TimeSpan.FromSeconds(0);
        recognizer.InitialSilenceTimeout = TimeSpan.FromSeconds(3);
        recognizer.EndSilenceTimeout = TimeSpan.FromMilliseconds(350);
        recognizer.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(650);
        recognizer.SpeechRecognized += (_, eventArgs) =>
        {
            var result = eventArgs.Result;
            if (result is null || string.IsNullOrWhiteSpace(result.Text))
            {
                return;
            }

            var words = Regex.Matches(result.Text, @"\b[\p{L}\p{N}']+\b").Count;
            if (words == 0)
            {
                return;
            }

            var audio = result.Audio;
            var start = audio?.AudioPosition.TotalSeconds ?? 0;
            var duration = audio?.Duration.TotalSeconds ?? Math.Max(0.6, words * 0.38);
            segments.Add((words, start, start + duration, result.Confidence, result.Text));
        };
        recognizer.RecognizeCompleted += (_, eventArgs) =>
        {
            if (eventArgs.Error is not null)
            {
                completionSource.TrySetException(eventArgs.Error);
            }
            else
            {
                completionSource.TrySetResult(true);
            }
        };

        recognizer.SetInputToWaveFile(filePath);
        recognizer.RecognizeAsync(RecognizeMode.Multiple);
        await completionSource.Task;

        if (segments.Count == 0)
        {
            return TranscriptCalibrationResult.Failure("No speech recognized.");
        }

        var totalWords = segments.Sum(s => s.Words);
        var timelineSeconds = Math.Max(segments.Max(s => s.End) - segments.Min(s => s.Start), 0.01);
        var averageWpm = totalWords * (60d / timelineSeconds);
        var peakWpm = segments.Max(s => s.Words * (60d / Math.Max(s.End - s.Start, 0.01)));
        var text = string.Join(' ', segments.Select(s => s.Text));

        return TranscriptCalibrationResult.Success(totalWords, Math.Min(averageWpm, 230), Math.Min(peakWpm, 230), text);
    }
    catch (Exception exception)
    {
        return TranscriptCalibrationResult.Failure(exception.Message);
    }
}

static float[] ToMono(float[] interleavedSamples, int samplesRead, int channels)
{
    if (channels <= 1)
    {
        return interleavedSamples.Take(samplesRead).ToArray();
    }

    var frameCount = samplesRead / channels;
    var mono = new float[frameCount];
    for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
    {
        double sum = 0;
        for (var channel = 0; channel < channels; channel++)
        {
            sum += interleavedSamples[(frameIndex * channels) + channel];
        }

        mono[frameIndex] = (float)(sum / channels);
    }

    return mono;
}

static double CalculateRms(IEnumerable<float> samples)
{
    var sum = 0d;
    var count = 0;
    foreach (var sample in samples)
    {
        sum += sample * sample;
        count++;
    }

    return count == 0 ? 0 : Math.Sqrt(sum / count);
}

static async Task WriteSnapshotsCsvAsync(string csvPath, DateTimeOffset sessionStart, IReadOnlyList<LivePaceSnapshot> snapshots)
{
    var lines = new List<string>
    {
        "elapsedSeconds,estimatedWpm,rollingWpm,alert,status"
    };

    foreach (var snapshot in snapshots)
    {
        var elapsedSeconds = (snapshot.Timestamp - sessionStart).TotalSeconds;
        var status = snapshot.StatusMessage.Replace('"', '\'');
        lines.Add($"{elapsedSeconds:F2},{snapshot.EstimatedWordsPerMinute:F1},{snapshot.RollingAverageWordsPerMinute:F1},{snapshot.AlertLevel},\"{status}\"");
    }

    await File.WriteAllLinesAsync(csvPath, lines);
}

internal sealed record CalibrationScenario(string Slug, string Name, double TargetWordsPerMinute, string Script, PaceAlertLevel ExpectedMaxAlert)
{
    public int WordCount { get; } = Regex.Matches(Script, @"\b[\p{L}\p{N}']+\b").Count;
}

internal sealed record GeneratedSample(string FilePath, int VoiceRate, double ActualWordsPerMinute, int WordCount, TimeSpan Duration);

internal sealed record CalibrationResult(
    CalibrationScenario Scenario,
    string FilePath,
    TimeSpan SessionDuration,
    double ActualWordsPerMinute,
    double EstimatedAverageWordsPerMinute,
    double EstimatedPeakWordsPerMinute,
    double EstimatedDisplayedPeakWordsPerMinute,
    double CautionSeconds,
    double CriticalSeconds,
    PaceAlertLevel MaxAlert,
    TranscriptCalibrationResult Transcript,
    HybridLiveSimulationResult HybridLive)
{
    public string FormatReport()
    {
        var transcriptSummary = Transcript.IsAvailable
            ? $"Transcript average / peak WPM: {Transcript.AverageWordsPerMinute:N1} / {Transcript.PeakWordsPerMinute:N1}"
            : $"Transcript analysis: unavailable ({Transcript.ErrorMessage})";
        var transcriptWords = Transcript.IsAvailable
            ? $"Transcript recognized words / expected words: {Transcript.RecognizedWordCount:N0} / {Scenario.WordCount:N0}"
            : string.Empty;
        var transcriptPreview = Transcript.IsAvailable && !string.IsNullOrWhiteSpace(Transcript.RecognizedText)
            ? $"Transcript preview: {BuildPreview(Transcript.RecognizedText)}"
            : string.Empty;
        var hybridTiming = $"Hybrid live first visible WPM / caution / critical: {FormatElapsed(HybridLive.FirstVisibleWpmAt)} / {FormatElapsed(HybridLive.FirstCautionAt)} / {FormatElapsed(HybridLive.FirstCriticalAt)}";

        var lines = new[]
        {
            $"Scenario: {Scenario.Name}",
            $"Sample: {FilePath}",
            $"Target / actual WPM: {Scenario.TargetWordsPerMinute:N0} / {ActualWordsPerMinute:N1}",
            $"Signal engine average / peak WPM: {EstimatedAverageWordsPerMinute:N1} / {EstimatedPeakWordsPerMinute:N1}",
            $"Signal peak displayed live WPM: {EstimatedDisplayedPeakWordsPerMinute:N1}",
            $"Signal max alert / expected max alert: {MaxAlert} / {Scenario.ExpectedMaxAlert}",
            $"Signal caution / critical seconds: {CautionSeconds:N1} / {CriticalSeconds:N1}",
            transcriptSummary,
            transcriptWords,
            transcriptPreview,
            $"Hybrid live average / peak WPM: {HybridLive.AverageWordsPerMinute:N1} / {HybridLive.PeakDisplayedWordsPerMinute:N1}",
            $"Hybrid live max alert / expected max alert: {HybridLive.MaxAlert} / {Scenario.ExpectedMaxAlert}",
            hybridTiming,
            $"Hybrid live snapshot CSV: {HybridLive.SnapshotCsvPath}",
            string.Empty,
        };

        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string BuildPreview(string text)
    {
        const int maximumPreviewLength = 120;
        if (text.Length <= maximumPreviewLength)
        {
            return text;
        }

        return text[..maximumPreviewLength].TrimEnd() + "...";
    }

    private static string FormatElapsed(TimeSpan? elapsed)
        => elapsed.HasValue ? $"{elapsed.Value.TotalSeconds:N1}s" : "n/a";
}

internal sealed record TranscriptCalibrationResult(
    bool IsAvailable,
    int RecognizedWordCount,
    double AverageWordsPerMinute,
    double PeakWordsPerMinute,
    string RecognizedText,
    string ErrorMessage)
{
    public static TranscriptCalibrationResult Success(int recognizedWordCount, double averageWordsPerMinute, double peakWordsPerMinute, string recognizedText)
        => new(true, recognizedWordCount, averageWordsPerMinute, peakWordsPerMinute, recognizedText, string.Empty);

    public static TranscriptCalibrationResult Failure(string errorMessage)
        => new(false, 0, 0, 0, string.Empty, errorMessage);
}

internal sealed record HybridLiveSimulationResult(
    double AverageWordsPerMinute,
    double PeakDisplayedWordsPerMinute,
    PaceAlertLevel MaxAlert,
    TimeSpan? FirstVisibleWpmAt,
    TimeSpan? FirstCautionAt,
    TimeSpan? FirstCriticalAt,
    string SnapshotCsvPath);

internal sealed class ReplayTranscriptMetricsSource : ITranscriptMetricsSource
{
    private readonly DateTimeOffset sessionStart;
    private readonly IReadOnlyList<ReplayTranscriptSegment> segments;
    private bool started;
    private double? latestConfidence;

    private ReplayTranscriptMetricsSource(DateTimeOffset sessionStart, IReadOnlyList<ReplayTranscriptSegment> segments)
    {
        this.sessionStart = sessionStart;
        this.segments = segments;
    }

    public static async Task<ReplayTranscriptMetricsSource> CreateFromWaveFileAsync(string filePath, DateTimeOffset sessionStart)
    {
        var segments = await ExtractSegmentsAsync(filePath);
        return new ReplayTranscriptMetricsSource(sessionStart, segments);
    }

    public bool TryStartLive(CultureInfo? preferredCulture = null)
    {
        started = true;
        return true;
    }

    public void Stop()
    {
        started = false;
        latestConfidence = null;
    }

    public void FeedAudio(float[] monoSamples, int sampleRate)
    {
        // Replay source does not process live audio.
    }

    public TranscriptMetricsSnapshot GetMetrics(
        DateTimeOffset now,
        TimeSpan liveWindow,
        TimeSpan rollingWindow,
        int minimumCurrentWords,
        double minimumCurrentSpeechSeconds,
        int minimumRollingWords,
        double minimumRollingSpeechSeconds)
    {
        if (!started)
        {
            return TranscriptMetricsSnapshot.Unavailable("Replay transcript source is not running.");
        }

        var elapsed = now - sessionStart;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var current = CalculateWindowMetrics(elapsed, liveWindow, includeHypothesis: true, hypothesisWeight: 0.82, minimumCurrentWords, minimumCurrentSpeechSeconds);
        var rolling = CalculateWindowMetrics(elapsed, rollingWindow, includeHypothesis: false, hypothesisWeight: 0, minimumRollingWords, minimumRollingSpeechSeconds);

        return new TranscriptMetricsSnapshot(
            IsAvailable: true,
            HasStableEstimate: current.HasStableEstimate,
            CurrentWordsPerMinute: current.WordsPerMinute,
            RollingWordsPerMinute: rolling.WordsPerMinute,
            LatestConfidence: latestConfidence,
            StatusMessage: "Simulated live transcript replay from sample audio.");
    }

    private WindowMetric CalculateWindowMetrics(
        TimeSpan elapsed,
        TimeSpan window,
        bool includeHypothesis,
        double hypothesisWeight,
        int minimumWords,
        double minimumSpeechSeconds)
    {
        var windowStart = elapsed - window;
        if (windowStart < TimeSpan.Zero)
        {
            windowStart = TimeSpan.Zero;
        }

        double effectiveWords = 0;
        double effectiveSpeechSeconds = 0;
        double observedWords = 0;
        double observedSpeechSeconds = 0;
        TimeSpan? mostRecentSpeechEnd = null;

        foreach (var segment in segments.Where(segment => segment.AvailabilityOffset <= elapsed))
        {
            var overlapSeconds = GetOverlapSeconds(segment.StartOffset, segment.EndOffset, windowStart, elapsed);
            if (overlapSeconds <= 0)
            {
                continue;
            }

            var overlapRatio = overlapSeconds / Math.Max(segment.DurationSeconds, 0.01);
            var observedSegmentWords = segment.WordCount * overlapRatio;
            var confidenceWeight = Math.Clamp(0.88 + (segment.Confidence * 0.12), 0.88, 1.0);
            observedWords += observedSegmentWords;
            observedSpeechSeconds += overlapSeconds;
            effectiveWords += observedSegmentWords * confidenceWeight;
            effectiveSpeechSeconds += overlapSeconds;
            latestConfidence = segment.Confidence;
            mostRecentSpeechEnd = !mostRecentSpeechEnd.HasValue || segment.EndOffset > mostRecentSpeechEnd
                ? segment.EndOffset
                : mostRecentSpeechEnd;
        }

        if (includeHypothesis)
        {
            var hypothesisSegment = segments.LastOrDefault(segment => segment.StartOffset < elapsed && segment.AvailabilityOffset > elapsed);
            if (hypothesisSegment is not null)
            {
                var hypothesisEnd = elapsed < hypothesisSegment.EndOffset ? elapsed : hypothesisSegment.EndOffset;
                var overlapSeconds = GetOverlapSeconds(hypothesisSegment.StartOffset, hypothesisEnd, windowStart, elapsed);
                if (overlapSeconds > 0)
                {
                    var overlapRatio = overlapSeconds / Math.Max(hypothesisSegment.DurationSeconds, 0.01);
                    var observedHypothesisWords = hypothesisSegment.WordCount * overlapRatio;
                    var confidenceWeight = Math.Clamp(0.78 + (hypothesisSegment.Confidence * 0.12), 0.78, 0.94);
                    observedWords += observedHypothesisWords;
                    observedSpeechSeconds += overlapSeconds;
                    effectiveWords += observedHypothesisWords * hypothesisWeight * confidenceWeight;
                    effectiveSpeechSeconds += overlapSeconds * hypothesisWeight;
                    latestConfidence = hypothesisSegment.Confidence;
                    mostRecentSpeechEnd = !mostRecentSpeechEnd.HasValue || hypothesisEnd > mostRecentSpeechEnd
                        ? hypothesisEnd
                        : mostRecentSpeechEnd;
                }
            }
        }

        if (observedWords < minimumWords || observedSpeechSeconds < minimumSpeechSeconds)
        {
            return WindowMetric.Unstable;
        }

        var activeWordsPerMinute = effectiveWords * (60d / Math.Max(effectiveSpeechSeconds, 0.01));
        var windowWordsPerMinute = effectiveWords * (60d / window.TotalSeconds);
        var evidenceWeight = Math.Clamp((observedSpeechSeconds / window.TotalSeconds) + 0.14, 0.38, 0.78);
        var wordsPerMinute = (activeWordsPerMinute * evidenceWeight) + (windowWordsPerMinute * (1 - evidenceWeight));

        if (includeHypothesis && mostRecentSpeechEnd is TimeSpan mostRecentEnd)
        {
            var silenceSeconds = (elapsed - mostRecentEnd).TotalSeconds;
            if (silenceSeconds > 0.65)
            {
                var fadeSeconds = Math.Max(1.2, window.TotalSeconds * 0.35);
                var decay = 1 - Math.Clamp((silenceSeconds - 0.65) / fadeSeconds, 0, 1);
                wordsPerMinute *= decay;
            }
        }

        return new WindowMetric(Math.Clamp(wordsPerMinute, 0, 230), true);
    }

    private static async Task<IReadOnlyList<ReplayTranscriptSegment>> ExtractSegmentsAsync(string filePath)
    {
        var recognizerInfo = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(info => info.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault()
            ?? throw new InvalidOperationException("No local Windows speech recognizer is installed.");

        using var recognizer = new SpeechRecognitionEngine(recognizerInfo);
        var segments = new List<ReplayTranscriptSegment>();
        var completionSource = new TaskCompletionSource<IReadOnlyList<ReplayTranscriptSegment>>(TaskCreationOptions.RunContinuationsAsynchronously);

        recognizer.UnloadAllGrammars();
        recognizer.LoadGrammar(new DictationGrammar());
        recognizer.MaxAlternates = 1;
        recognizer.BabbleTimeout = TimeSpan.FromSeconds(0);
        recognizer.InitialSilenceTimeout = TimeSpan.FromSeconds(3);
        recognizer.EndSilenceTimeout = TimeSpan.FromMilliseconds(350);
        recognizer.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(650);
        recognizer.SpeechRecognized += (_, eventArgs) =>
        {
            var segment = CreateReplaySegment(eventArgs.Result);
            if (segment is not null)
            {
                segments.Add(segment);
            }
        };
        recognizer.RecognizeCompleted += (_, eventArgs) =>
        {
            if (eventArgs.Error is not null)
            {
                completionSource.TrySetException(eventArgs.Error);
                return;
            }

            completionSource.TrySetResult(segments.OrderBy(segment => segment.StartOffset).ToList());
        };

        recognizer.SetInputToWaveFile(filePath);
        recognizer.RecognizeAsync(RecognizeMode.Multiple);
        return await completionSource.Task;
    }

    private static ReplayTranscriptSegment? CreateReplaySegment(RecognitionResult? result)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.Text))
        {
            return null;
        }

        var wordCount = Regex.Matches(result.Text, @"\b[\p{L}\p{N}']+\b").Count;
        if (wordCount == 0)
        {
            return null;
        }

        var audio = result.Audio;
        var startOffset = audio?.AudioPosition ?? TimeSpan.Zero;
        var duration = audio?.Duration ?? TimeSpan.FromSeconds(Math.Max(0.6, wordCount * 0.38));
        var endOffset = startOffset + duration;
        var availabilityDelay = TimeSpan.FromMilliseconds(Math.Clamp(duration.TotalMilliseconds * 0.22, 90, 260));
        return new ReplayTranscriptSegment(startOffset, endOffset, endOffset + availabilityDelay, wordCount, result.Confidence, result.Text);
    }

    private static double GetOverlapSeconds(TimeSpan segmentStart, TimeSpan segmentEnd, TimeSpan windowStart, TimeSpan windowEnd)
    {
        var overlapStart = segmentStart > windowStart ? segmentStart : windowStart;
        var overlapEnd = segmentEnd < windowEnd ? segmentEnd : windowEnd;
        return overlapEnd <= overlapStart ? 0 : (overlapEnd - overlapStart).TotalSeconds;
    }

    private sealed record ReplayTranscriptSegment(
        TimeSpan StartOffset,
        TimeSpan EndOffset,
        TimeSpan AvailabilityOffset,
        int WordCount,
        double Confidence,
        string Text)
    {
        public double DurationSeconds => Math.Max((EndOffset - StartOffset).TotalSeconds, 0.01);
    }

    private readonly record struct WindowMetric(double WordsPerMinute, bool HasStableEstimate)
    {
        public static WindowMetric Unstable => new(0, false);
    }
}