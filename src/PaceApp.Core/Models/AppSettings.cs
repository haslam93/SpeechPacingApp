namespace PaceApp.Core.Models;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }

    public bool AlwaysOnTop { get; set; } = true;

    public double TargetWordsPerMinute { get; set; } = 135;

    public double CautionWordsPerMinute { get; set; } = 165;

    public double CriticalWordsPerMinute { get; set; } = 190;

    public double HysteresisWordsPerMinute { get; set; } = 8;

    public double MinimumPauseMilliseconds { get; set; } = 320;

    public AppSettings Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        AlwaysOnTop = AlwaysOnTop,
        TargetWordsPerMinute = TargetWordsPerMinute,
        CautionWordsPerMinute = CautionWordsPerMinute,
        CriticalWordsPerMinute = CriticalWordsPerMinute,
        HysteresisWordsPerMinute = HysteresisWordsPerMinute,
        MinimumPauseMilliseconds = MinimumPauseMilliseconds,
    };
}