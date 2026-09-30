using System.Text;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering;
using Harbor.Ui.Framework.Rendering.Widgets;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Modal dialog kind. Drives button layout and default Enter behaviour.
/// <see cref="Select"/>, <see cref="Radio"/> and <see cref="Multiline"/> are
/// the [PRIM4] form kinds (#289): choice lists and the multiline editor hosted
/// in the same centered box (seated on the z-stack since [PRIM2c]).
/// <see cref="Approval"/> is the [UX7] approval modal (#267): the PRIM4 radio
/// (Allow once / Allow for session / Deny) plus the PRIM4 multiline editor as
/// the reject-reason field plus a capped unified-diff preview, composed in one
/// box — no new widgets, only assembly.
/// </summary>
public enum DialogKind
{
    Alert,
    Confirm,
    Prompt,
    /// <summary>Single-column option list (<see cref="DialogSelectList"/>).</summary>
    Select,
    /// <summary>Single-row mutually-exclusive options (<see cref="DialogRadioGroup"/>).</summary>
    Radio,
    /// <summary>Multiline text field (<see cref="DialogMultilineInput"/>).</summary>
    Multiline,
    /// <summary>Approval modal: fixed choice row + reject-reason editor + diff preview.</summary>
    Approval,
}

/// <summary>
/// One button on a <see cref="DialogOverlay"/>.
/// </summary>
public sealed record DialogButton(string Label, string Id);

/// <summary>
/// Cell-native modal dialog overlay (CellForge EPIC H).
/// Hosts a single centered modal box on top of the chat feed.
/// Hosts are responsible for advancing <see cref="Tick"/> for animations
/// (none today; the field is reserved for future spinner integration) and
/// for invoking <see cref="Dismiss"/> after a button commits.
/// </summary>
public sealed class DialogOverlay
{
    public const int MinWidth = 20;
    public const int MaxWidth = 72;
    public const int MinHeight = 5;
    public const int MaxHeight = 18;
    /// <summary>Max option rows a select dialog reserves (the list scrolls past this).</summary>
    public const int MaxSelectRows = 6;
    /// <summary>Max editor rows a multiline dialog reserves (extra lines clip).</summary>
    public const int MaxMultilineRows = 5;
    /// <summary>Max diff preview rows an approval modal reserves (extra lines clip).</summary>
    public const int MaxApprovalDiffRows = 6;
    /// <summary>Max reason rows an approval modal reserves (extra lines clip).</summary>
    public const int MaxApprovalReasonRows = 2;
    private const int Padding = 1;
    private const int ButtonRowHeight = 2;
    private const int SelectPageRows = 5;

    /// <summary>Fixed choice labels of an approval modal (radio order = <see cref="ApprovalChoice"/> order).</summary>
    public static readonly IReadOnlyList<string> ApprovalChoices = ["Allow once", "Allow for session", "Deny"];

    private const string ApprovalReasonCaption = "reject reason (deny only):";

    private readonly List<DialogButton> _buttons = new();
    private readonly DialogSelectList _select = new();
    private readonly DialogRadioGroup _radio = new();
    private readonly DialogMultilineInput _editor = new();
    private readonly List<DiffViewerLine> _approvalDiff = new();
    private string _approvalTool = string.Empty;
    private string _approvalFile = string.Empty;
    private string _title = string.Empty;
    private string _message = string.Empty;
    private string _input = string.Empty;
    private int _focusedButton;
    private DialogKind _kind = DialogKind.Alert;

    public DialogOverlay()
    {
    }

    public bool Visible { get; private set; }

    public string Title => _title;

    public string Message => _message;

    /// <summary>
    /// Text value: the prompt buffer for <see cref="DialogKind.Prompt"/>, the
    /// multiline editor text for <see cref="DialogKind.Multiline"/>.
    /// </summary>
    public string Input => _kind == DialogKind.Multiline ? _editor.Text : _input;

    /// <summary>Choice options for <see cref="DialogKind.Select"/> / <see cref="DialogKind.Radio"/> (empty otherwise).</summary>
    public IReadOnlyList<string> Options =>
        _kind == DialogKind.Select ? _select.Items :
        _kind is DialogKind.Radio or DialogKind.Approval ? _radio.Options :
        Array.Empty<string>();

    /// <summary>Selected choice index (-1 when the kind has no options).</summary>
    public int SelectedIndex =>
        _kind == DialogKind.Select ? _select.SelectedIndex :
        _kind is DialogKind.Radio or DialogKind.Approval ? _radio.SelectedIndex :
        -1;

    /// <summary>Selected choice text (empty when the kind has no options).</summary>
    public string SelectedOption =>
        _kind == DialogKind.Select ? _select.SelectedItem :
        _kind is DialogKind.Radio or DialogKind.Approval ? _radio.SelectedOption :
        string.Empty;

