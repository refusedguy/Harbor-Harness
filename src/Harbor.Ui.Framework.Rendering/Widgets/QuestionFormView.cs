using System.Text;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>One selectable answer of a <see cref="QuestionItem" />.</summary>
public sealed class QuestionOption
{
    public QuestionOption(string label, string? description = null)
    {
        Label = string.IsNullOrWhiteSpace(label) ? "?" : label.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    /// <summary>Option label (sanitized, never empty).</summary>
    public string Label { get; }

    /// <summary>Optional one-line elaboration (<see langword="null" /> when absent).</summary>
    public string? Description { get; }
}

/// <summary>
/// One question of a <see cref="QuestionFormView" /> batch form: a title (the
/// tab), a prompt, and an enumerated option set plus an optional free-text
/// custom answer (the kilocode QuestionDock / crush batch-form pattern).
/// Immutable config — all interactive state lives in the form.
/// </summary>
public sealed class QuestionItem
{
    public QuestionItem(
        string title,
        string prompt,
        IReadOnlyList<QuestionOption>? options = null,
        bool multiSelect = true,
        bool allowCustom = true)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "?" : title.Trim();
        Prompt = (prompt ?? string.Empty).Trim();
        Options = options is null ? [] : [.. options];
        MultiSelect = multiSelect;
        AllowCustom = allowCustom;
    }

    /// <summary>Tab title (sanitized, never empty).</summary>
    public string Title { get; }

    /// <summary>Question prompt.</summary>
    public string Prompt { get; }

    /// <summary>Enumerated options in host order (possibly empty when free-text only).</summary>
    public IReadOnlyList<QuestionOption> Options { get; }

    /// <summary>True = checkbox set (Space toggles), false = radio (Space selects exclusively).</summary>
    public bool MultiSelect { get; }

    /// <summary>Whether the trailing custom-answer row is offered.</summary>
    public bool AllowCustom { get; }
}

/// <summary>Submitted answers of one <see cref="QuestionItem" /> (host consumption + audit).</summary>
public sealed record QuestionAnswer(int QuestionIndex, string[] SelectedLabels, string CustomAnswer);

/// <summary>
/// Inline question form in the chat timeline ([PRIM13], issue #309): a batch
/// of questions behind a tab strip (one tab per question), each with a
/// multiselect/single-select option list plus a free-text custom answer row.
/// Painted as a pending card; after submit the hint row is replaced with a
/// stamp so history keeps an audit trail (height stays identical — layout
/// never jumps on submit; switching tabs legitimately re-measures).
/// Blocks stay paint-only in this renderer, so answers are collected by the
/// host frame loop calling <see cref="HandleKey" /> first while the form is
/// focused. Implements <see cref="IFocusTarget" /> so the host
/// <c>FocusRouter</c> can traverse it via Tab.
/// Scope: new widget only — no approval wiring, no diff viewer, no status.
/// </summary>
public sealed class QuestionFormView : IChatBlock, IFocusTarget
{
    private const string HeaderLabel = "? question";
    private const string HintLine = "[tab] switch · [↑↓] move · [space] toggle · type for custom · [enter] submit";
    private const int LeftPad = 2;

    private static long _nextId;
    private readonly long _id = Interlocked.Increment(ref _nextId);

    private readonly List<QuestionItem> _questions;
    private readonly List<QuestionState> _states;
    private int _selectedQuestion;
    private bool _focused;
    private bool _submitted;

    private List<string> _wrappedPrompt = [];
    private int _wrappedFor = -1;
    private int _wrappedWidth = -1;

    /// <summary>Stable router id — unique per form instance.</summary>
    public string Id => $"question:{_id}";

    public void OnFocusChanged(bool focused) => _focused = focused;

    /// <summary>
    /// Screen-space clip rect from the most recent <see cref="Paint" /> pass.
    /// Lets hosts hit-test mouse clicks against the card without a layout pass;
    /// harmless staleness until the next frame.
    /// </summary>
    internal Rect? LastPaintRect { get; private set; }

    public QuestionFormView(IReadOnlyList<QuestionItem>? questions)
    {
        _questions = questions is null ? [] : [.. questions];
        _states = new List<QuestionState>(_questions.Count);
        for (int i = 0; i < _questions.Count; i++)
        {
            _states.Add(new QuestionState());
        }
    }

