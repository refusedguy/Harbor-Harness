// SessionPromptHistoryReplTests.cs — issue #3111, atomic slice 7.
//
// SessionPromptHistory was a bounded, session-keyed store with zero callers in
// src/ or apps/. The REPL now records every submitted line against the active
// SessionId and refills the composer's readline rail from that store whenever
// the session changes. These tests drive the same helpers the input loop calls
// and read the composer buffer a terminal would paint, proving that Up/Down
// cannot surface a prompt from another session.

using Harbor.Abstractions.Models.Identifiers;
using Harbor.App.Cli.Repl;
using Harbor.Tools.Builtin;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Ui.Framework.Rendering.Input;

namespace Harbor.App.Cli.Tests;

public class SessionPromptHistoryReplTests
{
    [Test]
    public async Task UpDownRecall_IsIsolatedPerSession()
    {
        const string sessionA = "session-A";
        const string sessionB = "session-B";
        var store = new SessionPromptHistory();
        var composer = new ComposerController();

        // Two submits in A, recorded and bound exactly as the input loop does.
        ReplInputLoop.RecordSubmittedPrompt(store, sessionA, "  first prompt  ");
        ReplInputLoop.RecordSubmittedPrompt(store, sessionA, "second prompt");
        ReplInputLoop.PopulatePromptHistory(composer, store, sessionA);

        await Assert.That(composer.History.Count).IsEqualTo(2);

        // Newest first on the first Up, older on the next.
        composer.Buffer.Clear();
        composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("second prompt");
        composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("first prompt");

        // Switch to B: nothing was submitted there, so Up recalls nothing even
        // though A still holds two prompts in the backing store.
        ReplInputLoop.PopulatePromptHistory(composer, store, sessionB);
        await Assert.That(composer.History.Count).IsEqualTo(0);
        composer.Buffer.Clear();
        composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo(string.Empty);

        // Return to A: the prompts are still there, newest first again.
        ReplInputLoop.PopulatePromptHistory(composer, store, sessionA);
        await Assert.That(composer.History.Count).IsEqualTo(2);
        composer.Buffer.Clear();
        composer.HandleKey(KeyEvent.Simple(KeyCode.Up));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("second prompt");
    }

    [Test]
    public async Task RecordedPrompt_IsReadByIndexFromNewest()
    {
        var store = new SessionPromptHistory();
        var id = SessionId.Create("session-1");

        ReplInputLoop.RecordSubmittedPrompt(store, "session-1", "one");
        ReplInputLoop.RecordSubmittedPrompt(store, "session-1", "two");

        // This is the read shape the REPL uses while walking a session's rail.
        await Assert.That(store.At(id, 0)).IsEqualTo("two");
        await Assert.That(store.At(id, 1)).IsEqualTo("one");
        await Assert.That(store.At(id, 2)).IsNull();
    }
}
