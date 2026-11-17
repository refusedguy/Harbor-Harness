using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// O7 (#1482): the queue dock's dialog half, seated entirely on seams that
/// already exist. The visible counter lives in <see cref="ComposerPanel"/> and
/// reads <see cref="ComposerController.QueuedCounterText"/> directly; when the
/// user opens the queue, this coordinator shows the waiting prompts oldest-first
/// through the stock <see cref="DialogOverlay"/> and pushes the stock
/// <see cref="DialogOverlayLayer"/> onto the screen's <see cref="OverlayStack"/>.
/// <para>
/// No new extension axis (#555 freeze): this is a plain object over the
/// composer (queue state), the existing dialog widget and the existing overlay
/// seat. It adds no interface, no <c>IPlugin</c>/<c>IPanelRegistry</c> route and
/// no key-plane fact row — nothing for the architecture census to grow.
/// </para>
/// <para>
/// Queue semantics are untouched. "Undo newest" is the composer's existing
/// <see cref="ComposerController.TryUndoQueued"/> plus
/// <see cref="ComposerController.RestoreToComposer"/>; "delete newest" is the
/// same take with the payload discarded (no restore).
/// </para>
/// </summary>
public sealed class QueuedPromptDock
{
    private readonly ComposerController _composer;
    private readonly DialogOverlay _dialog = new();
    private readonly DialogOverlayLayer _layer;

    public QueuedPromptDock(ComposerController composer)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _layer = new DialogOverlayLayer(_dialog);
    }

    /// <summary>The composer-backed dock counter (null when the queue is empty).</summary>
    public string? CounterText => _composer.QueuedCounterText();

    /// <summary>The stock dialog model this dock drives.</summary>
    public DialogOverlay Dialog => _dialog;

    /// <summary>The stock overlay seat hosting <see cref="Dialog"/>.</summary>
    public DialogOverlayLayer Layer => _layer;

    public bool IsOpen => _dialog.Visible;

    /// <summary>
    /// Opens the queue dialog with the waiting prompts oldest-first. A no-op
    /// (dismiss) on an empty queue — the counter is the only rail state then.
    /// </summary>
    public void Open()
    {
        var prompts = _composer.QueuedPrompts;
        if (prompts.Count == 0)
        {
            _dialog.Dismiss();
            return;
        }

        _dialog.ShowSelect(
            title: "Queued prompts",
            message: "Oldest first. Undo returns the newest to the composer; delete discards it.",
            options: prompts,
            selectedIndex: prompts.Count - 1,
            okLabel: "Undo newest",
            cancelLabel: "Delete newest");
    }

    /// <summary>
    /// Undo the newest entry: drop it and hand its text back to the composer
    /// (caret at the end). Refreshes the dialog for the remaining entries, or
    /// closes it when the queue drains.
    /// </summary>
    public bool UndoNewest()
    {
        if (!_composer.TryUndoQueued(out var text))
        {
            return false;
        }

        _composer.RestoreToComposer(text);
        AfterChange();
        return true;
    }

    /// <summary>
    /// Delete the newest entry without returning its text to the composer.
    /// Refreshes the dialog for the remaining entries, or closes it when the
    /// queue drains.
    /// </summary>
    public bool DeleteNewest()
    {
        if (!_composer.TryUndoQueued(out _))
        {
            return false;
        }

        AfterChange();
        return true;
    }

    public void Close() => _dialog.Dismiss();

    private void AfterChange()
    {
        if (_composer.QueuedCount == 0)
        {
            _dialog.Dismiss();
        }
        else
        {
            Open();
        }
    }
}
