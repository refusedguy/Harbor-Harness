// InputHistoryBuffer.cs — issue #2681, item G02.
//
// WHAT THIS CLOSES
// ----------------
// The Avalonia composer is a plain TextBox: Enter sends the current line and
// clears it, but nothing remembers what was sent. Alt+Up / Alt+Down therefore
// have nothing to walk through, even though the shared UI state already models
// history in `InputModel` for the terminal renderers (`ChatAction.InputHistoryPrev`
// / `InputHistoryNext`). The Avalonia input path is local (the TextBox owns the
// text and the VM clears it on send), so it needs a small local buffer rather
// than a synthetic key event into the store.
//
// The buffer is deliberately a pure type with no Avalonia dependency. That keeps
// the history semantics (duplicate suppression, draft restore, cursor bounds)
// testable without standing up a window, and keeps the view-model thin.

namespace Harbor.App.Avalonia.Services;

/// <summary>
///     #2681 G02: most-recent-last prompt history for the Avalonia composer.
///     <see cref="Add" /> is called when a prompt is submitted;
///     <see cref="TryNavigateUp" /> / <see cref="TryNavigateDown" /> move a
///     cursor through the entries and return the text the input box should show.
/// </summary>
/// <remarks>
///     Walking down past the newest entry restores the draft the user had typed
///     before the first Alt+Up, so history navigation is non-destructive. Repeated
///     submissions of the same text are collapsed: the newest copy is the one the
///     user just sent, and an older identical entry adds no information.
/// </remarks>
public sealed class InputHistoryBuffer
{
    private readonly List<string> _entries = new();
    private int _cursor = -1;
    private string _draft = string.Empty;

    /// <summary>Oldest-first snapshot of submitted prompts.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Index into <see cref="Entries" />, or -1 when the composer is live.</summary>
    public int Cursor => _cursor;

    /// <summary>Record a submitted prompt. Blank and repeated prompts are ignored.</summary>
    /// <param name="text">The submitted prompt (may carry trailing newlines from the TextBox).</param>
    public void Add(string text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0) return;
        if (_entries.Count > 0 && string.Equals(_entries[^1], trimmed, StringComparison.Ordinal))
        {
            Reset();
            return;
        }

        _entries.Add(trimmed);
        Reset();
    }

    /// <summary>
    ///     Move toward older prompts. On the first step the current input text is
    ///     saved as the draft that <see cref="TryNavigateDown" /> will restore.
    /// </summary>
    /// <param name="currentText">Text currently in the input box.</param>
    /// <param name="text">The text to put in the input box. Valid only when this returns true.</param>
    /// <returns>True when the input box must be updated.</returns>
    public bool TryNavigateUp(string currentText, out string text)
    {
        if (_entries.Count == 0)
        {
            text = currentText;
            return false;
        }

        if (_cursor < 0) _draft = currentText ?? string.Empty;
        _cursor = _cursor < 0 ? _entries.Count - 1 : Math.Max(0, _cursor - 1);
        text = _entries[_cursor];
        return true;
    }

    /// <summary>
    ///     Move toward newer prompts. Past the newest entry the saved draft is
    ///     restored and the cursor goes back to the live state.
    /// </summary>
    /// <param name="text">The text to put in the input box (empty when there was no draft).</param>
    /// <returns>True when the input box must be updated.</returns>
    public bool TryNavigateDown(out string text)
    {
        if (_cursor < 0)
        {
            text = string.Empty;
            return false;
        }

        _cursor++;
        if (_cursor >= _entries.Count)
        {
            text = _draft;
            _draft = string.Empty;
            _cursor = -1;
            return true;
        }

        text = _entries[_cursor];
        return true;
    }

    /// <summary>Return to the live (uncursored) state without dropping history.</summary>
    public void Reset()
    {
        _cursor = -1;
        _draft = string.Empty;
    }
}