    /// <summary>Multiline editor state (meaningful for <see cref="DialogKind.Multiline"/> and the <see cref="DialogKind.Approval"/> reject-reason field).</summary>
    public DialogMultilineInput Editor => _editor;

    /// <summary>Select-list state (white-box scroll asserts; meaningful for <see cref="DialogKind.Select"/>).</summary>
    internal DialogSelectList SelectList => _select;

    /// <summary>Radio-group state (white-box asserts; meaningful for <see cref="DialogKind.Radio"/>).</summary>
    internal DialogRadioGroup RadioGroup => _radio;

    public IReadOnlyList<DialogButton> Buttons => _buttons;

    public int FocusedButtonIndex => _focusedButton;

    public DialogKind Kind => _kind;

    public void ShowAlert(string title, string message, string okLabel = "OK")
    {
        ArgumentNullException.ThrowIfNull(okLabel);
        _kind = DialogKind.Alert;
        _title = title ?? string.Empty;
        _message = message ?? string.Empty;
        _input = string.Empty;
        _buttons.Clear();
        _buttons.Add(new DialogButton(okLabel, "ok"));
        _focusedButton = 0;
        Visible = true;
    }

    public void ShowConfirm(
        string title,
        string message,
        string okLabel = "OK",
        string cancelLabel = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(okLabel);
        ArgumentNullException.ThrowIfNull(cancelLabel);
        _kind = DialogKind.Confirm;
        _title = title ?? string.Empty;
        _message = message ?? string.Empty;
        _input = string.Empty;
        _buttons.Clear();
        _buttons.Add(new DialogButton(okLabel, "ok"));
        _buttons.Add(new DialogButton(cancelLabel, "cancel"));
        _focusedButton = 0;
        Visible = true;
    }

    public void ShowPrompt(
        string title,
        string message,
        string defaultValue = "",
        string okLabel = "OK",
        string cancelLabel = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(okLabel);
        ArgumentNullException.ThrowIfNull(cancelLabel);
        _kind = DialogKind.Prompt;
        _title = title ?? string.Empty;
        _message = message ?? string.Empty;
        _input = defaultValue ?? string.Empty;
        _buttons.Clear();
        _buttons.Add(new DialogButton(okLabel, "ok"));
        _buttons.Add(new DialogButton(cancelLabel, "cancel"));
        _focusedButton = 0;
        Visible = true;
    }

    /// <summary>
    /// Shows an option-list dialog ([PRIM4]). Enter commits via the host (same
    /// contract as <see cref="DialogKind.Prompt"/>); the host reads
    /// <see cref="SelectedOption"/> / <see cref="SelectedIndex"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">When <paramref name="options"/> or a label is null.</exception>
    /// <exception cref="ArgumentException">When <paramref name="options"/> is empty.</exception>
    public void ShowSelect(
        string title,
        string message,
        IReadOnlyList<string> options,
        int selectedIndex = 0,
        string okLabel = "OK",
        string cancelLabel = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(okLabel);
        ArgumentNullException.ThrowIfNull(cancelLabel);
        if (options.Count == 0)
        {
            throw new ArgumentException("Select dialog needs at least one option.", nameof(options));
        }
        _kind = DialogKind.Select;
        _title = title ?? string.Empty;
        _message = message ?? string.Empty;
        _input = string.Empty;
        _select.SetItems(options);
        _select.MoveTo(selectedIndex);
        _buttons.Clear();
        _buttons.Add(new DialogButton(okLabel, "ok"));
        _buttons.Add(new DialogButton(cancelLabel, "cancel"));
        _focusedButton = 0;
        Visible = true;
    }

    /// <summary>
    /// Shows a radio-row dialog ([PRIM4]). Enter commits via the host; the host
    /// reads <see cref="SelectedOption"/> / <see cref="SelectedIndex"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">When <paramref name="options"/> or a label is null.</exception>
    /// <exception cref="ArgumentException">When <paramref name="options"/> is empty.</exception>
    public void ShowRadio(
        string title,
        string message,
        IReadOnlyList<string> options,
        int selectedIndex = 0,
        string okLabel = "OK",
        string cancelLabel = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(okLabel);
        ArgumentNullException.ThrowIfNull(cancelLabel);
        if (options.Count == 0)
        {
            throw new ArgumentException("Radio dialog needs at least one option.", nameof(options));
        }
        _kind = DialogKind.Radio;
        _title = title ?? string.Empty;
        _message = message ?? string.Empty;
        _input = string.Empty;
        _radio.SetOptions(options, selectedIndex);
        _buttons.Clear();
        _buttons.Add(new DialogButton(okLabel, "ok"));
        _buttons.Add(new DialogButton(cancelLabel, "cancel"));
        _focusedButton = 0;
        Visible = true;
    }

