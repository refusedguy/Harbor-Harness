using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.App.Avalonia.Services;
using Harbor.Application.Sessions;
using Harbor.Storage.Memory;
using Harbor.TestKit;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     #670: the UI framework forked a session with its own hand-written copy of
///     <c>SessionForkService</c> (<c>SessionFactory.CreateBranchAsync</c>), and the copy had
///     drifted away from the core on every fact the user can observe.
/// </summary>
/// <remarks>
///     <para>
///         These tests deliberately assert against the <b>persisted</b> child — re-read from
///         the store — rather than against whatever record the call returned. That is the whole
///         shape of the bug: the copy built a correct-looking <see cref="Session" /> in memory
///         with <c>with { Title = … }</c> and never wrote it, so the session list, the session
///         tree and the next process all saw something the caller was never shown.
///     </para>
///     <para>
///         <see cref="ForkThroughUi_AndThroughTheCore_AgreeOnEveryObservableFact" /> is the
///         statement of the issue itself: the two paths must not be able to disagree. The
///         per-fact tests around it name each disagreement so a regression reports which one
///         came back.
///     </para>
/// </remarks>
public class SessionForkParityTests
{
    // ── Per-fact guards ───────────────────────────────────────────────────────

    /// <summary>
    ///     The child must carry a durable <see cref="Session.ParentSessionId" />. Without it
    ///     the fork is a sibling root: nothing anywhere can tell it apart from an unrelated
    ///     session that happens to share a transcript.
    /// </summary>
    [Test]
    public async Task Fork_FromUi_PersistsParentSessionId()
    {
        var (factory, store) = NewFactory();
        Session source = await SeedAsync(store, "the original work").ConfigureAwait(false);

        var forked = await factory.CreateBranchAsync(source).ConfigureAwait(false);

        await Assert.That(forked.IsSuccess).IsTrue();
        Session persisted = (await store.GetAsync(forked.Value.Id).ConfigureAwait(false)).Value;
        await Assert.That(persisted.ParentSessionId).IsEqualTo(source.Id)
            .Because(
                "ParentSessionId is the ONLY carrier of fork lineage. SessionTreePanelModel, "
                + "SessionTreeRunner, SessionKind and SubagentsModel all read it; a child "
                + "without it is rendered as an unrelated root session.");
    }

    /// <summary>
    ///     The title the caller is shown must be the title that was stored. The copy applied
    ///     <c>with { Title = … }</c> and never called <c>UpdateAsync</c>, so the store kept the
    ///     <c>Session {timestamp}</c> default and the two disagreed for as long as the row lived.
    /// </summary>
    [Test]
    public async Task Fork_FromUi_PersistsTheTitleItReports()
    {
        var (factory, store) = NewFactory();
        Session source = await SeedAsync(store, "the original work").ConfigureAwait(false);

        var forked = await factory.CreateBranchAsync(source).ConfigureAwait(false);

        await Assert.That(forked.IsSuccess).IsTrue();
        Session persisted = (await store.GetAsync(forked.Value.Id).ConfigureAwait(false)).Value;
        await Assert.That(persisted.Title).IsEqualTo($"Fork of {source.Title}")
            .Because(
                "CreateBranchAsync hands the child to SessionLifecycleService.BranchActiveAsync, "
                + "which immediately re-reads the session from the store. A title that was never "
                + "persisted is gone before the user sees the branch — and the success toast has "
                + "already promised it.");
    }

    /// <summary>
    ///     Copied messages keep their original ids. The copy replaced them with
    ///     <c>Guid.NewGuid()</c>, so no id named in the parent could be named in the child —
///     including the cut point <c>SessionForkService.ForkAsync</c> takes.
    /// </summary>
    [Test]
    public async Task Fork_FromUi_KeepsMessageIds()
    {
        var (factory, store) = NewFactory();
        Session source = await SeedAsync(store, "the original work").ConfigureAwait(false);
        var sourceIds = (await store.GetMessagesAsync(source.Id).ConfigureAwait(false)).Value
            .Select(m => m.Id).ToArray();

        var forked = await factory.CreateBranchAsync(source).ConfigureAwait(false);

        await Assert.That(forked.IsSuccess).IsTrue();
        var copied = (await store.GetMessagesAsync(forked.Value.Id).ConfigureAwait(false)).Value;
        await Assert.That(copied.Select(m => m.Id)).IsEquivalentTo(sourceIds)
            .Because(
                "SessionForkService re-parents with `with { SessionId = child.Id }` and nothing "
                + "else, precisely so a message id means the same thing on both sides of a fork. "
                + "Regenerating them makes the child's transcript unaddressable.");
    }

