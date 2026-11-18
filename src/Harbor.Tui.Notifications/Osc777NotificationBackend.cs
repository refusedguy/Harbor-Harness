using System.Text;

namespace Harbor.Tui.Notifications;

/// <summary>
///     Terminal notification through the OSC 777 escape sequence, for
///     headless and SSH sessions where there is no OS notifier but the
///     terminal emulator can still render a notification banner.
/// </summary>
/// <remarks>
///     <para>
///         OSC 777 has the shape <c>ESC ] 777 ; notify ; title ; body BEL</c>.
///         BEL is used as the terminator rather than ST (<c>ESC \</c>) because
///         more emulators accept it for this sequence. The sequence is emitted
///         to the supplied <see cref="TextWriter" />, which is stdout for the
///         CLI; emulators that do not implement OSC 777 ignore it.
///     </para>
///     <para>
///         Notification text frequently comes from tool output (see the
///         <c>ToolErrorNotificationHandler</c> in <see cref="NotificationTuiRenderer" />).
///         A raw ESC or BEL in that text would end the OSC sequence early and
///         let a tool inject an unrelated escape sequence into the terminal,
///         so <see cref="Sanitize" /> replaces every control character and
///         every field separator with a space before the text is written.
///     </para>
/// </remarks>
internal sealed class Osc777NotificationBackend : INotificationBackend
{
    private const string Prefix = "\u001b]777;notify;";
    private const char Terminator = '\u0007';

    private readonly TextWriter _output;

    /// <summary>Create a backend that writes OSC 777 sequences to <paramref name="output" />.</summary>
    /// <param name="output">The terminal output stream (stdout for the CLI).</param>
    public Osc777NotificationBackend(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <inheritdoc />
    public string Name => "OSC 777 (terminal)";

    /// <inheritdoc />
    public void Notify(string title, string body, bool isError, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);

        // isError is a styling hint for OS toasts. OSC 777 has no severity
        // field, so the discriminator stays in the title the handlers already
        // supply ("Harbor — error" vs "Harbor — done") and no extra field is
        // invented here.
        _output.Write(Prefix);
        _output.Write(Sanitize(title));
        _output.Write(';');
        _output.Write(Sanitize(body));
        _output.Write(Terminator);
        _output.Flush();
    }

    /// <summary>
    ///     Neutralize characters that would terminate or inject into an OSC
    ///     sequence: all control characters (including ESC and BEL) and the
    ///     semicolon that separates OSC fields.
    /// </summary>
    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(char.IsControl(c) || c == ';' ? ' ' : c);
        return sb.ToString();
    }
}
