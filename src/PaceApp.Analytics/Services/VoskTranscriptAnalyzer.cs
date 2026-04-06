using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

using Vosk;

namespace PaceApp.Analytics.Services;

public sealed class VoskTranscriptAnalyzer : IDisposable, ITranscriptMetricsSource
{
    private static readonly Regex WordRegex = new(@"\b[\p{L}\p{N}']+\b", RegexOptions.Compiled);
    private static readonly TimeSpan SegmentRetention = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HypothesisGracePeriod = TimeSpan.FromSeconds(1.2);
    private const double MaximumPlausibleWordsPerMinute = 230;
    private const int VoskSampleRate = 16000;
    private const string ModelDirectoryName = "vosk-model-small-en-us-0.15";
    private const string ModelDownloadUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";

    private readonly object syncRoot = new();
    private readonly Queue<TranscriptSegment> finalSegments = new();

    private Model? model;
    private VoskRecognizer? recognizer;
    private TranscriptSegment? currentHypothesis;
    private bool disposed;
    private bool isLiveSession;
    private string statusMessage = "Vosk speech recognition not started.";
    private double streamPositionSeconds;

    // Resample state: linear interpolation from device rate to 16 kHz.
    private double resampleFraction;

    public bool IsAvailable { get; private set; }

    public double? LatestConfidence { get; private set; }

    public string StatusMessage => statusMessage;

    public bool TryStartLive(CultureInfo? preferredCulture = null)
    {
        lock (syncRoot)
        {
            ResetTracking();

            try
            {
                var modelPath = EnsureModelAvailable();
                Vosk.Vosk.SetLogLevel(-1);
                model = new Model(modelPath);
                recognizer = new VoskRecognizer(model, VoskSampleRate);
                recognizer.SetWords(true);
                recognizer.SetPartialWords(true);
                isLiveSession = true;
                IsAvailable = true;
                statusMessage = "Listening for speech with Vosk. Live WPM starts once transcript catches up.";
                return true;
            }
            catch (Exception exception)
            {
                DisposeRecognizerLocked();
                IsAvailable = false;
                isLiveSession = false;
                statusMessage = $"Vosk speech recognition unavailable. Using audio-only pace estimation. {exception.Message}";
                return false;
            }
        }
    }

    public void FeedAudio(float[] monoSamples, int sampleRate)
    {
        lock (syncRoot)
        {
            if (!isLiveSession || recognizer is null || monoSamples.Length == 0)
            {
                return;
            }

            var pcm16k = ResampleTo16kPcm(monoSamples, sampleRate);
            if (pcm16k.Length == 0)
            {
                return;
            }

            var accepted = recognizer.AcceptWaveform(pcm16k, pcm16k.Length);
            streamPositionSeconds += monoSamples.Length / (double)sampleRate;

            if (accepted)
            {
                ProcessFinalResult(recognizer.Result());
            }
            else
            {
                ProcessPartialResult(recognizer.PartialResult());
            }
        }
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
        lock (syncRoot)
        {
            if (!IsAvailable)
            {
                return TranscriptMetricsSnapshot.Unavailable(statusMessage);
            }

            TrimSegments(now);

            var current = CalculateWindowMetrics(now, liveWindow, includeHypothesis: true, hypothesisWeight: 0.92, minimumCurrentWords, minimumCurrentSpeechSeconds);
            var rolling = CalculateWindowMetrics(now, rollingWindow, includeHypothesis: false, hypothesisWeight: 0, minimumRollingWords, minimumRollingSpeechSeconds);

            var latestText = currentHypothesis?.Text
                ?? finalSegments.LastOrDefault()?.Text
                ?? string.Empty;

            return new TranscriptMetricsSnapshot(
                IsAvailable: true,
                HasStableEstimate: current.HasStableEstimate,
                CurrentWordsPerMinute: current.WordsPerMinute,
                RollingWordsPerMinute: rolling.WordsPerMinute,
                LatestConfidence: LatestConfidence,
                StatusMessage: statusMessage,
                LatestRecognizedText: latestText);
        }
    }

