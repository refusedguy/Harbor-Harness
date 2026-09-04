using System.Text;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// CF-B-005: input history through the store. Up/Down arrive as
/// <see cref="InputMsg.HistoryUp"/> / <see cref="InputMsg.HistoryDown"/>
/// (see <c>InputModel.cs</c>) and are applied to the
/// <see cref="PromptHistory"/> walk: the in-flight draft is saved on the
/// first Up and restored exactly once by the final Down (readline
/// semantics); the caret follows the store sync rule
/// (<c>SyncInputFromState</c>: end of text). Covers draft save/restore,
/// walk boundaries, and the MRU cap.
/// </summary>
public class InputHistoryTests
{
    private static KeyEvent CharKey(char c, KeyModifiers mods = KeyModifiers.None) =>
        KeyEvent.Char(new Rune(c), mods);

    private static readonly KeyEvent Up = KeyEvent.Simple(KeyCode.Up);
    private static readonly KeyEvent Down = KeyEvent.Simple(KeyCode.Down);

    /// <summary>Type text without submitting (stays the in-flight draft).</summary>
    private static void TypeDraft(ComposerController composer, string text)
    {
        foreach (var c in text)
        {
            _ = composer.HandleKey(CharKey(c));
        }
    }

    /// <summary>Type text + Enter, mimicking the host's post-submit TakeText.</summary>
    private static void TypeAndSubmit(ComposerController composer, string text)
    {
        TypeDraft(composer, text);
        _ = composer.HandleKey(KeyEvent.Simple(KeyCode.Enter));
        _ = composer.Buffer.TakeText();
    }

    /// <summary>Build a store-side <see cref="InputModel"/> holding the same submitted lines.</summary>
    private static InputModel StoreWith(params string[] entries)
    {
        var store = InputModel.Empty;
        foreach (var entry in entries)
        {
            store = store.SetText(entry).Consume().Next;
        }

        return store;
    }

    [Test]
    public async Task Up_SavesDraft_FirstStroke_Down_RestoresExactlyOnce()
    {
        var composer = new ComposerController();
        TypeAndSubmit(composer, "one");
        TypeAndSubmit(composer, "two");
        TypeDraft(composer, "wip draft");

        _ = composer.HandleKey(Up);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("two");
        await Assert.That(composer.IsRecalling).IsTrue();

        _ = composer.HandleKey(Up);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("one");

        _ = composer.HandleKey(Down);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("two");

        // Final Down restores the saved draft exactly once...
        _ = composer.HandleKey(Down);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("wip draft");
        await Assert.That(composer.IsRecalling).IsFalse();

        // ...and the walk has ended: further Down is plain caret movement,
        // the draft is NOT clobbered or duplicated.
        _ = composer.HandleKey(Down);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("wip draft");
    }

    [Test]
    public async Task Up_Matches_Store_HistoryUp_Transition()
    {
        var composer = new ComposerController();
        TypeAndSubmit(composer, "one");
        TypeAndSubmit(composer, "two");
        TypeDraft(composer, "draft");

        _ = composer.HandleKey(Up);

        var store = StoreWith("one", "two").SetText("draft");
        var viaStore = InputMsg.Update(store, new InputMsg.HistoryUp());

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo(viaStore.Text);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("two");
    }

    [Test]
    public async Task Down_RestoresDraft_WhereStoreAlone_WouldLoseIt()
    {
        // InputModel.NavigateDown restores empty; the PromptHistory-owned walk
        // (CF-B-005) restores the saved draft instead — readline semantics.
        var composer = new ComposerController();
        TypeAndSubmit(composer, "one");
        TypeAndSubmit(composer, "two");
        TypeDraft(composer, "keep me");

        _ = composer.HandleKey(Up);
        _ = composer.HandleKey(Up);
        _ = composer.HandleKey(Down);
        _ = composer.HandleKey(Down);

        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("keep me");

        var store = StoreWith("one", "two").SetText("keep me");
        store = InputMsg.Update(store, new InputMsg.HistoryUp());
        store = InputMsg.Update(store, new InputMsg.HistoryUp());
        store = InputMsg.Update(store, new InputMsg.HistoryDown());
        var lastDown = InputMsg.Update(store, new InputMsg.HistoryDown());
        await Assert.That(lastDown.Text).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Boundaries_OldestUp_KeepsEntry_IdleDown_KeepsDraft()
    {
        var composer = new ComposerController();
        TypeAndSubmit(composer, "only");
        TypeDraft(composer, "draft");

        _ = composer.HandleKey(Up);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("only");

        // Oldest boundary: further Up keeps the oldest entry (caret move only).
        _ = composer.HandleKey(Up);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("only");
        await Assert.That(composer.IsRecalling).IsTrue();

        // Down from the newest entry restores the draft and ends the walk.
        _ = composer.HandleKey(Down);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft");
        await Assert.That(composer.IsRecalling).IsFalse();

        // Idle (not walking): Down leaves the buffer alone.
        var idle = new ComposerController();
        TypeAndSubmit(idle, "only");
        TypeDraft(idle, "draft");
        _ = idle.HandleKey(Down);
        await Assert.That(idle.Buffer.SnapshotText()).IsEqualTo("draft");
        await Assert.That(idle.IsRecalling).IsFalse();
    }

    [Test]
    public async Task EmptyHistory_UpDown_KeepBuffer()
    {
        var composer = new ComposerController();
        TypeDraft(composer, "draft");

        _ = composer.HandleKey(Up);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft");
        _ = composer.HandleKey(Down);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft");
        await Assert.That(composer.IsRecalling).IsFalse();
    }

    [Test]
    public async Task Cap_Default50_EvictsOldest_MruOrder()
    {
        await Assert.That(PromptHistory.DefaultCapacity).IsEqualTo(50);

        var history = new PromptHistory();
        for (int i = 0; i < 55; i++)
        {
            history.Push($"item-{i:00}");
        }

        await Assert.That(history.Count).IsEqualTo(50);

        // Newest recalled first, oldest surviving entry is item-05.
        await Assert.That(history.TryRecallPrevious("draft", out var newest)).IsTrue();
        await Assert.That(newest).IsEqualTo("item-54");

        string oldest = string.Empty;
        while (history.TryRecallPrevious(string.Empty, out var entry))
        {
            oldest = entry;
        }

        await Assert.That(oldest).IsEqualTo("item-05");

        // Walk back forward: newest again, then the captured draft exactly once.
        string last = string.Empty;
        int steps = 0;
        while (history.TryRecallNext(out var entry))
        {
            last = entry;
            steps++;
        }

        await Assert.That(steps).IsEqualTo(50);
        await Assert.That(last).IsEqualTo("draft");
        await Assert.That(history.TryRecallNext(out _)).IsFalse();
    }

    [Test]
    public async Task Recall_PinsCursor_ToEnd_LikeStoreSync()
    {
        // SyncInputFromState rule: a text change pins the caret to the end of
        // the text. Recall must leave the composer caret there too.
        var composer = new ComposerController();
        TypeAndSubmit(composer, "one");
        TypeAndSubmit(composer, "two");
        TypeDraft(composer, "draft");

        _ = composer.HandleKey(Up);
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(composer.Buffer.Length);

        _ = composer.HandleKey(Down);
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("draft");
        await Assert.That(composer.Buffer.Cursor).IsEqualTo(composer.Buffer.Length);
    }
}
