// #2379: a domain-free Accordion primitive from gpui-kit-components.md.
//
// ICollapsibleChatBlock is a chat-block mixin for ONE block with ONE body; it
// is not a container and knows nothing about several sections sharing an
// expansion policy. This type deliberately does NOT implement it. It adds a
// container that holds N sections and delegates the expanded set to the host:
// the widget never owns a second copy of that state. No new extension axis,
// no chat/session/plugin wiring.

using System.Text;

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>
/// Expansion policy of an <see cref="Accordion"/>: at most one section open,
/// or any number of sections open at once.
/// </summary>
public enum AccordionPolicy
{
    /// <summary>Opening a section closes every other one.</summary>
    Single,

    /// <summary>Each section opens and closes independently.</summary>
    Multiple,
}

/// <summary>
/// One section of an <see cref="Accordion"/>: a stable id, a header title and
/// the lines painted under the header while the section is expanded.
/// </summary>
public readonly record struct AccordionSection
{
    /// <summary>Copies the id and title and takes a defensive copy of the body lines.</summary>
    public AccordionSection(string id, string title, IReadOnlyList<string>? body = null)
    {
        Id = id ?? string.Empty;
        Title = title ?? string.Empty;
        Body = body is null ? Array.Empty<string>() : [.. body];
    }

    /// <summary>Stable identity of the section in the host's expanded set.</summary>
    public string Id { get; }

    /// <summary>Header text.</summary>
    public string Title { get; }

    /// <summary>Lines shown only while this section is expanded.</summary>
    public IReadOnlyList<string> Body { get; }
}

/// <summary>
/// Host-controlled container of expandable sections. The host owns the set of
/// expanded section ids: the widget reads it through <c>getExpanded</c> and
/// announces the whole next set through <c>onExpandedChange</c>. It stores no
/// expanded state of its own, so a host that rejects a change stays the single
/// source of truth.
/// </summary>
/// <remarks>
/// <para>
///     <see cref="Policy"/> decides what a toggle announces. Under
///     <see cref="AccordionPolicy.Single"/> the next set is either empty (the
///     open section was toggled shut) or the single toggled id; under
///     <see cref="AccordionPolicy.Multiple"/> the id is added or removed and
///     every other id is left alone.
/// </para>
/// <para>
///     The type is an <see cref="IFocusTarget"/>, so a host adds it to a
///     <c>FocusRouter</c> Tab order. Navigation and activation use the strict
///     command gate (press/repeat, no modifiers): Up/Left and k/h move the
///     cursor back, Down/Right and j/l forward, Home/End jump to the ends, and
///     Enter or Space toggles the focused section. It is declared as a Command
///     site in the key-gate inventory.
/// </para>
/// </remarks>
public sealed class Accordion : IFocusTarget
{
    private AccordionSection[] _sections;
    private readonly Func<IReadOnlySet<string>> _getExpanded;
    private readonly Action<IReadOnlySet<string>> _onExpandedChange;
    private int _cursor;
    private bool _disabled;

