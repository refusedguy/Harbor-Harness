using System.Globalization;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Retry countdown (killer features §P6.3): renders the «retrying tool call»
/// status line — attempt fraction, seconds until the next attempt, and a
/// proportional braille-block progress bar. Pure formatter; the host owns
/// the timer and feeds remaining seconds.
/// </summary>
public static class RetryCountdown
{
    private const string BarFill = "█";
    private const string BarTrack = "░";

    /// <summary>One-line retry status: «retry 2/5 in 4s» (no bar).</summary>
    public static string Line(int attempt, int maxAttempts, int secondsRemaining) =>
        $"retry {Math.Max(1, attempt)}/{maxAttempts} in {Math.Max(0, secondsRemaining)}s";

    /// <summary>
    /// Status segments for the status bar: the retry line plus an optional
    /// trailing bar segment (when <paramref name="barWidth" /> ≥ 3). The bar
    /// depletes left-to-right as the countdown burns down.
    /// </summary>
    public static (string Line, string Bar) Segments(int attempt, int maxAttempts, int secondsRemaining, int totalSeconds, int barWidth)
    {
        string line = Line(attempt, maxAttempts, secondsRemaining);
        if (barWidth < 3)
        {
            return (line, string.Empty);
        }

        int total = Math.Max(1, totalSeconds);
        int remaining = Math.Clamp(secondsRemaining, 0, total);
        int fill = (int)Math.Round(barWidth * remaining / (double)total, MidpointRounding.AwayFromZero);
        fill = Math.Clamp(fill, 0, barWidth);

        return (line, Bar(fill, barWidth));
    }

    /// <summary>Progress bar — <paramref name="fill" /> filled cells of <paramref name="width" />.</summary>
    public static string Bar(int fill, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        int clamped = Math.Clamp(fill, 0, width);
        return string.Create(width, (clamped, width), static (span, state) =>
        {
            var (f, w) = state;
            int i = 0;
            for (; i < f; i++)
            {
                span[i] = '█';
            }

            for (; i < w; i++)
            {
                span[i] = '░';
            }
        });
    }

    /// <summary>Exponential backoff delay for attempt n (1-based): base·2^(n−1), capped.</summary>
    public static int BackoffSeconds(int attempt, int baseSeconds = 1, int maxSeconds = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        long delay = baseSeconds;
        for (int i = 1; i < attempt; i++)
        {
            delay = Math.Min(delay * 2, maxSeconds);
        }

        return (int)Math.Min(delay, maxSeconds);
    }

    /// <summary>Culture-invariant seconds suffix helper for custom hosts.</summary>
    public static string Seconds(int seconds) =>
        Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture) + "s";
}
