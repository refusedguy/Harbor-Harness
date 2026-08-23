using Harbor.Ui.Framework.Diagnostics;
using Microsoft.Extensions.Logging;
namespace Harbor.Tui.Termina.Views;

/// <summary>
///     Projects the shared <see cref="IDiagnosticsPanel" /> ring buffer into
///     plain text lines for inline display inside the Termina chat stream.
/// </summary>
public sealed class DiagnosticsView
{
    /// <summary>
    ///     Format the most-recent <paramref name="max" /> log entries from
    ///     <paramref name="panel" /> as plain text lines, oldest-first.
    /// </summary>
    public IReadOnlyList<string> Render(IDiagnosticsPanel panel, int max = 10)
    {
        if (panel is null)
            throw new ArgumentNullException(nameof(panel));

        var entries = panel.GetRecent(max);
        var result = new List<string>(entries.Count + 2);
        result.Add("──── Logs (last 10, F12 to refresh) ────");

        if (entries.Count == 0)
        {
            result.Add("(no log entries yet)");
            return result;
        }

        foreach (var e in entries)
        {
            string tag = LevelTag(e.Level);
            string time = e.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            string cat = ShortenCategory(e.Category);
            string msg = CollapseWhitespace(e.Message);
            result.Add($"{time} {tag} {cat}: {msg}");
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
