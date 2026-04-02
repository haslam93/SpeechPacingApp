using System.IO;
using System.Diagnostics;

namespace PaceApp.App.Services;

public sealed class AppDiagnosticsService
{
    private readonly object syncRoot = new();
    private readonly string logFilePath;

    public AppDiagnosticsService(string? rootPath = null)
    {
        var basePath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PaceApp");

        Directory.CreateDirectory(basePath);
        logFilePath = Path.Combine(basePath, "diagnostics.log");
    }

    public string LogFilePath => logFilePath;

    public void Write(string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        lock (syncRoot)
        {
            File.AppendAllLines(logFilePath, [line]);
        }
    }

    public IReadOnlyList<string> GetRecentEntries(int maxEntries = 30)
    {
        lock (syncRoot)
        {
            if (!File.Exists(logFilePath))
            {
                return [];
            }

            return File.ReadLines(logFilePath)
                .Reverse()
                .Take(maxEntries)
                .Reverse()
                .ToList();
        }
    }

    public string GetAllText()
    {
        lock (syncRoot)
        {
            return File.Exists(logFilePath)
                ? File.ReadAllText(logFilePath)
                : string.Empty;
        }
    }

    public void OpenLogFolder()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{logFilePath}\"",
            UseShellExecute = true,
        };

        Process.Start(startInfo);
    }
}