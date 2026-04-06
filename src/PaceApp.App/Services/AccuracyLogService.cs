using System.IO;
using System.Text;
using PaceApp.Core.Models;

namespace PaceApp.App.Services;

public sealed class AccuracyLogService
{
    private readonly object syncRoot = new();
    private readonly string logDirectory;
    private StreamWriter? writer;
    private string? currentLogPath;
    private string lastRecognizedText = string.Empty;

    public AccuracyLogService(string? rootPath = null)
    {
        logDirectory = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PaceApp",
            "accuracy-logs");
    }

    public bool IsRecording { get; private set; }

    public string? CurrentLogPath => currentLogPath;

    public void Start()
    {
        lock (syncRoot)
        {
            if (IsRecording)
            {
                return;
            }

            Directory.CreateDirectory(logDirectory);
            currentLogPath = Path.Combine(logDirectory, $"accuracy-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
            writer = new StreamWriter(currentLogPath, append: false, Encoding.UTF8);
            writer.WriteLine("timestamp,elapsed_seconds,displayed_wpm,rolling_wpm,transcript_wpm,alert_level,recognized_text");
            writer.Flush();
            IsRecording = true;
            lastRecognizedText = string.Empty;
        }
    }

    public void RecordSnapshot(LivePaceSnapshot snapshot, DateTimeOffset sessionStart, double transcriptCurrentWpm)
    {
        lock (syncRoot)
        {
            if (!IsRecording || writer is null)
            {
                return;
            }

            var text = snapshot.RecognizedText;
            var textChanged = !string.Equals(text, lastRecognizedText, StringComparison.Ordinal);
            lastRecognizedText = text;

            // Only write a row when the recognized text changes or the alert level is not Calm.
            // This keeps the file size reasonable while capturing all interesting moments.
            if (!textChanged && snapshot.AlertLevel == PaceAlertLevel.Calm && snapshot.EstimatedWordsPerMinute < 20)
            {
                return;
            }

            var elapsed = (snapshot.Timestamp - sessionStart).TotalSeconds;
            var escapedText = EscapeCsvField(text);

            writer.WriteLine(
                $"{snapshot.Timestamp:O},{elapsed:F1},{snapshot.EstimatedWordsPerMinute:F0},{snapshot.RollingAverageWordsPerMinute:F0},{transcriptCurrentWpm:F0},{snapshot.AlertLevel},{escapedText}");
            writer.Flush();
        }
    }

    public void Stop()
    {
        lock (syncRoot)
        {
            IsRecording = false;
            writer?.Dispose();
            writer = null;
        }
    }

    public string? GetLatestLogPath()
    {
        lock (syncRoot)
        {
            return currentLogPath;
        }
    }

    private static string EscapeCsvField(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
