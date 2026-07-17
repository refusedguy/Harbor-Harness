using Spectre.Tui;
using Spectre.Tui.App;
using System.Linq;

namespace Harbor.Tui.SpectreTui;

/// <summary>
///     All interactive actions the chat screen understands, decoupled from the
///     raw key that triggers them. The actual key bindings live in
///     <see cref="ChatKeyMap" /> so they can be documented and rendered from one place.
/// </summary>
public enum ChatAction
{
    None,
    Quit,
    Abort,
    Submit,
    ToggleFocus,
    ScrollUpLine,
    ScrollDownLine,
    ScrollUpPage,
    ScrollDownPage,
    ScrollTop,
    ScrollBottom,
    InputHistoryPrev,
    InputHistoryNext,
    Autocomplete,
    Backspace,
    Clear,
    Char,
}

/// <summary>
///     Central registry of key bindings + human-readable labels for the chat
///     screen. Both the input handler (<see cref="SpectreTuiRenderer.ChatScreen" />)
///     and the footer/help text (<see cref="Helpers.LayoutBuilder" />) read from here,
///     so there is a single source of truth for "what key does what".
/// </summary>
public sealed class ChatKeyMap
{
    /// <summary>Match spec: a key, optionally gated by required modifiers.</summary>
    public readonly record struct Binding(Key Key, KeyModifier Modifiers = KeyModifier.None)
    {
        public bool Matches(IKeyInfo key)
            => key.Key == Key && key.Modifiers.HasFlag(Modifiers);
    }

    /// <summary>One documented action: its binding(s) and the label shown in the UI.</summary>
    public sealed record Entry(ChatAction Action, string Label, params Binding[] Bindings);

    private readonly Entry[] _entries =
    [
        new(ChatAction.Quit, "quit", new Binding(Key.Escape)),
        new(ChatAction.Abort, "abort", new Binding(Key.Escape)),
        new(ChatAction.Submit, "send", new Binding(Key.Enter)),
        new(ChatAction.ToggleFocus, "focus", new Binding(Key.F2)),
        new(ChatAction.ScrollUpLine, "up", new Binding(Key.Up)),
        new(ChatAction.ScrollDownLine, "down", new Binding(Key.Down)),
        new(ChatAction.ScrollUpPage, "page up", new Binding(Key.PageUp)),
        new(ChatAction.ScrollDownPage, "page down", new Binding(Key.PageDown)),
        new(ChatAction.ScrollTop, "top", new Binding(Key.Home)),
        new(ChatAction.ScrollBottom, "bottom", new Binding(Key.End)),
        new(ChatAction.InputHistoryPrev, "prev input", new Binding(Key.Up, KeyModifier.Alt)),
        new(ChatAction.InputHistoryNext, "next input", new Binding(Key.Down, KeyModifier.Alt)),
        new(ChatAction.Autocomplete, "complete", new Binding(Key.Tab)),
        new(ChatAction.Backspace, "backspace", new Binding(Key.Backspace)),
        // Clear is bound to Ctrl+L, which the framework reports as a character — handled separately.
        new(ChatAction.Clear, "clear"),
    ];

    private readonly Dictionary<ChatAction, Entry> _byAction;

    public ChatKeyMap()
    {
        _byAction = _entries.ToDictionary(e => e.Action);
    }

    /// <summary>Resolve a key press to an action (first matching entry wins).</summary>
    public ChatAction Resolve(IKeyInfo key)
    {
        var match = _entries.FirstOrDefault(e => e.Bindings.Any(b => b.Matches(key)));
        return match != null ? match.Action : ChatAction.None;
    }

    /// <summary>Get the documented entry for an action.</summary>
    public Entry Get(ChatAction action) => _byAction[action];

    /// <summary>All documented actions (used to render footer/help).</summary>
    public IReadOnlyList<Entry> All => _entries;
}
