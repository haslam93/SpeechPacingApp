using System.Windows;
using PaceApp.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PaceApp.App;

public partial class SessionFeedbackWindow : Window
{
    public SessionFeedbackWindow(SessionSummary summary)
    {
        InitializeComponent();

        var duration = summary.EndedAt - summary.StartedAt;
        var totalSeconds = Math.Max(1, duration.TotalSeconds);
        var alertPercent = (summary.CautionSeconds + summary.CriticalSeconds) / totalSeconds * 100;

        DataContext = new SessionFeedbackViewModel
        {
            GradeEmoji = summary.SessionGrade switch
            {
                "Great" => "🟢",
                "Good" => "🟡",
                "Watch pace" => "🟠",
                _ => "🔴",
            },
            GradeHeadline = summary.SessionGrade switch
            {
                "Great" => "Great session!",
                "Good" => "Good session",
                "Watch pace" => "Watch your pace",
                _ => "Too fast",
            },
            AccentBrush = summary.SessionGrade switch
            {
                "Great" => Freeze(new SolidColorBrush(Color.FromRgb(45, 212, 191))),
                "Good" => Freeze(new SolidColorBrush(Color.FromRgb(251, 191, 36))),
                "Watch pace" => Freeze(new SolidColorBrush(Color.FromRgb(251, 146, 60))),
                _ => Freeze(new SolidColorBrush(Color.FromRgb(248, 113, 113))),
            },
            DurationLine = duration.TotalMinutes >= 1
                ? $"{(int)duration.TotalMinutes} min {duration.Seconds}s session"
                : $"{(int)duration.TotalSeconds}s session",
            AvgWpmText = $"{summary.AverageWordsPerMinute:N0}",
            PeakWpmText = $"{summary.PeakWordsPerMinute:N0}",
            CautionText = $"{summary.CautionSeconds:N0}s",
            CriticalText = $"{summary.CriticalSeconds:N0}s",
            Advice = summary.SessionGrade switch
            {
                "Great" => "Your pace was steady and clear. Keep it up!",
                "Good" => "Mostly solid — a few moments crept up but nothing major.",
                "Watch pace" => "You spent a fair chunk above the comfort zone. Try pausing between points.",
                _ => "Most of the session was too fast. Slow down and leave deliberate gaps.",
            },
        };
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private sealed class SessionFeedbackViewModel
    {
        public string GradeEmoji { get; init; } = "";
        public string GradeHeadline { get; init; } = "";
        public Brush AccentBrush { get; init; } = null!;
        public string DurationLine { get; init; } = "";
        public string AvgWpmText { get; init; } = "";
        public string PeakWpmText { get; init; } = "";
        public string CautionText { get; init; } = "";
        public string CriticalText { get; init; } = "";
        public string Advice { get; init; } = "";
    }
}
