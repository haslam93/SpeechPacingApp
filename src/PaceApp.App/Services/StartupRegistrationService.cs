using Microsoft.Win32;

namespace PaceApp.App.Services;

public sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PaceCoach";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string currentValue && !string.IsNullOrWhiteSpace(currentValue);
    }

    public void SetEnabled(bool isEnabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            return;
        }

        if (!isEnabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return;
        }

        key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
    }
}