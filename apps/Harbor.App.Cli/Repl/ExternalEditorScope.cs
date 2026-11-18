using System.Diagnostics;
using System.Linq;
using System.Text;
using Harbor.Tui.CellForge.Input;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Suspend/restore scope around an external editor (<c>$VISUAL</c>, then
///     <c>$EDITOR</c>, then <see cref="FallbackEditor"/>). The cell renderer owns
///     the terminal while it runs, so the child gets the tty only after the
///     raw-mode controller is restored and the alternate screen is left. Both
///     halves live in <see cref="RunAsync"/>'s try/finally: a child that throws
///     cannot leave the terminal suspended and the renderer unrenderable.
/// </summary>
/// <remarks>
///     Editor values are never logged or echoed. A command line can carry a
///     token in a flag, so the only value that leaves this scope is the
///     child's exit code. The injected seams exist to test selection and
///     ordering without spawning a process or touching a real tty.
/// </remarks>
internal sealed class ExternalEditorScope
{
    /// <summary>Editor used when neither <c>$VISUAL</c> nor <c>$EDITOR</c> names one.</summary>
    internal const string FallbackEditor = "vi";

    private const string VisualVariable = "VISUAL";
    private const string EditorVariable = "EDITOR";

    private readonly Func<string?, string?> _getEnv;
    private readonly Func<bool> _stdinRedirected;
    private readonly ITerminalModeController _mode;
    private readonly Func<string, int?> _launch;
    private readonly Func<CancellationToken, ValueTask> _leaveScreen;
    private readonly Func<CancellationToken, ValueTask> _enterScreenAndRepaint;

    internal ExternalEditorScope(
        ITerminalModeController mode,
        Func<CancellationToken, ValueTask> leaveScreen,
        Func<CancellationToken, ValueTask> enterScreenAndRepaint,
        Func<string?, string?>? getEnv = null,
        Func<bool>? stdinRedirected = null,
        Func<string, int?>? launch = null)
    {
        _mode = mode;
        _leaveScreen = leaveScreen;
        _enterScreenAndRepaint = enterScreenAndRepaint;
        _getEnv = getEnv ?? (name => Environment.GetEnvironmentVariable(name!));
        _stdinRedirected = stdinRedirected ?? (() => Console.IsInputRedirected);
        _launch = launch ?? LaunchProcess;
    }

    /// <summary>
    ///     Pure editor selection: <c>$VISUAL</c> first, then <c>$EDITOR</c>,
    ///     then <see cref="FallbackEditor"/>. Blank counts as unset. The
    ///     accessor is injected so a unit test substitutes the variables
    ///     without mutating the process environment.
    /// </summary>
    internal static string ResolveEditor(Func<string?, string?> getEnv)
    {
        string? visual = Trimmed(getEnv(VisualVariable));
        if (visual is not null)
        {
            return visual;
        }

        string? editor = Trimmed(getEnv(EditorVariable));
        return editor ?? FallbackEditor;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    ///     Hands the terminal to the editor, then restores raw mode and a
    ///     full repaint in <c>finally</c>. Returns the child exit code, or
    ///     <see langword="null"/> when stdin is redirected (a hand-over
    ///     cannot be done safely) or the child could not be started. The
    ///     caller reports that case without printing the editor value.
    /// </summary>
    internal async Task<int?> RunAsync(CancellationToken ct = default)
    {
        if (_stdinRedirected())
        {
            return null;
        }

        string editor = ResolveEditor(_getEnv);
        try
        {
            await _leaveScreen(ct).ConfigureAwait(false);
            _mode.Restore();
            return _launch(editor);
        }
        finally
        {
            _mode.Enter();
            await _enterScreenAndRepaint(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Real child launch: inherited stdio so the editor owns the tty.
    ///     A failed start returns <see langword="null"/> rather than
    ///     throwing a message that might echo the command (and its secrets).
    /// </summary>
    private static int? LaunchProcess(string editor)
    {
        (string file, string arguments) = SplitCommand(editor);
        var startInfo = new ProcessStartInfo(file)
        {
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        try
        {
            using Process? child = Process.Start(startInfo);
            if (child is null)
            {
                return null;
            }

            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception message can embed the executable path. It is
            // swallowed on purpose: no env var value reaches a log or a UI line.
            _ = ex;
            return null;
        }
    }

    /// <summary>
    ///     Splits a command into executable + arguments, honouring single and
    ///     double quotes around paths that contain spaces. Quoted tokens are
    ///     re-quoted for <see cref="ProcessStartInfo.Arguments"/>, which parses
    ///     its own quoting on the way to the child.
    /// </summary>
    private static (string File, string Arguments) SplitCommand(string command)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        foreach (char c in command)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    token.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }
            }
            else
            {
                token.Append(c);
            }
        }

        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
        }

        if (tokens.Count == 0)
        {
            return (FallbackEditor, string.Empty);
        }

        string arguments = string.Join(' ', tokens.Skip(1).Select(static t =>
            t.Any(ch => char.IsWhiteSpace(ch)) ? $"\"{t}\"" : t));
        return (tokens[0], arguments);
    }
}