    public void Stop()
    {
        lock (syncRoot)
        {
            if (isLiveSession && recognizer is not null)
            {
                ProcessFinalResult(recognizer.FinalResult());
            }

            DisposeRecognizerLocked();
            IsAvailable = false;
            isLiveSession = false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lock (syncRoot)
        {
            disposed = true;
            DisposeRecognizerLocked();
            finalSegments.Clear();
            currentHypothesis = null;
        }
    }

    private void ProcessFinalResult(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("result", out var wordsArray) || wordsArray.GetArrayLength() == 0)
        {
            currentHypothesis = null;
            return;
        }

        var text = root.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
        var wordCount = CountWords(text);
        if (wordCount == 0)
        {
            currentHypothesis = null;
            return;
        }

        var firstWord = wordsArray[0];
        var lastWord = wordsArray[wordsArray.GetArrayLength() - 1];
        var startSeconds = firstWord.GetProperty("start").GetDouble();
        var endSeconds = lastWord.GetProperty("end").GetDouble();
        var duration = Math.Max(endSeconds - startSeconds, 0.1);

        // Average confidence from word-level data.
        double totalConf = 0;
        int confCount = 0;
        foreach (var word in wordsArray.EnumerateArray())
        {
            if (word.TryGetProperty("conf", out var conf))
            {
                totalConf += conf.GetDouble();
                confCount++;
            }
        }

        var avgConfidence = confCount > 0 ? totalConf / confCount : 0.8;

        var now = DateTimeOffset.UtcNow;
        var segment = new TranscriptSegment(
            now - TimeSpan.FromSeconds(duration),
            now,
            wordCount,
            avgConfidence,
            false,
            text);

        finalSegments.Enqueue(segment);
        currentHypothesis = null;
        LatestConfidence = avgConfidence;
        TrimSegments(now);
    }

    private void ProcessPartialResult(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var partial = root.TryGetProperty("partial", out var partialProp)
            ? partialProp.GetString() ?? ""
            : "";

        if (string.IsNullOrWhiteSpace(partial))
        {
            return;
        }

        var wordCount = CountWords(partial);
        if (wordCount == 0)
        {
            return;
        }

        // Try to get timing from partial_result word array.
        double durationSeconds;
        double confidence = 0.75;

        if (root.TryGetProperty("partial_result", out var partialWords) && partialWords.GetArrayLength() > 0)
        {
            var firstWord = partialWords[0];
            var lastWord = partialWords[partialWords.GetArrayLength() - 1];
            var startSeconds = firstWord.GetProperty("start").GetDouble();
            var endSeconds = lastWord.GetProperty("end").GetDouble();
            durationSeconds = Math.Max(endSeconds - startSeconds, 0.3);

            double totalConf = 0;
            int confCount = 0;
            foreach (var word in partialWords.EnumerateArray())
            {
                if (word.TryGetProperty("conf", out var conf))
                {
                    totalConf += conf.GetDouble();
                    confCount++;
                }
            }

            if (confCount > 0)
            {
                confidence = totalConf / confCount;
            }
        }
        else
        {
            durationSeconds = Math.Max(wordCount * 0.35, 0.3);
        }

        var now = DateTimeOffset.UtcNow;
        currentHypothesis = new TranscriptSegment(
            now - TimeSpan.FromSeconds(durationSeconds),
            now,
            wordCount,
            confidence,
            true,
            partial);

        LatestConfidence = confidence;
    }