    /// <summary>
    ///     The user-visible symptom, stated end to end: the session tree is a live panel, and a
    ///     UI-forked child has to show up indented under its parent there.
    /// </summary>
    [Test]
    public async Task Fork_FromUi_AppearsAsAChildInTheSessionTree()
    {
        var (factory, store) = NewFactory();
        Session source = await SeedAsync(store, "the original work").ConfigureAwait(false);

        var forked = await factory.CreateBranchAsync(source).ConfigureAwait(false);
        await Assert.That(forked.IsSuccess).IsTrue();

        IReadOnlyList<Session> all = (await store.ListAsync().ConfigureAwait(false)).Value;
        var entries = SessionTreeModel.BuildEntries(all.Select(Seed).ToArray());
        SessionTreeEntry child = entries.Single(e => e.SessionId == forked.Value.Id);

        await Assert.That(child.Depth).IsEqualTo(1)
            .Because(
                "BuildEntries roots a session at depth 0 and indents a child by exactly one level "
                + "per lineage link, so this is the '/tree' row the user sees. Depth 0 means the "
                + "fork renders flat next to the session it came from — indistinguishable from a "
                + "session they started themselves.");
        await Assert.That(child.Title).Contains(source.Title)
            .Because(
                "The row's own text has to say where it came from too. The old copy asked the "
                + "user to read an unpersisted \" (branch)\" suffix in a success toast while the "
                + "panel showed \"Session 2026-09-30 18:45\".");
    }

    // ── The statement of the issue ────────────────────────────────────────────

    /// <summary>
    ///     The two fork paths must not be able to disagree. Anything the core does — lineage,
    ///     title, message identity — the UI path does identically, because it is the same code.
    /// </summary>
    [Test]
    public async Task Fork_ThroughUi_AndThroughTheCore_AgreeOnEveryObservableFact()
    {
        // Same starting state, forked twice: once down the UI path, once down the core path
        // the CLI uses.
        var (uiFactory, uiStore) = NewFactory();
        Session uiSource = await SeedAsync(uiStore, "the original work").ConfigureAwait(false);
        Session uiChild = (await uiFactory.CreateBranchAsync(uiSource).ConfigureAwait(false)).Value;
        Session uiPersisted = (await uiStore.GetAsync(uiChild.Id).ConfigureAwait(false)).Value;
        var uiIds = (await uiStore.GetMessagesAsync(uiChild.Id).ConfigureAwait(false)).Value
            .Select(m => m.Id).ToArray();

        var coreStore = new MemorySessionStore();
        Session coreSource = await SeedAsync(coreStore, "the original work").ConfigureAwait(false);
        var coreFork = await new SessionForkService()
            .ForkAsync(coreStore, coreSource.Id).ConfigureAwait(false);
        await Assert.That(coreFork.IsSuccess).IsTrue();
        Session corePersisted = coreFork.Value.Session;
        var coreIds = (await coreStore.GetMessagesAsync(corePersisted.Id).ConfigureAwait(false)).Value
            .Select(m => m.Id).ToArray();

        await Assert.That(uiPersisted.Title).IsEqualTo(corePersisted.Title);
        await Assert.That(uiPersisted.ParentSessionId).IsEqualTo(corePersisted.ParentSessionId);
        await Assert.That(uiPersisted.Agent).IsEqualTo(corePersisted.Agent);
        await Assert.That(uiPersisted.Model).IsEqualTo(corePersisted.Model);
        await Assert.That(uiPersisted.ProviderId).IsEqualTo(corePersisted.ProviderId);
        await Assert.That(uiIds).IsEquivalentTo(coreIds);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (SessionFactory Factory, MemorySessionStore Store) NewFactory()
    {
        var store = new MemorySessionStore();
        var factory = new SessionFactory(
            new FakeAgentRegistry(AgentDefinition.CodeDefault("test-model", "test-provider")),
            new FakeAgent(),
            store,
            new SessionForkerAdapter(store),
            NullLogger<SessionFactory>.Instance);
        return (factory, store);
    }

    /// <summary>A source session with a two-message transcript, as a user would have after a few turns.</summary>
    private static async Task<Session> SeedAsync(ISessionStore store, string content)
    {
        Session session = (await store.CreateAsync("/tmp/proj", "code", "test-provider", "test-model").ConfigureAwait(false)).Value;
        await store.AppendMessageAsync(session.Id, NewUser(session.Id, "first")).ConfigureAwait(false);
        await store.AppendMessageAsync(session.Id, NewUser(session.Id, content)).ConfigureAwait(false);
        return session;
    }

    private static UserMessage NewUser(string sessionId, string content) => new(
        Guid.NewGuid().ToString("N"), sessionId, DateTimeOffset.UtcNow, content, "code", "test-model");

    private static SessionTreeSeed Seed(Session s) => new(
        s.Id, s.Title, s.Directory, s.Agent, s.Model, s.CreatedAt, s.UpdatedAt, s.ParentSessionId, false);
}