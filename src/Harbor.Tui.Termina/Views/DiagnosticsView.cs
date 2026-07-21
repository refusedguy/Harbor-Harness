using Harbor.Ui.Framework.Diagnostics;
using Microsoft.Extensions.Logging;
using Termina.Terminal;
namespace Harbor.Tui.Termina.Views;
/// <summary>
///     Projects the shared <see cref="IDiagnosticsPanel" /> ring buffer into a
///     sequence of Termina <see cref="ChatLine" />s for inline display inside
///     the chat stream. Invoked from <see cref="ChatBridge.DumpDiagnostics" />
///     when the user presses <c>F12</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a separate view:</b> the F12 diagnostics surface used to live
///         inline in <c>ChatBridge.DumpDiagnostics</c> as a 30-line block of
///         formatting code. Extracting it into its own view class keeps the
///         bridge focused on agent-event routing and makes the formatting
///         unit-testable in isolation.
///     </para>
///     <para>
///         <b>Color policy:</b> Trace/Debug=grey, Information=white,
///         Warning=yellow, Error/Critical=red — matches the SpectreTUI
///         <c>LogsPanel</c> and the Terminal.Gui overlay so the user sees a
///         consistent log palette across renderers.
///     </para>
///     <para>
///         <b>Auto-scroll:</b> the caller pushes the returned lines in order,
///         oldest-first, so the most-recent entry lands at the bottom of the
///         visible chat stream.
///     </para>
/// </remarks>
public sealed class DiagnosticsView
{
    /// <summary>
    ///     Format the most-recent <paramref name="max" /> log entries from
    ///     <paramref name="panel" /> as Termina chat lines, oldest-first.
    /// </summary>
    /// <param name="panel">The shared diagnostics panel. Must not be <see langword="null" />.</param>
    /// <param name="max">Maximum entries to format (defaults to 10).</param>
    /// <returns>
    ///     A header line, the formatted log entries (or an "empty" notice),
    ///     ready to push onto the chat output stream. Never <see langword="null" />.
    /// </returns>
    public IReadOnlyList<ChatLine> Render(IDiagnosticsPanel panel, int max = 10)
    {
        if (panel is null)
            throw new ArgumentNullException(nameof(panel));

        var entries = panel.GetRecent(max);
        var result = new List<ChatLine>(entries.Count + 2);
        result.Add(new ChatLine("──── Logs (last 10, F12 to refresh) ────", Color.Cyan, true));

        if (entries.Count == 0)
        {
            result.Add(new ChatLine("(no log entries yet)", Color.DarkGray));
            return result;
        }

        foreach (var e in entries)
        {
            var color = LevelColor(e.Level);
            string tag = LevelTag(e.Level);
            string time = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            string cat = ShortenCategory(e.Category);
            string msg = CollapseWhitespace(e.Message);
            result.Add(new ChatLine($"{time} {tag} {cat}: {msg}", color));
        }

        return result;
    }

    private static Color LevelColor(LogLevel level) => level switch
    {
        LogLevel.Trace => Color.DarkGray,
        LogLevel.Debug => Color.DarkGray,
        LogLevel.Information => Color.White,
        LogLevel.Warning => Color.Yellow,
        LogLevel.Error => Color.Red,
        LogLevel.Critical => Color.Red,
        _ => Color.Gray
    };

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRAC",
        LogLevel.Debug => "DBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERRO",
        LogLevel.Critical => "CRIT",
        _ => "????"
    };

    private static string ShortenCategory(string category)
    {
        if (string.IsNullOrEmpty(category))
            return "-";
        int lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1
            ? category[(lastDot + 1)..]
            : category;
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return text.Replace("\r", " ").Replace("\n", " ");
    }
}
