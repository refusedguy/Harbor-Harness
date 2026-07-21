using Harbor.Ui.Framework.Diagnostics;
using Microsoft.Extensions.Logging;
namespace Harbor.Tui.RazorConsole.Views;
/// <summary>
///     Stateless formatter that projects the shared
///     <see cref="IDiagnosticsPanel" /> ring buffer into a sequence of
///     <see cref="ChatLine" />s for inline display inside the RazorConsole
///     chat transcript. Invoked from <see cref="ChatBridge.DumpDiagnostics" />
///     when the user submits <c>/logs</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>RazorConsole key handling:</b> the RazorConsole component
///         pipeline does not expose a global F12 key hook the way SpectreTUI
///         and Terminal.Gui do — the <c>TextInput</c> Blazor component owns
///         the keystroke stream and only surfaces <c>OnSubmit</c>. The
///         documented escape hatch is therefore <c>/logs</c> (typed in the
///         input box and submitted with Enter). See
///         <c>docs/TUI_FEATURE_GAPS.md</c>.
///     </para>
///     <para>
///         <b>Color policy:</b> the <see cref="ChatLine" /> record carries
///         only text + role; per-entry color is encoded in the role tag
///         (<c>system</c>, <c>error</c>) so the existing <c>ChatTui</c>
///         component's role-color table applies. The 4-char severity prefix
///         (<c>TRAC</c>, <c>DBUG</c>, <c>INFO</c>, <c>WARN</c>, <c>ERRO</c>,
///         <c>CRIT</c>) lets the user scan severity at a glance even without
///         per-entry color.
///     </para>
///     <para>
///         <b>Auto-scroll:</b> the caller (<c>ChatBridge.PushLine</c>)
///         appends each returned line in order, oldest-first, so the most-
///         recent entry lands at the bottom of the visible chat transcript
///         and the <c>ViewHeightScrollable</c> component keeps the tail in
///         view.
///     </para>
/// </remarks>
public sealed class DiagnosticsView
{
    /// <summary>
    ///     Format the most-recent <paramref name="max" /> log entries from
    ///     <paramref name="panel" /> as RazorConsole chat lines, oldest-first.
    /// </summary>
    /// <param name="panel">The shared diagnostics panel. Must not be <see langword="null" />.</param>
    /// <param name="max">Maximum entries to format (defaults to 10).</param>
    /// <returns>
    ///     A header line, the formatted log entries (or an "empty" notice),
    ///     ready to push onto the chat transcript. Never <see langword="null" />.
    /// </returns>
    public IReadOnlyList<ChatLine> Render(IDiagnosticsPanel panel, int max = 10)
    {
        if (panel is null)
            throw new ArgumentNullException(nameof(panel));

        var entries = panel.GetRecent(max);
        var result = new List<ChatLine>(entries.Count + 2);
        result.Add(new ChatLine(ChatRoles.System, "──── Logs (last 10, /logs to refresh) ────"));

        if (entries.Count == 0)
        {
            result.Add(new ChatLine(ChatRoles.System, "(no log entries yet)"));
            return result;
        }

        foreach (var e in entries)
        {
            string role = e.Level switch
            {
                LogLevel.Error or LogLevel.Critical => ChatRoles.Error,
                LogLevel.Warning => ChatRoles.System,
                _ => ChatRoles.System
            };
            string tag = LevelTag(e.Level);
            string time = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            string cat = ShortenCategory(e.Category);
            string msg = CollapseWhitespace(e.Message);
            result.Add(new ChatLine(role, $"{time} {tag} {cat}: {msg}"));
        }

        return result;
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