    public string Kind => "question";

    public bool IsStreamContinuation => false;

    public int BudgetBytes
    {
        get
        {
            int bytes = 128;
            for (int i = 0; i < _questions.Count; i++)
            {
                var q = _questions[i];
                bytes += (q.Title.Length + q.Prompt.Length) * 2;
                for (int j = 0; j < q.Options.Count; j++)
                {
                    bytes += q.Options[j].Label.Length * 2;
                }

                bytes += _states[i].Custom.Length * 2;
            }

            return bytes;
        }
    }

    /// <summary>Questions in host order.</summary>
    public IReadOnlyList<QuestionItem> Questions => _questions;

    /// <summary>Number of questions (tabs).</summary>
    public int Count => _questions.Count;

    /// <summary>Active tab index (0 when empty).</summary>
    public int SelectedQuestion => _questions.Count == 0 ? 0 : Math.Clamp(_selectedQuestion, 0, _questions.Count - 1);

    /// <summary>True after <see cref="TrySubmit" /> — answers are frozen.</summary>
    public bool IsSubmitted => _submitted;

    public bool IsPending => !_submitted;

    /// <summary>Raised exactly once, on the render/input thread that submitted, when answers are recorded.</summary>
    public event EventHandler? Submitted;

    /// <summary>Cursor row of question <paramref name="questionIndex" /> (option rows, then the custom row).</summary>
    public int CursorAt(int questionIndex) => _states[CheckedIndex(questionIndex)].Cursor;

    /// <summary>Selected option indices of question <paramref name="questionIndex" /> (ascending).</summary>
    public IReadOnlyList<int> SelectedOptions(int questionIndex)
    {
        var state = _states[CheckedIndex(questionIndex)];
        var sorted = new List<int>(state.Selected);
        sorted.Sort();
        return sorted;
    }

    /// <summary>Free-text custom answer of question <paramref name="questionIndex" />.</summary>
    public string CustomAnswer(int questionIndex) => _states[CheckedIndex(questionIndex)].Custom.ToString();

    /// <summary>Snapshot of all answers (selected labels + custom text per question).</summary>
    public IReadOnlyList<QuestionAnswer> GetAnswers()
    {
        var answers = new List<QuestionAnswer>(_questions.Count);
        for (int i = 0; i < _questions.Count; i++)
        {
            var labels = new List<string>();
            foreach (int idx in SelectedOptions(i))
            {
                labels.Add(_questions[i].Options[idx].Label);
            }

            answers.Add(new QuestionAnswer(i, [.. labels], _states[i].Custom.ToString()));
        }

        return answers;
    }

    /// <summary>Selects a tab (clamped). Returns true when the selection changed.</summary>
    public bool SelectQuestion(int index)
    {
        if (_questions.Count == 0 || _submitted)
        {
            return false;
        }

        int next = Math.Clamp(index, 0, _questions.Count - 1);
        if (next == _selectedQuestion)
        {
            return false;
        }

        _selectedQuestion = next;
        return true;
    }

    /// <summary>Selects the next tab (wraps). Returns true when the selection changed.</summary>
    public bool NextQuestion() =>
        _questions.Count > 1 && !_submitted && SelectQuestion(_selectedQuestion + 1 >= _questions.Count ? 0 : _selectedQuestion + 1);

    /// <summary>Selects the previous tab (wraps). Returns true when the selection changed.</summary>
    public bool PrevQuestion() =>
        _questions.Count > 1 && !_submitted && SelectQuestion(_selectedQuestion - 1 < 0 ? _questions.Count - 1 : _selectedQuestion - 1);

    /// <summary>Moves the option cursor of the active question by a signed delta (clamped).</summary>
    public bool MoveCursor(int delta)
    {
        if (_questions.Count == 0 || _submitted || delta == 0)
        {
            return false;
        }

        int q = SelectedQuestion;
        int rows = RowCount(q);
        if (rows == 0)
        {
            return false;
        }

        var state = _states[q];
        int next = Math.Clamp(state.Cursor, 0, rows - 1) + delta;
        next = Math.Clamp(next, 0, rows - 1);
        if (next == state.Cursor)
        {
            return false;
        }

        state.Cursor = next;
        return true;
    }