    /// <summary>Creates an accordion over a host-owned expanded set.</summary>
    public Accordion(
        string id,
        IReadOnlyList<AccordionSection> sections,
        AccordionPolicy policy,
        Func<IReadOnlySet<string>> getExpanded,
        Action<IReadOnlySet<string>> onExpandedChange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(getExpanded);
        ArgumentNullException.ThrowIfNull(onExpandedChange);

        Id = id;
        _sections = [.. sections];
        Policy = policy;
        _getExpanded = getExpanded;
        _onExpandedChange = onExpandedChange;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <summary>The expansion policy applied to every toggle.</summary>
    public AccordionPolicy Policy { get; }

    /// <summary>The sections, in paint and navigation order.</summary>
    public IReadOnlyList<AccordionSection> Sections => _sections;

    /// <summary>The host's expanded set, read straight from the getter.</summary>
    public IReadOnlySet<string> Expanded => _getExpanded();

    /// <summary>Whether a section id is currently expanded.</summary>
    public bool IsExpanded(string id) => _getExpanded().Contains(id);

    /// <summary>Index of the section the cursor rests on; clamped to the list.</summary>
    public int Cursor => _sections.Length == 0 ? 0 : Math.Clamp(_cursor, 0, _sections.Length - 1);

    /// <summary>Whether this entry may own focus right now.</summary>
    public bool CanFocus => !_disabled;

    /// <summary>Whether this entry currently owns focus.</summary>
    public bool IsFocused { get; private set; }

    /// <summary>
    /// Whether keyboard input is disabled. A disabled accordion reports
    /// <see cref="CanFocus"/> = false and refuses every key, so no toggle can
    /// be announced for it.
    /// </summary>
    public bool Disabled
    {
        get => _disabled;
        set
        {
            if (_disabled == value)
            {
                return;
            }

            _disabled = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when focus, cursor, sections or enabled state change so a host can repaint.</summary>
    public event EventHandler? Changed;

    /// <inheritdoc />
    public void OnFocusChanged(bool focused)
    {
        IsFocused = focused;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the sections (copied) and clamps the cursor into the new list.</summary>
    public void SetSections(IReadOnlyList<AccordionSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        _sections = [.. sections];
        _cursor = Math.Clamp(_cursor, 0, Math.Max(_sections.Length - 1, 0));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Routes one key through the command gate (press/repeat, no modifiers).
    /// Arrows and vim keys move the cursor, Home/End jump to the ends, and
    /// Enter or Space toggles the focused section. Returns true when consumed;
    /// a disabled accordion and all modified keys are left to the host.
    /// </summary>
    public bool HandleKey(in KeyEvent key)
    {
        if (_disabled
            || key.EventType is not (KeyEventType.Press or KeyEventType.Repeat)
            || !key.Modifiers.IsUnmodified())
        {
            return false;
        }

        switch (key.Key)
        {
            case KeyCode.Up:
            case KeyCode.Left:
                return Move(-1);
            case KeyCode.Down:
            case KeyCode.Right:
                return Move(1);
            case KeyCode.Home:
                return MoveTo(0);
            case KeyCode.End:
                return MoveTo(_sections.Length - 1);
            case KeyCode.Enter:
                return Toggle(Cursor);
            case KeyCode.Char:
                return HandleRune(key.Character);
            default:
                return false;
        }
    }

    /// <summary>
    /// Toggles one section and announces the whole next expanded set. Never
    /// mutates a local copy of the set: a controlled host stays in charge.
    /// Returns false for a disabled widget or an out-of-range index.
    /// </summary>
    public bool Toggle(int index)
    {
        if (_disabled || index < 0 || index >= _sections.Length)
        {
            return false;
        }

        var next = new HashSet<string>(_getExpanded(), StringComparer.Ordinal);
        string id = _sections[index].Id;
        bool wasExpanded = next.Contains(id);

        if (Policy == AccordionPolicy.Single)
        {
            next.Clear();
            if (!wasExpanded)
            {
                next.Add(id);
            }
        }
        else if (wasExpanded)
        {
            next.Remove(id);
        }
        else
        {
            next.Add(id);
        }

        _onExpandedChange(next);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Paints each section header and, while expanded, its body lines inside
    /// <paramref name="rect"/>. The cursor gets a <c>&gt;</c> marker, an open
    /// section a <c>-</c> marker and a closed one a <c>+</c> marker. Output is
    /// clipped to the rect and stops at its bottom edge.
    /// </summary>
    public void Paint(ScreenBuffer buffer, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var headerStyle = new CellStyle(ChatPalette.Text, attrs: _disabled ? StyleAttr.Dim : StyleAttr.None);
        var cursorStyle = new CellStyle(ChatPalette.Accent, attrs: _disabled ? StyleAttr.Dim : StyleAttr.Bold);
        CellStyle bodyStyle = ChatPalette.Dim;
        int bottom = rect.Y + rect.Height;

        for (int i = 0; i < _sections.Length; i++)
        {
            int y = rect.Y + ActualCursorOffset(i);
            if (y >= bottom)
            {
                return;
            }

            AccordionSection section = _sections[i];
            bool expanded = IsExpanded(section.Id);
            string prefix = i == Cursor ? "> " : "  ";
            string header = prefix + (expanded ? "- " : "+ ") + section.Title;
            Write(buffer, rect.X, y, header, rect.Width, i == Cursor ? cursorStyle : headerStyle);

            if (!expanded)
            {
                continue;
            }

            for (int line = 0; line < section.Body.Count; line++)
            {
                int bodyY = y + line + 1;
                if (bodyY >= bottom)
                {
                    return;
                }

                Write(buffer, rect.X, bodyY, "    " + section.Body[line], rect.Width, bodyStyle);
            }
        }
    }

    // Headers and bodies of earlier sections shift later ones down. Computed
    // from the host-owned expanded set so the paint matches an external change
    // without the widget caching anything.
    private int ActualCursorOffset(int index)
    {
        int offset = 0;
        for (int i = 0; i < index; i++)
        {
            offset += 1;
            if (IsExpanded(_sections[i].Id))
            {
                offset += _sections[i].Body.Count;
            }
        }

        return offset;
    }

    private bool HandleRune(Rune rune) => rune.Value switch
    {
        'k' or 'h' => Move(-1),
        'j' or 'l' => Move(1),
        ' ' => Toggle(Cursor),
        _ => false,
    };

    private bool Move(int delta)
    {
        if (_sections.Length == 0 || delta == 0)
        {
            return false;
        }

        // The key is consumed at the edges, like the other primitives: a host
        // sees a handled navigation key even when the cursor cannot move.
        _cursor = Math.Clamp(_cursor + delta, 0, _sections.Length - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool MoveTo(int index)
    {
        if (_sections.Length == 0)
        {
            return false;
        }

        _cursor = Math.Clamp(index, 0, _sections.Length - 1);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static void Write(ScreenBuffer buffer, int x, int y, string text, int width, in CellStyle style)
    {
        string clipped = text.Length > width ? text[..width] : text;
        buffer.SetText(x, y, clipped, style);
    }
}