    /// <summary>
    /// Shows a multiline editor dialog ([PRIM4]). The editor is the shared
    /// <see cref="DialogMultilineInput"/> (<see cref="PromptBuffer"/> engine);
    /// plain Enter still submits (uniform dialog contract — the host commits
    /// and reads <see cref="Input"/>); newlines come from multiline prefill,
    /// paste, or kitty Shift+Enter (see the <c>KeyEvent</c> overload).
    /// </summary>
    /// <exception cref="ArgumentNullException">When a label is null.</exception>
    public void ShowMultiline(
        string title,
        string message,
        string defaultValue = "",
        string okLabel = "OK",
        string cancelLabel = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(okLabel);
        ArgumentNullException.ThrowIfNull(cancelLabel);
        _kind = DialogKind.Multiline;
        _title = title ?? string.Empty;
        _message = message ?? string.Empty;
        _input = string.Empty;
        _editor.SetText(defaultValue ?? string.Empty);
        _buttons.Clear();
        _buttons.Add(new DialogButton(okLabel, "ok"));
        _buttons.Add(new DialogButton(cancelLabel, "cancel"));
        _focusedButton = 0;
        Visible = true;
    }

    /// <summary>
    /// Shows an approval modal ([UX7] #267): the fixed PRIM4 radio choice row
    /// (<see cref="ApprovalChoices"/>) plus the PRIM4 multiline editor as the
    /// reject-reason field plus a capped unified-diff preview — one centered
    /// box on the PRIM2c z-stack, no new widgets.
    /// Enter commits via the host (same contract as <see cref="DialogKind.Prompt"/>);
    /// the host reads <see cref="SelectedApproval"/> and <see cref="RejectReason"/>.
    /// Escape dismisses (the host denies fail-closed).
    /// </summary>
    /// <param name="toolName">Tool requesting approval (header + audit; blank coerces to "?").</param>
    /// <param name="detail">One-line request summary (rule pattern + args).</param>
    /// <param name="diffText">Optional unified diff; prefix-classified and capped at <see cref="MaxApprovalDiffRows"/> rows (null/blank hides the section).</param>
    /// <param name="filePath">Optional display path shown above the diff (ignored without <paramref name="diffText"/>).</param>
    /// <param name="selectedIndex">Initial choice (clamped; 0 = allow once).</param>
    /// <param name="okLabel">Confirm button label.</param>
    /// <param name="cancelLabel">Cancel button label.</param>
    /// <exception cref="ArgumentNullException">When a label is null.</exception>
    public void ShowApproval(
        string? toolName,
        string? detail,
        string? diffText = null,
        string? filePath = null,
        int selectedIndex = 0,
        string okLabel = "Confirm",
        string cancelLabel = "Cancel")
    {
        ArgumentNullException.ThrowIfNull(okLabel);
        ArgumentNullException.ThrowIfNull(cancelLabel);
        _kind = DialogKind.Approval;
        _approvalTool = string.IsNullOrWhiteSpace(toolName) ? "?" : toolName.Trim();
        _title = $"permission required · {_approvalTool}";
        _message = detail ?? string.Empty;
        _input = string.Empty;
        _approvalFile = string.IsNullOrWhiteSpace(filePath) ? string.Empty : filePath.Trim();
        ParseApprovalDiff(diffText);
        _radio.SetOptions(ApprovalChoices, selectedIndex);
        _editor.SetText(string.Empty);
        _buttons.Clear();
        _buttons.Add(new DialogButton(okLabel, "ok"));
        _buttons.Add(new DialogButton(cancelLabel, "cancel"));
        _focusedButton = 0;
        Visible = true;
    }

    /// <summary>Tool the visible approval modal asks for (empty unless <see cref="DialogKind.Approval"/>).</summary>
    public string ApprovalTool => _kind == DialogKind.Approval ? _approvalTool : string.Empty;

    /// <summary>Display path above the approval diff preview (empty when none).</summary>
    public string ApprovalFilePath => _kind == DialogKind.Approval ? _approvalFile : string.Empty;

    /// <summary>Parsed, capped diff preview rows (empty when the modal has no diff).</summary>
    public IReadOnlyList<DiffViewerLine> ApprovalDiff => _approvalDiff;

    /// <summary>
    /// Choice the approval modal commits: radio index 0/1/2 maps to
    /// Approve / AlwaysAllow / Deny (out-of-range degrades to Deny, fail closed).
    /// Meaningful only for <see cref="DialogKind.Approval"/>.
    /// </summary>
    public ApprovalChoice SelectedApproval => _radio.SelectedIndex switch
    {
        0 => ApprovalChoice.Approve,
        1 => ApprovalChoice.AlwaysAllow,
        2 => ApprovalChoice.Deny,
        _ => ApprovalChoice.Deny,
    };

    /// <summary>
    /// Reject-reason text (trimmed editor content; empty when the user typed
    /// nothing). The host attaches it to Deny commits; Allow commits ignore it.
    /// </summary>
    public string RejectReason => _kind == DialogKind.Approval ? _editor.Text.Trim() : string.Empty;

