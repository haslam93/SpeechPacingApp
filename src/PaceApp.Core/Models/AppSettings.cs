namespace PaceApp.Core.Models;

public sealed class AppSettings
{
    public const double RecommendedTargetWordsPerMinute = 140;
    public const double RecommendedCautionWordsPerMinute = 170;
    public const double RecommendedCriticalWordsPerMinute = 195;
    public const double RecommendedHysteresisWordsPerMinute = 10;

    public bool StartWithWindows { get; set; }

    public bool AlwaysOnTop { get; set; } = true;

    public double TargetWordsPerMinute { get; set; } = RecommendedTargetWordsPerMinute;

    public double CautionWordsPerMinute { get; set; } = RecommendedCautionWordsPerMinute;

    public double CriticalWordsPerMinute { get; set; } = RecommendedCriticalWordsPerMinute;

    public double HysteresisWordsPerMinute { get; set; } = RecommendedHysteresisWordsPerMinute;

    public double MinimumPauseMilliseconds { get; set; } = 320;

    public string ThemeName { get; set; } = "Dark";

    public bool UsesLegacyDefaultPaceBand() =>
        TargetWordsPerMinute == 135
        && CautionWordsPerMinute == 165
        && CriticalWordsPerMinute == 190
        && HysteresisWordsPerMinute == 8;

    public void ApplyRecommendedPaceBand()
    {
        TargetWordsPerMinute = RecommendedTargetWordsPerMinute;
        CautionWordsPerMinute = RecommendedCautionWordsPerMinute;
        CriticalWordsPerMinute = RecommendedCriticalWordsPerMinute;
        HysteresisWordsPerMinute = RecommendedHysteresisWordsPerMinute;
    }

    public AppSettings Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        AlwaysOnTop = AlwaysOnTop,
        TargetWordsPerMinute = TargetWordsPerMinute,
        CautionWordsPerMinute = CautionWordsPerMinute,
        CriticalWordsPerMinute = CriticalWordsPerMinute,
        HysteresisWordsPerMinute = HysteresisWordsPerMinute,
        MinimumPauseMilliseconds = MinimumPauseMilliseconds,
        ThemeName = ThemeName,
    };
}