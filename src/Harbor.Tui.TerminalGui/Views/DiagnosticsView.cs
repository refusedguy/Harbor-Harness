using System.Text;
using Harbor.Ui.Framework.Diagnostics;
using Microsoft.Extensions.Logging;
namespace Harbor.Tui.TerminalGui.Views;
/// <summary>
///     Stateless formatter that projects the shared
///     <see cref="IDiagnosticsPanel" /> ring buffer into a single
///     multi-line string suitable for assignment to a Terminal.Gui
///     <c>TextView.Text</c>. Invoked every dirty frame by
///     <c>TerminalGuiScreen.ApplySnapshot</c> when the F12 diagnostics
///     overlay is visible.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a separate view:</b> the inline overlay rendering used to
///         live as a 20-line block inside
///         <c>TerminalGuiScreen.ApplySnapshot</c>. Extracting it into its own
///         view class keeps the screen class focused on agent-event routing
///         and makes the formatting unit-testable in isolation (no Terminal.Gui
///         <c>Application</c> / <c>TextView</c> needed).
///     </para>
///     <para>
///         <b>Color policy:</b> Terminal.Gui v2 <c>TextView</c> is plain text,
///         so per-level color is NOT applied here — every entry is plain text.
///         The level is encoded in a 4-char prefix (<c>TRAC</c>, <c>DBUG</c>,
///         <c>INFO</c>, <c>WARN</c>, <c>ERRO</c>, <c>CRIT</c>) so the user can
///         still scan severity at a glance. (The SpectreTUI <c>LogsPanel</c>
///         and the Termina <c>DiagnosticsView</c> apply color because their
///         renderers support per-line color.)
///     </para>
///     <para>
///         <b>Auto-scroll:</b> the caller assigns the returned string to
///         <c>TextView.Text</c> and then calls <c>MoveEnd()</c> so the most-
///         recent entry is always at the bottom of the visible region.
///     </para>
/// </remarks>
public sealed class DiagnosticsView
{
    /// <summary>
    ///     Format the most-recent <paramref name="max" /> log entries from
    ///     <paramref name="panel" /> as a single multi-line string suitable
    ///     for <c>TextView.Text</c>. Each entry occupies one line, terminated
    ///     by <c>\n</c>. Newest entry is the last line.
    /// </summary>
    /// <param name="panel">The shared diagnostics panel. Must not be <see langword="null" />.</param>
    /// <param name="max">Maximum entries to format (defaults to 10).</param>
    /// <returns>
    ///     A non-null string. Empty when the panel has no entries; otherwise
    ///     one line per entry, oldest-first.
    /// </returns>
    public string Render(IDiagnosticsPanel panel, int max = 10)
    {
        if (panel is null)
            throw new ArgumentNullException(nameof(panel));

        var entries = panel.GetRecent(max);
        if (entries.Count == 0)
            return string.Empty;

        var sb = new StringBuilder(entries.Count * 80);
        foreach (var e in entries)
        {
            string tag = LevelTag(e.Level);
            string time = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            string cat = ShortenCategory(e.Category);
            string msg = CollapseWhitespace(e.Message);
            sb.Append(time).Append(' ').Append(tag).Append(' ')
                .Append(cat).Append(": ").Append(msg).Append('\n');
        }

        return sb.ToString();
    }

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