    private WindowMetric CalculateWindowMetrics(
        DateTimeOffset now,
        TimeSpan window,
        bool includeHypothesis,
        double hypothesisWeight,
        int minimumWords,
        double minimumSpeechSeconds)
    {
        var threshold = now - window;
        double effectiveWords = 0;
        double effectiveSpeechSeconds = 0;
        double observedWords = 0;
        double observedSpeechSeconds = 0;
        DateTimeOffset? earliestSpeechStart = null;
        DateTimeOffset? mostRecentSpeechEnd = null;

        foreach (var segment in finalSegments)
        {
            var overlapSeconds = GetOverlapSeconds(segment, threshold, now);
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
            var effectiveStart = segment.StartAt > threshold ? segment.StartAt : threshold;
            earliestSpeechStart = !earliestSpeechStart.HasValue || effectiveStart < earliestSpeechStart
                ? effectiveStart
                : earliestSpeechStart;
            mostRecentSpeechEnd = !mostRecentSpeechEnd.HasValue || segment.EndAt > mostRecentSpeechEnd
                ? segment.EndAt
                : mostRecentSpeechEnd;
        }

        if (includeHypothesis
            && currentHypothesis is not null
            && (now - currentHypothesis.EndAt) <= HypothesisGracePeriod)
        {
            var overlapSeconds = GetOverlapSeconds(currentHypothesis, threshold, now);
            if (overlapSeconds > 0)
            {
                var overlapRatio = overlapSeconds / Math.Max(currentHypothesis.DurationSeconds, 0.01);
                var observedHypothesisWords = currentHypothesis.WordCount * overlapRatio;
                var confidenceWeight = Math.Clamp(0.78 + (currentHypothesis.Confidence * 0.12), 0.78, 0.94);
                observedWords += observedHypothesisWords;
                observedSpeechSeconds += overlapSeconds;
                effectiveWords += observedHypothesisWords * hypothesisWeight * confidenceWeight;
                effectiveSpeechSeconds += overlapSeconds * hypothesisWeight;
                var effectiveStart = currentHypothesis.StartAt > threshold ? currentHypothesis.StartAt : threshold;
                earliestSpeechStart = !earliestSpeechStart.HasValue || effectiveStart < earliestSpeechStart
                    ? effectiveStart
                    : earliestSpeechStart;
                mostRecentSpeechEnd = !mostRecentSpeechEnd.HasValue || currentHypothesis.EndAt > mostRecentSpeechEnd
                    ? currentHypothesis.EndAt
                    : mostRecentSpeechEnd;
            }
        }

        if (observedWords < minimumWords || observedSpeechSeconds < minimumSpeechSeconds)
        {
            return WindowMetric.Unstable;
        }

        var spanSeconds = earliestSpeechStart.HasValue && mostRecentSpeechEnd.HasValue
            ? Math.Max((mostRecentSpeechEnd.Value - earliestSpeechStart.Value).TotalSeconds, 0.5)
            : effectiveSpeechSeconds;
        var spanWordsPerMinute = effectiveWords * (60d / spanSeconds);
        var windowWordsPerMinute = effectiveWords * (60d / window.TotalSeconds);

        var speechFill = observedSpeechSeconds / window.TotalSeconds;
        var spanWeight = Math.Clamp(speechFill + 0.3, 0.5, 0.92);
        var wordsPerMinute = (spanWordsPerMinute * spanWeight) + (windowWordsPerMinute * (1 - spanWeight));

        if (includeHypothesis && mostRecentSpeechEnd is DateTimeOffset mostRecentEnd)
        {
            var silenceSeconds = (now - mostRecentEnd).TotalSeconds;
            if (silenceSeconds > 1.5)
            {
                var fadeSeconds = Math.Max(2.0, window.TotalSeconds * 0.4);
                var decay = 1 - Math.Clamp((silenceSeconds - 1.5) / fadeSeconds, 0, 1);
                wordsPerMinute *= decay;
            }
        }

        return new WindowMetric(Math.Clamp(wordsPerMinute, 0, MaximumPlausibleWordsPerMinute), true);
    }

    private void TrimSegments(DateTimeOffset now)
    {
        while (finalSegments.Count > 0 && (now - finalSegments.Peek().EndAt) > SegmentRetention)
        {
            finalSegments.Dequeue();
        }
    }

    private void ResetTracking()
    {
        DisposeRecognizerLocked();
        finalSegments.Clear();
        currentHypothesis = null;
        LatestConfidence = null;
        streamPositionSeconds = 0;
        resampleFraction = 0;
    }

    private void DisposeRecognizerLocked()
    {
        recognizer?.Dispose();
        recognizer = null;

        model?.Dispose();
        model = null;

        isLiveSession = false;
    }