    /// <summary>
    /// Toggles/selects the option under the active cursor: checkbox toggle in
    /// multiselect mode, exclusive select in single-select mode. No-op on the
    /// custom row (typing is the gesture there). Returns false when nothing changed.
    /// </summary>
    public bool ToggleAtCursor()
    {
        if (_questions.Count == 0 || _submitted)
        {
            return false;
        }

        int q = SelectedQuestion;
        var item = _questions[q];
        var state = _states[q];
        int cursor = Math.Clamp(state.Cursor, 0, Math.Max(0, RowCount(q) - 1));
        if (cursor >= item.Options.Count)
        {
            return false; // custom row — no toggle semantic
        }

        if (item.MultiSelect)
        {
            if (!state.Selected.Add(cursor))
            {
                _ = state.Selected.Remove(cursor);
            }

            return true;
        }

        if (state.Selected.Count == 1 && state.Selected.Contains(cursor))
        {
            state.Selected.Clear();
            return true;
        }

        state.Selected.Clear();
        _ = state.Selected.Add(cursor);
        return true;
    }

    /// <summary>Replaces the custom answer of question <paramref name="questionIndex" /> (prefill path).</summary>
    public bool SetCustomAnswer(int questionIndex, string? text)
    {
        if (_submitted)
        {
            return false;
        }

        int q = CheckedIndex(questionIndex);
        if (!_questions[q].AllowCustom)
        {
            return false;
        }

        var sb = _states[q].Custom;
        sb.Clear();
        if (!string.IsNullOrEmpty(text))
        {
            _ = sb.Append(text);
        }

        return true;
    }

    /// <summary>
    /// Records the answers — one-shot by contract (audit stamp). Returns false
    /// when already submitted or when the form holds no questions.
    /// </summary>
    public bool TrySubmit()
    {
        if (_submitted || _questions.Count == 0)
        {
            return false;
        }

        _submitted = true;
        Submitted?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Height: header + tab strip (multi-question only) + wrapped prompt +
    /// option rows + custom row (when offered) + hint/stamp — identical pending
    /// and submitted so a repaint never shifts timeline slots.
    /// </summary>
    public BlockMeasure Measure(int width)
    {
        if (_questions.Count == 0)
        {
            return BlockMeasure.Exact(1);
        }

        int w = Math.Max(8, width);
        EnsurePromptWrapped(SelectedQuestion, w);
        return BlockMeasure.Exact(1 + StripRows() + _wrappedPrompt.Count + RowCount(SelectedQuestion) + 1);
    }

    public int CheapEstimate(int width)
    {
        if (_questions.Count == 0)
        {
            return 1;
        }

        int w = Math.Max(8, width);
        var item = _questions[SelectedQuestion];
        return 1 + StripRows() + BlockMath.EstimateLines(item.Prompt, Math.Max(4, w - LeftPad)) + RowCount(SelectedQuestion) + 1;
    }

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        if (ctx.Rect.Width <= 0 || ctx.Rect.Height <= 0)
        {
            return;
        }

        LastPaintRect = ctx.Rect;

        if (_questions.Count == 0)
        {
            if (ctx.SkipRows == 0)
            {
                buffer.SetText(ctx.Rect.X, ctx.Rect.Y, "(empty)", ChatPalette.Dim);
            }

            return;
        }

        int q = SelectedQuestion;
        var item = _questions[q];
        var state = _states[q];
        int width = ctx.Rect.Width;
        int total = TotalRows(width);
        int skip = Math.Min(ctx.SkipRows, total);
        int rows = Math.Min(ctx.Rect.Height, total - skip);

        var headerStyle = _submitted
            ? ChatPalette.Dim
            : new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var promptStyle = new CellStyle(ChatPalette.Text);
        var optionStyle = ChatPalette.ToolArgs;
        var cursorStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var checkedStyle = ChatPalette.ToolOk;
        var hintStyle = ChatPalette.Dim;

        string focusRail = _focused && !_submitted ? "▸" : string.Empty;
        var body = new List<Row>(TotalRows(width));
        body.Add(new Row(focusRail + HeaderLabel + $" · {q + 1}/{_questions.Count} · {item.Title}", headerStyle));
        if (StripRows() == 1)
        {
            body.Add(new Row(BuildStrip(), ChatPalette.Dim, IsStrip: true));
        }

        EnsurePromptWrapped(q, Math.Max(8, width));
        for (int i = 0; i < _wrappedPrompt.Count; i++)
        {
            body.Add(new Row(_wrappedPrompt[i], promptStyle, Pad: true));
        }

        int cursor = state.Cursor;
        for (int i = 0; i < item.Options.Count; i++)
        {
            var opt = item.Options[i];
            string text = opt.Description is null ? opt.Label : $"{opt.Label} — {opt.Description}";
            body.Add(new Row(
                $"{(i == cursor ? "▸" : " ")} {(state.Selected.Contains(i) ? "[x]" : "[ ]")} {text}",
                i == cursor ? cursorStyle : optionStyle,
                OptionIndex: i));
        }

        if (item.AllowCustom)
        {
            bool customCursor = cursor >= item.Options.Count;
            string caret = _submitted ? string.Empty : "▌";
            body.Add(new Row(
                $"{(customCursor ? "▸" : " ")} ✎ custom: {state.Custom}{caret}",
                customCursor ? cursorStyle : optionStyle));
        }

        body.Add(_submitted
            ? new Row("✓ submitted", ChatPalette.ToolOk)
            : new Row(HintLine, hintStyle));

        for (int r = 0; r < rows; r++)
        {
            var row = body[skip + r];
            int y = ctx.Rect.Y + r;
            if (row.IsStrip)
            {
                PaintStrip(buffer, ctx.Rect.X, y, width);
            }
            else if (row.OptionIndex >= 0 && row.OptionIndex == cursor && !_submitted)
            {
                // Cursor row: accent marker, checkbox tone keeps its audit color.
                PaintOptionRow(buffer, ctx.Rect.X, y, width, row, cursorStyle, checkedStyle);
            }
            else
            {
                string line = row.Pad ? new string(' ', Math.Min(LeftPad, width)) + row.Text : row.Text;
                buffer.SetText(ctx.Rect.X, y, Truncate(line, width), row.Style);
            }
        }
    }

