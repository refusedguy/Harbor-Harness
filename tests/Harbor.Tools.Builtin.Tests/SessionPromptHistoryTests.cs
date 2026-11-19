// #2680 F121. Behaviour tests for the session-scoped prompt history. They pin
// the observable policy: prompts stay in their own session, each list and the
// session set are bounded, and reads refresh recency before eviction. No REPL,
// store or clock is involved.

using Harbor.Abstractions.Models.Identifiers;
using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

public class SessionPromptHistoryTests
{
    private static SessionId Session(string name) => SessionId.Create(name);

    [Test]
    public async Task RecordsPromptsOldestFirstWithinItsSession()
    {
        var history = new SessionPromptHistory();
        SessionId session = Session("11111111111111111111111111111111");

        await Assert.That(history.Record(session, "first")).IsTrue();
        await Assert.That(history.Record(session, "second")).IsTrue();

        IReadOnlyList<string> prompts = history.For(session);
        await Assert.That(prompts.Count).IsEqualTo(2);
        await Assert.That(prompts[0]).IsEqualTo("first");
        await Assert.That(prompts[1]).IsEqualTo("second");
    }

    [Test]
    public async Task SessionsAreIsolated()
    {
        var history = new SessionPromptHistory();
        SessionId left = Session("11111111111111111111111111111111");
        SessionId right = Session("22222222222222222222222222222222");

        history.Record(left, "left only");
        history.Record(right, "right only");

        await Assert.That(history.For(left).Count).IsEqualTo(1);
        await Assert.That(history.At(left, 0)).IsEqualTo("left only");
        await Assert.That(history.At(right, 0)).IsEqualTo("right only");
        await Assert.That(history.SessionCount).IsEqualTo(2);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task IgnoresBlankPrompts(string? prompt)
    {
        var history = new SessionPromptHistory();
        SessionId session = Session("11111111111111111111111111111111");

        await Assert.That(history.Record(session, prompt)).IsFalse();
        await Assert.That(history.For(session)).IsEmpty();
        await Assert.That(history.SessionCount).IsEqualTo(0);
    }

    [Test]
    public async Task CapsEachSessionAtHundredPromptsDroppingTheOldest()
    {
        var history = new SessionPromptHistory();
        SessionId session = Session("11111111111111111111111111111111");

        for (int i = 0; i < SessionPromptHistory.MaxEntriesPerSession + 10; i++)
            history.Record(session, $"p{i}");

        IReadOnlyList<string> prompts = history.For(session);
        await Assert.That(prompts.Count).IsEqualTo(SessionPromptHistory.MaxEntriesPerSession);
        await Assert.That(prompts[0]).IsEqualTo("p10");
        await Assert.That(prompts[^1]).IsEqualTo("p109");
    }

    [Test]
    public async Task CapsRememberedSessionsAndEvictsLeastRecentlyUsed()
    {
        var history = new SessionPromptHistory();
        var ids = new SessionId[SessionPromptHistory.MaxSessions];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = Session(i.ToString("x32"));
            history.Record(ids[i], "prompt");
        }

        // Reading session 0 refreshes it; session 1 is now the oldest unused.
        await Assert.That(history.At(ids[0], 0)).IsEqualTo("prompt");

        SessionId newcomer = Session("ffffffffffffffffffffffffffffffff");
        history.Record(newcomer, "new");

        await Assert.That(history.SessionCount).IsEqualTo(SessionPromptHistory.MaxSessions);
        await Assert.That(history.For(ids[0]).Count).IsEqualTo(1);
        await Assert.That(history.For(ids[1])).IsEmpty();
        await Assert.That(history.At(newcomer, 0)).IsEqualTo("new");
    }

    [Test]
    public async Task AtIndexesFromNewestAndRejectsOutOfRange()
    {
        var history = new SessionPromptHistory();
        SessionId session = Session("11111111111111111111111111111111");
        history.Record(session, "old");
        history.Record(session, "new");

        await Assert.That(history.At(session, 0)).IsEqualTo("new");
        await Assert.That(history.At(session, 1)).IsEqualTo("old");
        await Assert.That(history.At(session, -1)).IsNull();
        await Assert.That(history.At(session, 2)).IsNull();
        await Assert.That(history.At(Session("22222222222222222222222222222222"), 0)).IsNull();
    }

    [Test]
    public async Task SnapshotIsDetachedFromTheStore()
    {
        var history = new SessionPromptHistory();
        SessionId session = Session("11111111111111111111111111111111");
        history.Record(session, "first");

        IReadOnlyList<string> snapshot = history.For(session);
        history.Record(session, "second");

        await Assert.That(snapshot.Count).IsEqualTo(1);
        await Assert.That(history.Count(session)).IsEqualTo(2);
    }

    [Test]
    public async Task RemoveAndClearForgetHistory()
    {
        var history = new SessionPromptHistory();
        SessionId first = Session("11111111111111111111111111111111");
        SessionId second = Session("22222222222222222222222222222222");
        history.Record(first, "one");
        history.Record(second, "two");

        await Assert.That(history.Remove(first)).IsTrue();
        await Assert.That(history.Remove(first)).IsFalse();
        await Assert.That(history.For(first)).IsEmpty();
        await Assert.That(history.SessionCount).IsEqualTo(1);

        history.Clear();
        await Assert.That(history.SessionCount).IsEqualTo(0);
        await Assert.That(history.For(second)).IsEmpty();
    }
}
