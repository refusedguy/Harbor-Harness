using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.State;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Autocomplete trigger modes stolen from opencode's prompt layer
/// (<c>component/prompt/autocomplete.tsx</c> + <c>prompt/display.ts</c>
/// slash/mention trigger-index + <c>prompt/directory-completion.ts</c>):
/// <c>command</c> (<c>/</c>), <c>reference</c> (<c>@</c>) and
/// <c>directory</c> (a path token containing <c>/</c>).
/// </summary>
public enum AutocompleteMode : byte
{
    /// <summary>No trigger under the caret — the palette stays closed.</summary>
    None = 0,

    /// <summary><c>/</c> at a word start — slash-command completion.</summary>
    Command = 1,

    /// <summary><c>@</c> at a word start — file/context reference completion.</summary>
    Reference = 2,

    /// <summary>Path token containing <c>/</c> (<c>./x</c>, <c>~/x</c>, <c>dir/sub</c>).</summary>
    Directory = 3,
}

/// <summary>
/// Anchor of an open autocomplete session: the trigger position plus the
/// query after it. For <see cref="AutocompleteMode.Directory" /> the anchor
/// is the path-token start (no trigger char); otherwise it is the trigger
/// char and the query starts one cell later (<see cref="QueryStart" />).
/// </summary>
/// <param name="Mode">Which completion list to show.</param>
/// <param name="AnchorIndex">Char offset of the trigger (or token start for directory).</param>
/// <param name="QueryLength">Chars from the query start to the caret.</param>
public readonly record struct AutocompleteTrigger(AutocompleteMode Mode, int AnchorIndex, int QueryLength)
{
    /// <summary>No-trigger sentinel.</summary>
    public static readonly AutocompleteTrigger None = new(AutocompleteMode.None, -1, 0);

    /// <summary>Char offset where the query text starts.</summary>
    public int QueryStart => Mode == AutocompleteMode.Directory ? AnchorIndex : AnchorIndex + 1;

    /// <summary>
    /// Copies the query slice out of <paramref name="text" /> (commit/filter path).
    /// Returns empty when the slice no longer fits (buffer edited past the anchor).
    /// </summary>
    public string ExtractQuery(ReadOnlySpan<char> text)
    {
        if (Mode == AutocompleteMode.None || QueryLength <= 0)
        {
            return string.Empty;
        }

        int start = QueryStart;
        if (start < 0 || start + QueryLength > text.Length)
        {
            return string.Empty;
        }

        return new string(text.Slice(start, QueryLength));
    }
}

/// <summary>
/// Unified trigger-scan for the composer autocomplete (opencode
/// <c>slashTriggerIndex</c>/<c>mentionTriggerIndex</c> semantics in one pass):
/// command and reference triggers win at word starts, a slash-bearing path
/// token falls back to directory mode, everything else leaves the palette
/// closed. Pure, BCL-only, zero-alloc — safe to call per keystroke; hosts
/// throttle re-scans through <see cref="AnchorRescanIntervalMs" />.
/// </summary>
public static class AutocompleteTriggerScan
{
    /// <summary>
    /// Minimum age of an anchor before it is re-scanned (opencode tracks the
    /// anchor on a ~50ms cadence while the menu is open).
    /// </summary>
    public const int AnchorRescanIntervalMs = 50;

    /// <summary>
    /// Whether <paramref name="nowMs" /> is far enough past
    /// <paramref name="lastScanMs" /> (monotonic milliseconds, e.g.
    /// <see cref="Environment.TickCount64" />) to re-scan the anchor.
    /// </summary>
    public static bool ShouldRescan(long lastScanMs, long nowMs) =>
        (nowMs - lastScanMs) >= AnchorRescanIntervalMs;

    /// <summary>
    /// Scans <paramref name="text" /> before <paramref name="cursor" /> for an
    /// autocomplete trigger. Only text left of the caret counts, so typing
    /// mid-line never opens the palette for text right of the caret.
    /// </summary>
    /// <returns>True plus the trigger, or false when the palette stays closed.</returns>
    public static bool TryScan(ReadOnlySpan<char> text, int cursor, out AutocompleteTrigger trigger)
    {
        trigger = AutocompleteTrigger.None;
        if (cursor <= 0 || cursor > text.Length)
        {
            return false;
        }

        // Pass 1: strict word chars — a '@' or '/' right before the word with
        // a word boundary in front of it is a reference/command trigger.
        int i = cursor - 1;
        while (i >= 0 && IsQueryChar(text[i]))
        {
            i--;
        }

        int wordStart = i + 1;
        if (wordStart > 0)
        {
            char t = text[wordStart - 1];
            if ((t == '@' || t == '/') && IsBoundary(text, wordStart - 1))
            {
                trigger = new AutocompleteTrigger(
                    t == '@' ? AutocompleteMode.Reference : AutocompleteMode.Command,
                    wordStart - 1,
                    cursor - wordStart);
                return true;
            }
        }

        // Pass 2: path chars — a token containing '/' is a directory query
        // (covers "./x", "../x", "~/x", "dir/sub" and mid-word "a/b").
        int j = cursor - 1;
        while (j >= 0 && IsPathChar(text[j]))
        {
            j--;
        }

        int pathStart = j + 1;
        for (int k = pathStart; k < cursor; k++)
        {
            if (text[k] == '/')
            {
                trigger = new AutocompleteTrigger(AutocompleteMode.Directory, pathStart, cursor - pathStart);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Keymap isolation while the menu is open (opencode closes the keymap
    /// around the menu): navigation/commit/cancel plus query editing belong
    /// to the palette; Ctrl/Alt chords and function keys fall through to the
    /// host so global bindings keep working.
    /// </summary>
    public static bool ShouldConsumeWhileOpen(UiKey key) => key.Code switch
    {
        UiKeyCode.Up or UiKeyCode.Down or UiKeyCode.Enter
            or UiKeyCode.Escape or UiKeyCode.Tab or UiKeyCode.Backspace => true,
        UiKeyCode.Char => key.Mods.AcceptsTypedChar()
            && key.Character is { } c
            && !char.IsControl(c),
        _ => false,
    };

    private static bool IsQueryChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '-' or '.';

    private static bool IsPathChar(char c) =>
        IsQueryChar(c) || c is '/' or '~';

    private static bool IsBoundary(ReadOnlySpan<char> text, int triggerPos) =>
        triggerPos <= 0 || char.IsWhiteSpace(text[triggerPos - 1]);
}

/// <summary>
/// <see cref="PromptBuffer" /> entry point for the trigger scan: reads the
/// live span plus caret without materialising the draft string.
/// </summary>
public static class PromptBufferAutocompleteExtensions
{
    /// <summary>
    /// Scans the buffer content before the caret (see
    /// <see cref="AutocompleteTriggerScan.TryScan" />). The span is consumed
    /// synchronously, so the next edit cannot invalidate it mid-scan.
    /// </summary>
    public static bool TryScanAutocomplete(this PromptBuffer buffer, out AutocompleteTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return AutocompleteTriggerScan.TryScan(buffer.AsSpan(), buffer.Cursor, out trigger);
    }
}
