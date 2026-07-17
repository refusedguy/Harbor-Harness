using System.Collections.Immutable;

namespace Harbor.Tui.Abstractions.State;

/// <summary>
///     Pure input/editing model for the interactive prompt. Mirrors the behaviour
///     of the previous per-renderer <c>InputState</c> but is immutable and
///     renderer-agnostic. Renderers project this into their own widgets and feed
///     keystrokes back as <see cref="InputMsg" />.
/// </summary>
public sealed record InputModel(
    string Text,
    ImmutableArray<string> History,
    int HistoryIndex)
{
    public static readonly InputModel Empty = new(string.Empty, ImmutableArray<string>.Empty, -1);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    public InputModel Append(char c) =>
        this with { Text = Text + c, HistoryIndex = -1 };

    public InputModel Backspace() =>
        Text.Length == 0 ? this : this with { Text = Text[..^1], HistoryIndex = -1 };

    public InputModel Clear() => this with { Text = string.Empty, HistoryIndex = -1 };

    public InputModel SetText(string text) => this with { Text = text, HistoryIndex = -1 };

    /// <summary>Consume the current line into history; returns the submitted text or null.</summary>
    public (InputModel Next, string? Submitted) Consume()
    {
        var trimmed = Text.Trim();
        if (trimmed.Length == 0)
            return (this with { Text = string.Empty, HistoryIndex = -1 }, null);
        return (new InputModel(string.Empty, History.Add(trimmed), -1), trimmed);
    }

    public InputModel NavigateUp()
    {
        if (History.IsDefaultOrEmpty) return this;
        var i = HistoryIndex < 0 ? History.Length - 1 : Math.Max(0, HistoryIndex - 1);
        return this with { HistoryIndex = i, Text = History[i] };
    }

    public InputModel NavigateDown()
    {
        if (HistoryIndex < 0) return this;
        var i = HistoryIndex + 1;
        if (i >= History.Length)
            return this with { HistoryIndex = -1, Text = string.Empty };
        return this with { HistoryIndex = i, Text = History[i] };
    }
}

/// <summary>
///     Messages driving <see cref="InputModel" /> transitions, emitted by a
///     renderer's key handler. Kept separate so the editing logic is testable
///     without a terminal.
/// </summary>
public abstract record InputMsg
{
    public sealed record Char(char Value) : InputMsg;
    public sealed record Backspace : InputMsg;
    public sealed record Clear : InputMsg;
    public sealed record HistoryUp : InputMsg;
    public sealed record HistoryDown : InputMsg;
    public sealed record Autocomplete(ImmutableArray<string> SlashCommands) : InputMsg;
    public sealed record Submit : InputMsg;

    /// <summary>Pure transition function for the input model.</summary>
    public static InputModel Update(InputModel state, InputMsg msg) => msg switch
    {
        Char c => state.Append(c.Value),
        Backspace => state.Backspace(),
        Clear => state.Clear(),
        HistoryUp => state.NavigateUp(),
        HistoryDown => state.NavigateDown(),
        Submit => state,
        Autocomplete a => AutocompleteSlash(state, a.SlashCommands),
        _ => state
    };

    private static InputModel AutocompleteSlash(InputModel state, ImmutableArray<string> slash)
    {
        if (!state.Text.StartsWith('/')) return state;
        var match = slash.FirstOrDefault(c => c.StartsWith(state.Text, StringComparison.OrdinalIgnoreCase));
        return match is null ? state : state.SetText(match + " ");
    }
}