    /// <summary>
    /// Classifies <paramref name="diffText"/> into capped preview rows with the
    /// same prefix rules as the fullscreen viewer ([PRIM12]): <c>+</c> added,
    /// <c>-</c> removed, <c>@@</c> headers and <c>\</c> markers as context,
    /// <c>---</c>/<c>+++</c> file-pair preamble skipped, one leading space of
    /// unified context stripped. Empty lines are dropped (preview budget).
    /// </summary>
    private void ParseApprovalDiff(string? diffText)
    {
        _approvalDiff.Clear();
        if (string.IsNullOrWhiteSpace(diffText))
        {
            return;
        }

        string[] lines = diffText.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length && _approvalDiff.Count < MaxApprovalDiffRows; i++)
        {
            string line = lines[i].Replace("\t", "  ");
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line is "---" or "+++")
            {
                continue;
            }

            DiffViewerLineKind kind;
            string text;
            if (line[0] == '+')
            {
                kind = DiffViewerLineKind.Added;
                text = line[1..];
            }
            else if (line[0] == '-')
            {
                kind = DiffViewerLineKind.Removed;
                text = line[1..];
            }
            else if (line[0] == '\\')
            {
                kind = DiffViewerLineKind.Context;
                text = line;
            }
            else
            {
                kind = DiffViewerLineKind.Context;
                text = line[0] == ' ' ? line[1..] : line;
            }
            _approvalDiff.Add(new DiffViewerLine(kind, text));
        }
    }

    public void Dismiss()
    {
        Visible = false;
    }

    public void Tick()
    {
        // Reserved for future spinner/animation integration.
    }

    /// <summary>
    /// Routes a legacy console key. Escape dismisses, Tab cycles buttons,
    /// Enter submits to the host (returns false — the host commits and reads
    /// <see cref="Input"/> / <see cref="SelectedOption"/>); navigation and
    /// editing are kind-specific. The <see cref="DialogKind.Prompt"/> path is
    /// frozen legacy (arrows cycle buttons); <see cref="DialogKind.Multiline"/>
    /// owns the horizontal arrows as caret moves.
    /// </summary>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        if (!Visible)
        {
            return false;
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                Dismiss();
                return true;
            case ConsoleKey.Tab:
                CycleFocus(forward: true);
                return true;
            case ConsoleKey.LeftArrow:
            case ConsoleKey.RightArrow:
                bool forward = key.Key == ConsoleKey.RightArrow;
                if (_kind is DialogKind.Radio or DialogKind.Approval)
                {
                    _radio.Move(forward ? 1 : -1);
                    return true;
                }
                if (_kind == DialogKind.Multiline)
                {
                    if (forward)
                    {
                        _editor.MoveRight();
                    }
                    else
                    {
                        _editor.MoveLeft();
                    }
                    return true;
                }
                CycleFocus(forward);
                return true;
        }
        return _kind switch
        {
            DialogKind.Prompt => HandlePromptKey(key),
            DialogKind.Select => HandleSelectKey(key),
            DialogKind.Radio => HandleRadioKey(key),
            DialogKind.Approval => HandleApprovalKey(key),
            DialogKind.Multiline => HandleMultilineKey(key),
            _ => false,
        };
    }

    /// <summary>
    /// Routes a decoded key (kitty-capable hosts). Mirrors the
    /// <see cref="ConsoleKeyInfo"/> contract, plus Shift/Alt+Enter inserts a
    /// newline in <see cref="DialogKind.Multiline"/> (the composer Enter split:
    /// Ctrl+Enter submits, Shift/Alt+Enter newline, plain Enter submit).
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (!Visible)
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Escape:
                Dismiss();
                return true;
            case KeyCode.Tab:
                CycleFocus(forward: true);
                return true;
            case KeyCode.Left:
            case KeyCode.Right:
                bool forward = key.Key == KeyCode.Right;
                if (_kind is DialogKind.Radio or DialogKind.Approval)
                {
                    _radio.Move(forward ? 1 : -1);
                    return true;
                }
                if (_kind == DialogKind.Multiline)
                {
                    if (forward)
                    {
                        _editor.MoveRight();
                    }
                    else
                    {
                        _editor.MoveLeft();
                    }
                    return true;
                }
                CycleFocus(forward);
                return true;
            case KeyCode.Enter:
                if ((_kind == DialogKind.Multiline || _kind == DialogKind.Approval)
                    && (key.Modifiers & KeyModifiers.Ctrl) == 0
                    && (key.Modifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) != 0)
                {
                    _editor.InsertNewline();
                    return true;
                }
                return false;
            case KeyCode.Up:
                if (_kind == DialogKind.Select)
                {
                    _select.Move(-1);
                    return true;
                }
                if (_kind is DialogKind.Radio or DialogKind.Approval)
                {
                    _radio.Move(-1);
                    return true;
                }
                if (_kind == DialogKind.Multiline)
                {
                    _editor.MoveUp();
                    return true;
                }
                return false;
            case KeyCode.Down:
                if (_kind == DialogKind.Select)
                {
                    _select.Move(1);
                    return true;
                }
                if (_kind is DialogKind.Radio or DialogKind.Approval)
                {
                    _radio.Move(1);
                    return true;
                }
                if (_kind == DialogKind.Multiline)
                {
                    _editor.MoveDown();
                    return true;
                }
                return false;
            case KeyCode.PageUp:
                if (_kind == DialogKind.Select)
                {
                    _select.Move(-SelectPageRows);
                    return true;
                }
                return false;
            case KeyCode.PageDown:
                if (_kind == DialogKind.Select)
                {
                    _select.Move(SelectPageRows);
                    return true;
                }
                return false;
            case KeyCode.Home:
                if (_kind == DialogKind.Select)
                {
                    _select.MoveTo(0);
                    return true;
                }
                if (_kind is DialogKind.Radio or DialogKind.Approval)
                {
                    _radio.MoveTo(0);
                    return true;
                }
                if (_kind == DialogKind.Multiline)
                {
                    _editor.MoveToLineStart();
                    return true;
                }
                return false;
            case KeyCode.End:
                if (_kind == DialogKind.Select)
                {
                    _select.MoveTo(_select.Count - 1);
                    return true;
                }
                if (_kind is DialogKind.Radio or DialogKind.Approval)
                {
                    _radio.MoveTo(_radio.Count - 1);
                    return true;
                }
                if (_kind == DialogKind.Multiline)
                {
                    _editor.MoveToLineEnd();
                    return true;
                }
                return false;
            case KeyCode.Backspace:
                if (_kind == DialogKind.Prompt)
                {
                    if (_input.Length > 0)
                    {
                        _input = _input[..^1];
                    }
                    return true;
                }
                if (_kind is DialogKind.Multiline or DialogKind.Approval)
                {
                    _editor.Backspace();
                    return true;
                }
                return false;
            case KeyCode.Delete:
                if (_kind is DialogKind.Multiline or DialogKind.Approval)
                {
                    _editor.DeleteForward();
                    return true;
                }
                return false;
            case KeyCode.Char:
                if ((key.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Meta | KeyModifiers.Alt)) != 0)
                {
                    return false;
                }
                if (_kind is DialogKind.Multiline or DialogKind.Approval)
                {
                    string chars = key.Character.ToString();
                    for (int i = 0; i < chars.Length; i++)
                    {
                        _editor.InsertChar(chars[i]);
                    }
                    return true;
                }
                if (_kind == DialogKind.Prompt)
                {
                    string text = key.Character.ToString();
                    if (text.Length == 0 || char.IsControl(text[0]))
                    {
                        return false;
                    }
                    _input += text;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private bool HandlePromptKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Enter)
        {
            return false;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (_input.Length > 0)
            {
                _input = _input[..^1];
            }
            return true;
        }
        if (AcceptsTypedChar(key))
        {
            _input += key.KeyChar;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a legacy key press types a character into a buffer. Single gate
    /// for all three editing kinds (#473), and the same expression
    /// <see cref="DiffViewerOverlay"/> already uses at its own char arm: Ctrl or
    /// Alt means the gesture is a command, Shift means it is a case.
    /// <para>
    /// This is the modifier half of the rule the kitty overload applies at
    /// <c>KeyCode.Char</c>, and that <c>ComposerController</c> applies to the
    /// composer. <c>ConsoleKeyInfo.Modifiers</c> has no Meta slot, so
    /// {Ctrl, Alt} is a strict subset of the kitty side's {Ctrl, Meta, Alt}:
    /// the two overloads cannot disagree about a gesture again. Before this
    /// existed the gate was <c>!char.IsControl(KeyChar)</c>, written out three
    /// times, and it could not see a modifier at all — Alt+char has a
    /// printable <c>KeyChar</c>, so it typed here while the kitty twin refused
    /// it.
    /// </para>
    /// </summary>
    private static bool AcceptsTypedChar(ConsoleKeyInfo key) =>
        (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0
        && !char.IsControl(key.KeyChar);

    private bool HandleSelectKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _select.Move(-1);
                return true;
            case ConsoleKey.DownArrow:
                _select.Move(1);
                return true;
            case ConsoleKey.PageUp:
                _select.Move(-SelectPageRows);
                return true;
            case ConsoleKey.PageDown:
                _select.Move(SelectPageRows);
                return true;
            case ConsoleKey.Home:
                _select.MoveTo(0);
                return true;
            case ConsoleKey.End:
                _select.MoveTo(_select.Count - 1);
                return true;
            case ConsoleKey.Enter:
                return false;
            default:
                return false;
        }
    }

    private bool HandleRadioKey(ConsoleKeyInfo key)
    {
        // Left/Right move in the shared arrow step above; Up/Down are aliases.
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _radio.Move(-1);
                return true;
            case ConsoleKey.DownArrow:
                _radio.Move(1);
                return true;
            case ConsoleKey.Home:
                _radio.MoveTo(0);
                return true;
            case ConsoleKey.End:
                _radio.MoveTo(_radio.Count - 1);
                return true;
            case ConsoleKey.Enter:
                return false;
            default:
                return false;
        }
    }

    private bool HandleApprovalKey(ConsoleKeyInfo key)
    {
        // Choice navigation mirrors the radio row, including vertical arrows.
        // Typing edits the reject reason, since arrows never leave the choices.
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _radio.Move(-1);
                return true;
            case ConsoleKey.DownArrow:
                _radio.Move(1);
                return true;
            case ConsoleKey.Home:
                _radio.MoveTo(0);
                return true;
            case ConsoleKey.End:
                _radio.MoveTo(_radio.Count - 1);
                return true;
            case ConsoleKey.Enter:
                return false;
            case ConsoleKey.Backspace:
                _editor.Backspace();
                return true;
            case ConsoleKey.Delete:
                _editor.DeleteForward();
                return true;
        }
        if (AcceptsTypedChar(key))
        {
            _editor.InsertChar(key.KeyChar);
            return true;
        }
        return false;
    }

    private bool HandleMultilineKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                return false;
            case ConsoleKey.Backspace:
                _editor.Backspace();
                return true;
            case ConsoleKey.Delete:
                _editor.DeleteForward();
                return true;
            case ConsoleKey.UpArrow:
                _editor.MoveUp();
                return true;
            case ConsoleKey.DownArrow:
                _editor.MoveDown();
                return true;
            case ConsoleKey.Home:
                _editor.MoveToLineStart();
                return true;
            case ConsoleKey.End:
                _editor.MoveToLineEnd();
                return true;
        }
        if (AcceptsTypedChar(key))
        {
            _editor.InsertChar(key.KeyChar);
            return true;
        }
        return false;
    }

    private void CycleFocus(bool forward)
    {
        if (_buttons.Count == 0)
        {
            return;
        }
        if (forward)
        {
            _focusedButton = (_focusedButton + 1) % _buttons.Count;
        }
        else
        {
            _focusedButton = (_focusedButton - 1 + _buttons.Count) % _buttons.Count;
        }
    }

    /// <summary>
    /// Computes the centered modal box for <paramref name="viewport"/> (typically
    /// the full screen). Returns default when hidden or the viewport is too small.
    /// Single source of truth for <see cref="Paint"/> and the PRIM2c overlay-layer
    /// seating (<see cref="DialogOverlayLayer"/> reports this as its bounds).
    /// </summary>
    public Rect ComputeBox(Rect viewport)
    {
        if (!Visible || viewport.Width < MinWidth || viewport.Height < MinHeight)
        {
            return default;
        }

        int width = Math.Min(MaxWidth, viewport.Width - 2);
        int contentRows = CountMessageRows(width) + ButtonRowHeight + (Padding * 2) + ControlRows();
        int height = Math.Min(MaxHeight, Math.Max(MinHeight, Math.Min(viewport.Height - 2, contentRows + 2)));
        int x = viewport.X + (viewport.Width - width) / 2;
        int y = viewport.Y + (viewport.Height - height) / 2;
        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// Paint the modal centered inside <paramref name="rect"/> (typically the
    /// full screen). No-op when hidden or the rect is too small.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        if (!Visible || rect.Width < MinWidth || rect.Height < MinHeight)
        {
            return;
        }
        if (rect.X >= buffer.Cols || rect.Y >= buffer.Rows)
        {
            return;
        }

        var box = ComputeBox(rect);
        if (box.Width <= 0 || box.Height <= 0)
        {
            return;
        }
        PanelChrome.PaintBorderBox(buffer, box);

        int textX = box.X + Padding;
        int textY = box.Y + Padding;
        int innerW = box.Width - (Padding * 2);
        DrawTitle(buffer, textX, textY, innerW);
        textY += 1;

        int controlRows = ControlRows();
        int messageRows = Math.Max(1, box.Height - (Padding * 2) - 2 - ButtonRowHeight - controlRows);
        string[] wrapped = WrapText(_message, innerW);
        int drawn = 0;
        for (int i = 0; i < wrapped.Length && drawn < messageRows; i++)
        {
            buffer.SetText(textX, textY + drawn, wrapped[i].AsSpan(0, Math.Min(wrapped[i].Length, innerW)), ChatPalette.Dim);
            drawn++;
        }
        textY += messageRows;

        if (_kind == DialogKind.Prompt)
        {
            buffer.SetText(textX, textY, "› ", new CellStyle(ChatPalette.Accent));
            string input = _input.Length > innerW - 2 ? _input[(^Math.Max(1, innerW - 2))..] : _input;
            buffer.SetText(textX + 2, textY, input, new CellStyle(ChatPalette.Accent));
        }
        else if (_kind == DialogKind.Select)
        {
            PaintSelectList(buffer, box, textX, textY, innerW);
        }
        else if (_kind == DialogKind.Radio)
        {
            PaintRadioRow(buffer, textX, textY, innerW, box);
        }
        else if (_kind == DialogKind.Multiline)
        {
            PaintMultiline(buffer, textX, textY, innerW, box);
        }
        else if (_kind == DialogKind.Approval)
        {
            PaintApproval(buffer, box, textX, textY, innerW);
        }

        DrawButtons(buffer, textX, box.Bottom - ButtonRowHeight - 1, innerW);
    }

    /// <summary>
    /// Paints the [UX7] approval body below the message: optional file-path +
    /// capped unified-diff preview (PRIM12 palette mapping), the fixed PRIM4
    /// choice row, then the reject-reason caption + capped editor rows.
    /// Every section clips at the button row, so small boxes degrade by
    /// truncation, never by overlap.
    /// </summary>
    private void PaintApproval(ScreenBuffer buffer, Rect box, int x, int y, int innerW)
    {
        int bottom = box.Bottom - ButtonRowHeight - 1;
        if (_approvalDiff.Count > 0)
        {
            if (_approvalFile.Length > 0 && y < bottom)
            {
                string path = _approvalFile.Length > innerW
                    ? "…" + _approvalFile[^Math.Max(1, innerW - 1)..]
                    : _approvalFile;
                buffer.SetText(x, y, path, new CellStyle(ChatPalette.Muted));
                y++;
            }
            for (int i = 0; i < _approvalDiff.Count && y < bottom; i++)
            {
                var line = _approvalDiff[i];
                string prefix = line.Kind switch
                {
                    DiffViewerLineKind.Added => "+ ",
                    DiffViewerLineKind.Removed => "- ",
                    _ => "  ",
                };
                var style = line.Kind switch
                {
                    DiffViewerLineKind.Added => ChatPalette.ToolOk,
                    DiffViewerLineKind.Removed => ChatPalette.ToolError,
                    _ => ChatPalette.ToolBody,
                };
                string row = prefix + line.Text;
                if (row.Length > innerW)
                {
                    row = row[..Math.Max(0, innerW - 1)] + "…";
                }
                buffer.SetText(x, y, row, style);
                y++;
            }
        }
        if (y < bottom)
        {
            PaintRadioRow(buffer, x, y, innerW, box);
            y++;
        }
        if (y < bottom)
        {
            string caption = ApprovalReasonCaption.Length > innerW
                ? ApprovalReasonCaption[..Math.Max(0, innerW - 1)] + "…"
                : ApprovalReasonCaption;
            buffer.SetText(x, y, caption, new CellStyle(ChatPalette.Muted));
            y++;
        }
        string[] reason = _editor.Text.Split('\n');
        var caretStyle = new CellStyle(ChatPalette.Accent);
        for (int i = 0; i < reason.Length && i < MaxApprovalReasonRows && y < bottom; i++)
        {
            string row = (i == 0 ? "› " : "  ") + reason[i];
            if (row.Length > innerW)
            {
                row = row[..Math.Max(0, innerW - 1)] + "…";
            }
            buffer.SetText(x, y, row, i == 0 ? caretStyle : ChatPalette.ToolArgs);
            y++;
        }
    }

    /// <summary>
    /// Control-block height: prompt/editor/select/radio rows below the message.
    /// Alert/Confirm reserve none, so their boxes are unchanged by [PRIM4].
    /// </summary>
    private int ControlRows() => _kind switch
    {
        DialogKind.Prompt => 1,
        DialogKind.Select => Math.Min(_select.Count, MaxSelectRows),
        DialogKind.Radio => _radio.Count > 0 ? 1 : 0,
        DialogKind.Multiline => Math.Clamp(_editor.LineCount, 1, MaxMultilineRows),
        DialogKind.Approval => ApprovalControlRows(),
        _ => 0,
    };

    /// <summary>
    /// Control-block height of the [UX7] approval modal: diff section
    /// (file-path line when set + capped preview rows) + choice row +
    /// reason caption + capped reason rows. Mirrors <see cref="PaintApproval"/>
    /// section order so <see cref="ComputeBox"/> reserves exactly what paint emits.
    /// </summary>
    private int ApprovalControlRows()
    {
        int rows = 0;
        if (_approvalDiff.Count > 0)
        {
            rows += (_approvalFile.Length > 0 ? 1 : 0) + _approvalDiff.Count;
        }
        rows += 1; // fixed PRIM4 choice row
        rows += 1; // reject-reason caption
        rows += Math.Clamp(_editor.LineCount, 1, MaxApprovalReasonRows);
        return rows;
    }

    private void PaintSelectList(ScreenBuffer buffer, Rect box, int x, int y, int innerW)
    {
        int available = Math.Max(0, box.Bottom - ButtonRowHeight - 1 - y);
        if (available <= 0 || _select.Count == 0)
        {
            return;
        }
        _select.SyncViewport(available);
        int selected = _select.SelectedIndex;
        var selectedStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        int first = (int)_select.Viewport.Offset;
        int painted = 0;
        for (int i = first; i < _select.Count && painted < available; i++)
        {
            string row = (i == selected ? "› " : "  ") + _select.Items[i];
            if (row.Length > innerW)
            {
                row = row[..Math.Max(0, innerW - 1)] + "…";
            }
            buffer.SetText(x, y + painted, row, i == selected ? selectedStyle : ChatPalette.ToolArgs);
            painted++;
        }
        if (_select.Count > available)
        {
            _ = Scrollbar.TryPaint(buffer, new Rect(box.Right - 1, y, 1, available), _select.Viewport);
        }
    }

    private void PaintRadioRow(ScreenBuffer buffer, int x, int y, int innerW, Rect box)
    {
        if (_radio.Count == 0 || y >= box.Bottom - ButtonRowHeight - 1)
        {
            return;
        }
        int selected = _radio.SelectedIndex;
        var onStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var offStyle = new CellStyle(ChatPalette.Muted);
        // Two-tone pass: selected option accented, the rest muted.
        int cursor = x;
        for (int i = 0; i < _radio.Count && cursor < x + innerW; i++)
        {
            string chip = "(" + (i == selected ? "● " : "○ ") + _radio.Options[i] + ")";
            if (i > 0)
            {
                if (cursor >= x + innerW)
                {
                    break;
                }
                buffer.SetText(cursor, y, " ", offStyle);
                cursor++;
            }
            int len = Math.Min(chip.Length, x + innerW - cursor);
            if (len <= 0)
            {
                break;
            }
            buffer.SetText(cursor, y, chip.AsSpan(0, len), i == selected ? onStyle : offStyle);
            cursor += len;
        }
    }

    private void PaintMultiline(ScreenBuffer buffer, int x, int y, int innerW, Rect box)
    {
        int available = Math.Max(0, box.Bottom - ButtonRowHeight - 1 - y);
        if (available <= 0)
        {
            return;
        }
        string[] lines = _editor.Text.Split('\n');
        var caretStyle = new CellStyle(ChatPalette.Accent);
        for (int i = 0; i < lines.Length && i < available; i++)
        {
            string row = (i == 0 ? "› " : "  ") + lines[i];
            if (row.Length > innerW)
            {
                row = row[..Math.Max(0, innerW - 1)] + "…";
            }
            buffer.SetText(x, y + i, row, i == 0 ? caretStyle : ChatPalette.ToolArgs);
        }
    }

    private void DrawTitle(ScreenBuffer buffer, int x, int y, int innerW)
    {
        string title = _title.Length > innerW ? _title[..Math.Max(0, innerW - 1)] + "…" : _title;
        buffer.SetText(x, y, title, new CellStyle(ChatPalette.Accent));
    }

    private void DrawButtons(ScreenBuffer buffer, int x, int y, int innerW)
    {
        if (_buttons.Count == 0)
        {
            return;
        }
        int span = _buttons.Count;
        int gap = 2;
        int total = 0;
        for (int i = 0; i < span; i++)
        {
            total += _buttons[i].Label.Length + 2;
        }
        total += (span - 1) * gap;
        int startX = x + Math.Max(0, (innerW - total) / 2);
        int cursor = startX;
        for (int i = 0; i < span; i++)
        {
            var button = _buttons[i];
            var style = i == _focusedButton ? new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold) : new CellStyle(ChatPalette.Muted);
            buffer.SetText(cursor, y, "[", style);
            buffer.SetText(cursor + 1, y, button.Label, style);
            buffer.SetText(cursor + 1 + button.Label.Length, y, "]", style);
            cursor += button.Label.Length + 2 + gap;
        }
    }

    private int CountMessageRows(int innerW)
    {
        if (innerW <= 0)
        {
            return 1;
        }
        return Math.Max(1, WrapText(_message, innerW).Length);
    }

    private static string[] WrapText(string text, int width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return [string.Empty];
        }
        var lines = new List<string>();
        int pos = 0;
        while (pos < text.Length)
        {
            int len = Math.Min(width, text.Length - pos);
            int breakAt = -1;
            for (int i = pos + len - 1; i > pos; i--)
            {
                if (text[i] == ' ' || text[i] == '\n')
                {
                    breakAt = i;
                    break;
                }
            }
            if (breakAt < pos)
            {
                lines.Add(text.Substring(pos, len));
                pos += len;
            }
            else
            {
                lines.Add(text.Substring(pos, breakAt - pos));
                pos = breakAt + 1;
            }
        }
        return lines.Count == 0 ? [string.Empty] : lines.ToArray();
    }
}