    /// <summary>
    /// Resample float mono samples from the device sample rate to 16 kHz 16-bit PCM bytes.
    /// Uses linear interpolation to avoid aliasing.
    /// </summary>
    private byte[] ResampleTo16kPcm(float[] samples, int sourceSampleRate)
    {
        if (sourceSampleRate == VoskSampleRate)
        {
            // No resampling needed — just convert float to 16-bit PCM.
            var pcm = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                var clamped = Math.Clamp(samples[i], -1f, 1f);
                var value = (short)(clamped * 32767);
                pcm[i * 2] = (byte)(value & 0xFF);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            return pcm;
        }

        var ratio = (double)sourceSampleRate / VoskSampleRate;
        var outputLength = (int)((samples.Length + resampleFraction) / ratio);
        if (outputLength <= 0)
        {
            resampleFraction += samples.Length;
            return [];
        }

        var pcmBytes = new byte[outputLength * 2];
        for (var i = 0; i < outputLength; i++)
        {
            var srcPos = (i * ratio) + resampleFraction;
            var srcIndex = (int)srcPos;
            var frac = srcPos - srcIndex;

            float sample;
            if (srcIndex + 1 < samples.Length)
            {
                sample = (float)(samples[srcIndex] * (1 - frac) + samples[srcIndex + 1] * frac);
            }
            else if (srcIndex < samples.Length)
            {
                sample = samples[srcIndex];
            }
            else
            {
                break;
            }

            var clamped = Math.Clamp(sample, -1f, 1f);
            var value = (short)(clamped * 32767);
            pcmBytes[i * 2] = (byte)(value & 0xFF);
            pcmBytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        resampleFraction = ((outputLength * ratio) + resampleFraction) - samples.Length;
        if (resampleFraction < 0)
        {
            resampleFraction = 0;
        }

        return pcmBytes;
    }

    private static string EnsureModelAvailable()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var modelDir = Path.Combine(appDataPath, "PaceApp", ModelDirectoryName);

        if (Directory.Exists(modelDir) && File.Exists(Path.Combine(modelDir, "conf", "model.conf")))
        {
            return modelDir;
        }

        var parentDir = Path.Combine(appDataPath, "PaceApp");
        Directory.CreateDirectory(parentDir);

        var zipPath = Path.Combine(parentDir, $"{ModelDirectoryName}.zip");

        using (var httpClient = new HttpClient())
        {
            httpClient.Timeout = TimeSpan.FromMinutes(5);
            using var response = httpClient.GetAsync(ModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var fileStream = File.Create(zipPath);
            response.Content.CopyToAsync(fileStream).GetAwaiter().GetResult();
        }

        ZipFile.ExtractToDirectory(zipPath, parentDir, overwriteFiles: true);

        try
        {
            File.Delete(zipPath);
        }
        catch
        {
            // Non-critical cleanup.
        }

        if (!Directory.Exists(modelDir))
        {
            throw new InvalidOperationException($"Vosk model directory not found after extraction: {modelDir}");
        }

        return modelDir;
    }

    private static double GetOverlapSeconds(TranscriptSegment segment, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var overlapStart = segment.StartAt > windowStart ? segment.StartAt : windowStart;
        var overlapEnd = segment.EndAt < windowEnd ? segment.EndAt : windowEnd;
        return overlapEnd <= overlapStart ? 0 : (overlapEnd - overlapStart).TotalSeconds;
    }

    private static int CountWords(string text) => WordRegex.Matches(text).Count;

    private sealed record TranscriptSegment(
        DateTimeOffset StartAt,
        DateTimeOffset EndAt,
        int WordCount,
        double Confidence,
        bool IsHypothesis,
        string Text)
    {
        public double DurationSeconds => Math.Max((EndAt - StartAt).TotalSeconds, 0.01);
    }

    private readonly record struct WindowMetric(double WordsPerMinute, bool HasStableEstimate)
    {
        public static WindowMetric Unstable => new(0, false);
    }
}