    public string RawText()
    {
        if (_questions.Count == 0)
        {
            return "(empty)";
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(HeaderLabel).Append(" · ").Append(_questions.Count).AppendLine(" question(s)");
        for (int i = 0; i < _questions.Count; i++)
        {
            var item = _questions[i];
            sb.Append(i == SelectedQuestion ? '▸' : ' ').Append(" Q").Append(i + 1).Append(": ").AppendLine(item.Title);
            if (item.Prompt.Length > 0)
            {
                sb.Append("  ").AppendLine(item.Prompt);
            }

            var state = _states[i];
            for (int j = 0; j < item.Options.Count; j++)
            {
                _ = sb.Append("  ").Append(state.Selected.Contains(j) ? "[x] " : "[ ] ").AppendLine(item.Options[j].Label);
            }

            if (item.AllowCustom)
            {
                _ = sb.Append("  custom: ").AppendLine(state.Custom.ToString());
            }
        }

        _ = sb.Append(_submitted ? "submitted" : HintLine);
        return sb.ToString();
    }

    /// <summary>
    /// Route one key event. Handles press/repeat only and only while pending:
    /// Tab/Left/Right/digits switch tabs, Up/Down (j/k) move the
    /// option cursor, Space toggles (or types on the custom row), printable
    /// chars + Backspace edit the custom answer, Enter submits. Escape is NOT
    /// consumed (host-owned). Returns true when the key was consumed.
    /// <para>
    /// Modifiers: command keys and runes on the option rows take none, but a
    /// rune on the custom row is answer TEXT and takes <see cref="AcceptsTypedChar"/>
    /// — <c>Ctrl|Meta|Alt</c> refused, <c>Shift</c> allowed (#785).
    /// </para>
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (_submitted || _questions.Count == 0
            || (key.EventType != KeyEventType.Press && key.EventType != KeyEventType.Repeat))
        {
            return false;
        }

        // Two gates, because this widget is two widgets (#785). The custom row
        // is a text buffer and takes the TYPING gate; every other key, and
        // runes on the option rows, are commands and take the strict one.
        if (key.Key != KeyCode.Char)
        {
            if (key.Modifiers != KeyModifiers.None)
            {
                return false;
            }
        }
        else if (!AcceptsTypedChar(key))
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Tab:
                return NextQuestion();
            case KeyCode.Left:
                return PrevQuestion();
            case KeyCode.Right:
                return NextQuestion();
            case KeyCode.Home:
                return SelectQuestion(0);
            case KeyCode.End:
                return SelectQuestion(_questions.Count - 1);
            case KeyCode.Up:
                return MoveCursor(-1);
            case KeyCode.Down:
                return MoveCursor(1);
            case KeyCode.Enter:
                return TrySubmit();
            case KeyCode.Backspace:
                return BackspaceCustom();
            case KeyCode.Escape:
                return false;
            case KeyCode.Char:
                return HandleChar(key.Character);
            default:
                return false;
        }
    }

    /// <summary>
    /// The typing half of the modifier gate, and the reason this widget is not
    /// one of the <c>!= KeyModifiers.None</c> sites (#785).
    /// <para>
    /// On the custom row every printable rune is answer text, so the gate is
    /// the composer's (<c>ComposerController.cs:173</c>):
    /// <c>Ctrl|Meta|Alt</c> are command modifiers and are refused, while
    /// <c>Shift</c> is a case-shaper and passes — real encoders deliver a
    /// capital as the capital rune PLUS <c>Shift</c>, so refusing the modifier
    /// used to swallow the letter and left the answer untypable in capitals.
    /// </para>
    /// <para>
    /// Off the custom row the same rune is navigation (digits jump tabs,
    /// hjkl move, space toggles), so there the strict no-modifier gate still
    /// applies and nothing changes. That is the whole of the split: a buffer
    /// loses data when a rune is dropped, a command does not.
    /// </para>
    /// </summary>
    private bool AcceptsTypedChar(in KeyEvent key)
    {
        if ((key.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Meta | KeyModifiers.Alt)) != 0)
        {
            return false;
        }

        return OnCustomRow() || key.Modifiers == KeyModifiers.None;
    }

    private bool HandleChar(Rune c)
    {
        // Parked on the custom row, every printable rune (digits and hjkl
        // included) is answer text — tab digits and vim-nav stay dormant so
        // typing "hello 42" never switches tabs or moves the cursor.
        if (OnCustomRow())
        {
            if (c.Value is < 0x20 or 0x7F)
            {
                return false;
            }

            return AppendCustom(c.ToString());
        }

        // Digits jump to tabs 1..9 (the Tabs reason to exist); the active
        // question keeps Space-toggle for its own options.
        if (c.Value is >= '1' and <= '9')
        {
            int index = c.Value - '1';
            return index < _questions.Count && SelectQuestion(index);
        }

        int upper = Rune.ToUpperInvariant(c).Value;
        switch (upper)
        {
            case 'H':
                return PrevQuestion();
            case 'L':
                return NextQuestion();
            case 'J':
                return MoveCursor(1);
            case 'K':
                return MoveCursor(-1);
        }

        if (c.Value == ' ')
        {
            return ToggleAtCursor();
        }

        // Option rows never swallow typing (the composer keeps it).
        return false;
    }

    private bool OnCustomRow()
    {
        if (_questions.Count == 0)
        {
            return false;
        }

        int q = SelectedQuestion;
        var item = _questions[q];
        if (!item.AllowCustom)
        {
            return false;
        }

        return _states[q].Cursor >= item.Options.Count;
    }

    private bool AppendCustom(string text)
    {
        if (_questions.Count == 0)
        {
            return false;
        }

        int q = SelectedQuestion;
        if (!_questions[q].AllowCustom)
        {
            return false;
        }

        _ = _states[q].Custom.Append(text);
        return true;
    }

    private bool BackspaceCustom()
    {
        if (!OnCustomRow())
        {
            return false;
        }

        var sb = _states[SelectedQuestion].Custom;
        if (sb.Length == 0)
        {
            return false;
        }

        // Rune-aware delete: a trailing low surrogate takes its high half along.
        int take = sb.Length >= 2 && char.IsLowSurrogate(sb[^1]) && char.IsHighSurrogate(sb[^2]) ? 2 : 1;
        _ = sb.Remove(sb.Length - take, take);
        return true;
    }

    private int CheckedIndex(int questionIndex)
    {
        if ((uint)questionIndex >= (uint)_questions.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(questionIndex));
        }

        return questionIndex;
    }

    private int StripRows() => _questions.Count > 1 ? 1 : 0;

    private int RowCount(int q)
    {
        var item = _questions[q];
        return item.Options.Count + (item.AllowCustom ? 1 : 0);
    }

    private int TotalRows(int width)
    {
        EnsurePromptWrapped(SelectedQuestion, Math.Max(8, width));
        return 1 + StripRows() + _wrappedPrompt.Count + RowCount(SelectedQuestion) + 1;
    }

    private void EnsurePromptWrapped(int q, int width)
    {
        if (_wrappedFor == q && _wrappedWidth == width)
        {
            return;
        }

        var lines = new List<string>(1);
        string prompt = _questions[q].Prompt;
        if (prompt.Length > 0)
        {
            TextWrap.WrapTo(prompt, Math.Max(4, width - LeftPad), lines);
        }

        _wrappedPrompt = lines;
        _wrappedFor = q;
        _wrappedWidth = width;
    }

    private string BuildStrip()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _questions.Count; i++)
        {
            if (i > 0)
            {
                _ = sb.Append(" │ ");
            }

            if (i == SelectedQuestion)
            {
                _ = sb.Append('[').Append(i + 1).Append(' ').Append(_questions[i].Title).Append(']');
            }
            else
            {
                _ = sb.Append(i + 1).Append(' ').Append(_questions[i].Title);
            }
        }

        return sb.ToString();
    }

    private void PaintStrip(ScreenBuffer buffer, int x, int y, int width)
    {
        var selectedStyle = new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold);
        var plainStyle = ChatPalette.ToolArgs;
        var dividerStyle = ChatPalette.Dim;

        int cx = x;
        int right = x + width;
        for (int i = 0; i < _questions.Count && cx < right; i++)
        {
            if (i > 0)
            {
                buffer.SetText(cx, y, " │ ".AsSpan(0, Math.Min(3, right - cx)), dividerStyle);
                cx += 3;
                if (cx >= right)
                {
                    break;
                }
            }

            string segment = i == SelectedQuestion
                ? $"[{i + 1} {_questions[i].Title}]"
                : $"{i + 1} {_questions[i].Title}";
            int avail = right - cx;
            if (avail <= 0)
            {
                break;
            }

            if (segment.Length > avail)
            {
                segment = segment[..avail];
            }

            buffer.SetText(cx, y, segment, i == SelectedQuestion ? selectedStyle : plainStyle);
            cx += segment.Length;
        }
    }

    private static void PaintOptionRow(
        ScreenBuffer buffer, int x, int y, int width, Row row, CellStyle cursorStyle, CellStyle checkedStyle)
    {
        // Full row first, then the marker cell in accent, then the checkbox in
        // its audit tone on top — painting the checkbox before the row fill
        // buries it under the row style.
        string text = row.Text;
        buffer.SetText(x, y, Truncate(text, width), row.Style);
        if (width > 0 && text.Length > 0)
        {
            buffer.SetText(x, y, text.AsSpan(0, 1), cursorStyle);
        }

        int box = text.IndexOf('[');
        int boxLen = box < 0 ? 0 : Math.Min(3, Math.Min(text.Length - box, width - box));
        if (boxLen > 0)
        {
            buffer.SetText(x + box, y, text.AsSpan(box, boxLen), checkedStyle);
        }
    }

    private static string Truncate(string s, int max) =>
        max <= 0 ? string.Empty : s.Length <= max ? s : s[..max];

    private sealed class QuestionState
    {
        public int Cursor;
        public readonly HashSet<int> Selected = [];
        public readonly System.Text.StringBuilder Custom = new();
    }

    private readonly record struct Row(string Text, CellStyle Style, bool Pad = false, bool IsStrip = false, int OptionIndex = -1);
}
