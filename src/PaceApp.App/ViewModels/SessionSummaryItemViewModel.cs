using PaceApp.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PaceApp.App.ViewModels;

public sealed class SessionSummaryItemViewModel
{
    private static readonly Brush CalmBrush = Freeze(new SolidColorBrush(Color.FromRgb(15, 118, 110)));
    private static readonly Brush CautionBrush = Freeze(new SolidColorBrush(Color.FromRgb(245, 158, 11)));
    private static readonly Brush CriticalBrush = Freeze(new SolidColorBrush(Color.FromRgb(239, 68, 68)));

    private readonly SessionSummary summary;

    public SessionSummaryItemViewModel(SessionSummary summary)
    {
        this.summary = summary;
    }

    public Brush AccentBrush => summary.CriticalSeconds > 0
        ? CriticalBrush
        : summary.CautionSeconds > 0
            ? CautionBrush
            : CalmBrush;

    public string Headline => summary.EndedAt.LocalDateTime.ToString("ddd d MMM, HH:mm");

    public string DurationLabel => $"{Math.Max(1, Math.Round((summary.EndedAt - summary.StartedAt).TotalMinutes)):N0} min";

    public string Summary => $"Avg {summary.AverageWordsPerMinute:N0} WPM · Peak {summary.PeakWordsPerMinute:N0} · Clarity {summary.AverageClarityScore:N0}";

    public string Detail => $"{summary.CriticalSeconds:N0}s red · {summary.CautionSeconds:N0}s caution · {summary.PauseRatePerMinute:N1} pauses/min";

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}