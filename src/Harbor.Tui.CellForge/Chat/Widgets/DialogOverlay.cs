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
public sealed partial class DialogOverlay
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
        Harbor.Abstractions.Extensions.Ensure.NotNull(okLabel);
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
        Harbor.Abstractions.Extensions.Ensure.NotNull(okLabel);
        Harbor.Abstractions.Extensions.Ensure.NotNull(cancelLabel);
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
        Harbor.Abstractions.Extensions.Ensure.NotNull(okLabel);
        Harbor.Abstractions.Extensions.Ensure.NotNull(cancelLabel);
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
        Harbor.Abstractions.Extensions.Ensure.NotNull(options);
        Harbor.Abstractions.Extensions.Ensure.NotNull(okLabel);
        Harbor.Abstractions.Extensions.Ensure.NotNull(cancelLabel);
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
        Harbor.Abstractions.Extensions.Ensure.NotNull(options);
        Harbor.Abstractions.Extensions.Ensure.NotNull(okLabel);
        Harbor.Abstractions.Extensions.Ensure.NotNull(cancelLabel);
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
        Harbor.Abstractions.Extensions.Ensure.NotNull(okLabel);
        Harbor.Abstractions.Extensions.Ensure.NotNull(cancelLabel);
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
        Harbor.Abstractions.Extensions.Ensure.NotNull(okLabel);
        Harbor.Abstractions.Extensions.Ensure.NotNull(cancelLabel);
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

    /// <summary>
    /// Copies another overlay's full state into this one (issue #1248, slice 1):
    /// the onboarding flow owns its dialog while the host paints the screen's
    /// one, so every key the flow consumes is followed by this copy. Field copy,
    /// not a <c>Show*</c> call — input text and button focus survive it.
    /// </summary>
    public void SyncFrom(DialogOverlay source)
    {
        Harbor.Abstractions.Extensions.Ensure.NotNull(source);
        if (ReferenceEquals(this, source))
        {
            return;
        }

        _kind = source._kind;
        _title = source._title;
        _message = source._message;
        _input = source._input;
        _editor.SetText(source._editor.Text);
        _select.SetItems(source._select.Items);
        _select.MoveTo(source._select.SelectedIndex);
        _radio.SetOptions(source._radio.Options, source._radio.SelectedIndex);
        _approvalTool = source._approvalTool;
        _approvalFile = source._approvalFile;
        _approvalDiff.Clear();
        _approvalDiff.AddRange(source._approvalDiff);
        _buttons.Clear();
        _buttons.AddRange(source._buttons);
        _focusedButton = source._buttons.Count == 0
            ? 0
            : Math.Clamp(source._focusedButton, 0, source._buttons.Count - 1);
        Visible = source.Visible;
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
    /// <para>
    /// A RELEASE applies nothing (#784). <see cref="KeyEvent"/> is the only
    /// key vocabulary on this path that carries a phase, so this switch used to
    /// run twice for one physical keypress: the release of
    /// <see cref="KeyCode.Char"/> typed the character into the buffer a second
    /// time, an arrow moved the selection twice, and <see cref="KeyCode.Escape"/>
    /// dismissed on top of a dismissal that is already invisible. The gate
    /// accepts {Press, Repeat} — repeat is a real second press, so rejecting
    /// it would break auto-repeat — and it is deliberately here rather than in
    /// a converter: <c>KeyEventMapper.ToDto</c> already drops releases, and
    /// <c>ApprovalGateView.HandleKey</c> already gates on the same pair, so
    /// the phase was never lost at a boundary, only ignored here.
    /// <para>
    /// The legacy <see cref="HandleKey(ConsoleKeyInfo)"/> overload needs no
    /// twin gate: <see cref="ConsoleKeyInfo"/> has no phase field, because the
    /// BCL only ever reports presses.
    /// </para>
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (!Visible || key.EventType == KeyEventType.Release)
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
                if (key.Modifiers.IsCommandModifier())
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
        key.Modifiers.AcceptsTypedChar()
        && !char.IsControl(key.KeyChar);

    // The four kind switches the legacy per-kind handlers carried below the
    // shared prefix moved into the per-kind state objects at the end of this
    // file (#1497). The handler names stay — the census rule and the release
    // gate of #784 locate the dispatch through them — and are now one-line
    // delegations.

    private bool HandleSelectKey(ConsoleKeyInfo key) => SelectModeState.Instance.HandleConsoleKey(this, key);

    private bool HandleRadioKey(ConsoleKeyInfo key) => RadioModeState.Instance.HandleConsoleKey(this, key);

    private bool HandleApprovalKey(ConsoleKeyInfo key) => ApprovalModeState.Instance.HandleConsoleKey(this, key);

    private bool HandleMultilineKey(ConsoleKeyInfo key) => MultilineModeState.Instance.HandleConsoleKey(this, key);

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

    // ── [GoF State] #1497: one state object per dispatched DialogKind ─────────
    //
    // The four per-kind key-switch nuclei from the old Handle methods live
    // here. The kind dispatcher inside HandleKey remains the single
    // dispatch-by-kind point (no new extension axis — a new dialog kind is a
    // new state class plus one dispatcher arm, not a seam a plugin reaches, so
    // nothing is declared in PluginAxisRegistry): none of the existing states
    // change. Prompt is deliberately NOT a state: it has no
    // kind switch of its own and its path is frozen legacy by its own
    // contract.
    //
    // The states are stateless singletons that only ROUTE — the dialog's own
    // data stays in its fields, so SyncFrom keeps copying fields and needs no
    // state bookkeeping. What used to be copied between the handlers (the
    // Up/Down/Home/End branches and the Backspace/Delete/typed-char trio) is
    // shared once in DialogChoiceNavState / EditBuffer instead.

    /// <summary>
    /// Mode state of the legacy key path: the per-kind remainder of
    /// <c>HandleKey(ConsoleKeyInfo)</c> after its shared Escape/Tab/arrows
    /// prefix. Routing only — see the note above.
    /// </summary>
    private abstract class DialogModeState
    {
        /// <summary>Routes one editing/nav key for this kind. Consumed keys return true.</summary>
        public abstract bool HandleConsoleKey(DialogOverlay overlay, ConsoleKeyInfo key);

        /// <summary>
        /// Backspace/Delete/typed-char edits through the shared multiline
        /// editor: the buffer the <see cref="DialogKind.Multiline"/> kind
        /// types into, and the reject-reason field of an approval modal.
        /// </summary>
        protected static bool EditBuffer(DialogOverlay overlay, ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.Backspace:
                    overlay._editor.Backspace();
                    return true;
                case ConsoleKey.Delete:
                    overlay._editor.DeleteForward();
                    return true;
            }
            if (AcceptsTypedChar(key))
            {
                overlay._editor.InsertChar(key.KeyChar);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Shared arrow navigation for the modes that move one selection with the
    /// arrows: <see cref="SelectModeState"/> steps the option column,
    /// <see cref="RadioModeState"/> and <see cref="ApprovalModeState"/> step
    /// the shared radio row. The copied Up/Down/Home/End branch pair #1497
    /// names is this base routing once, with the state naming the field each
    /// gesture touches. Enter keeps the uniform dialog contract: the host
    /// commits and reads the selection.
    /// </summary>
    private abstract class DialogChoiceNavState : DialogModeState
    {
        /// <summary>Move the active selection by one row (negative = up).</summary>
        protected abstract void MoveBy(DialogOverlay overlay, int step);

        /// <summary>Jump the active selection to an edge: <c>0</c> = first row, <c>-1</c> = last.</summary>
        protected abstract void MoveToEdge(DialogOverlay overlay, int edge);

        public override bool HandleConsoleKey(DialogOverlay overlay, ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    MoveBy(overlay, -1);
                    return true;
                case ConsoleKey.DownArrow:
                    MoveBy(overlay, 1);
                    return true;
                case ConsoleKey.Home:
                    MoveToEdge(overlay, 0);
                    return true;
                case ConsoleKey.End:
                    MoveToEdge(overlay, -1);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// <see cref="DialogKind.Select"/>: option-column navigation. PageUp and
    /// PageDown page by <see cref="SelectPageRows"/> before the shared row
    /// navigation runs.
    /// </summary>
    private sealed class SelectModeState : DialogChoiceNavState
    {
        internal static readonly SelectModeState Instance = new();

        public override bool HandleConsoleKey(DialogOverlay overlay, ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.PageUp:
                    overlay._select.Move(-SelectPageRows);
                    return true;
                case ConsoleKey.PageDown:
                    overlay._select.Move(SelectPageRows);
                    return true;
            }
            return base.HandleConsoleKey(overlay, key);
        }

        protected override void MoveBy(DialogOverlay overlay, int step) => overlay._select.Move(step);

        protected override void MoveToEdge(DialogOverlay overlay, int edge) =>
            overlay._select.MoveTo(edge == 0 ? 0 : overlay._select.Count - 1);
    }

    /// <summary>
    /// <see cref="DialogKind.Radio"/>: the single-row mutually-exclusive
    /// choice. Left/Right are aliases handled by the shared prefix above;
    /// Up/Down/Home/End route through this state.
    /// </summary>
    private sealed class RadioModeState : DialogChoiceNavState
    {
        internal static readonly RadioModeState Instance = new();

        protected override void MoveBy(DialogOverlay overlay, int step) => overlay._radio.Move(step);

        protected override void MoveToEdge(DialogOverlay overlay, int edge) =>
            overlay._radio.MoveTo(edge == 0 ? 0 : overlay._radio.Count - 1);
    }

    /// <summary>
    /// <see cref="DialogKind.Approval"/>: the fixed PRIM4 radio choice row
    /// (same navigation as <see cref="RadioModeState"/>) plus the
    /// reject-reason editor. Typing edits the reason, since arrows never
    /// leave the choices.
    /// </summary>
    private sealed class ApprovalModeState : DialogChoiceNavState
    {
        internal static readonly ApprovalModeState Instance = new();

        public override bool HandleConsoleKey(DialogOverlay overlay, ConsoleKeyInfo key)
        {
            if (base.HandleConsoleKey(overlay, key))
            {
                return true;
            }
            return EditBuffer(overlay, key);
        }

        protected override void MoveBy(DialogOverlay overlay, int step) => overlay._radio.Move(step);

        protected override void MoveToEdge(DialogOverlay overlay, int edge) =>
            overlay._radio.MoveTo(edge == 0 ? 0 : overlay._radio.Count - 1);
    }

    /// <summary>
    /// <see cref="DialogKind.Multiline"/>: the shared multiline editor owns the
    /// vertical arrows and Home/End as caret moves; Backspace/Delete/typed
    /// chars route through <see cref="DialogModeState.EditBuffer"/>. Plain
    /// Enter still submits (the Shift/Alt+Enter newline split is kitty-only).
    /// </summary>
    private sealed class MultilineModeState : DialogModeState
    {
        internal static readonly MultilineModeState Instance = new();

        public override bool HandleConsoleKey(DialogOverlay overlay, ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    return false;
                case ConsoleKey.UpArrow:
                    overlay._editor.MoveUp();
                    return true;
                case ConsoleKey.DownArrow:
                    overlay._editor.MoveDown();
                    return true;
                case ConsoleKey.Home:
                    overlay._editor.MoveToLineStart();
                    return true;
                case ConsoleKey.End:
                    overlay._editor.MoveToLineEnd();
                    return true;
            }
            return EditBuffer(overlay, key);
        }
    }
}