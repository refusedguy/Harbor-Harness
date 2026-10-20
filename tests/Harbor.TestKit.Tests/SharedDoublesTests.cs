using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Git;
using Harbor.Abstractions.Providers;

namespace Harbor.TestKit.Tests;

/// <summary>
///     The remaining shared doubles: the event-bus middlewares, the git
///     query, the auth resolvers, the empty container and the HTTP stub.
/// </summary>
public class SharedDoublesTests
{
    [Test]
    public async Task A_Recording_Middleware_Counts_And_Records()
    {
        var middleware = new RecordingEventBusMiddleware();
        AgentEvent evt = new TurnStartEvent(1);

        bool verdict = await middleware.ProcessAsync(ref evt);

        await Assert.That(verdict).IsTrue();
        await Assert.That(middleware.Seen).IsEqualTo(1)
            .Because("the middleware counts what it was asked about, and it allocates nothing to do it — the bus drains optional sinks on a path that is measured to be allocation-free");
    }

    [Test]
    public async Task A_Recording_Middleware_Can_Record_The_Order_It_Ran_In()
    {
        var order = new List<string>();
        var first = new RecordingEventBusMiddleware("first", EventBusSinkKind.Mandatory, order);
        var second = new RecordingEventBusMiddleware("second", EventBusSinkKind.Mandatory, order);
        AgentEvent evt = new TurnStartEvent(1);

        await first.ProcessAsync(ref evt);
        await second.ProcessAsync(ref evt);

        await Assert.That(order).IsEquivalentTo(new[] { "first", "second" });
    }

    [Test]
    public async Task A_Dropping_Middleware_Drops()
    {
        var middleware = new DropEventBusMiddleware();
        AgentEvent evt = new TurnStartEvent(1);

        bool verdict = await middleware.ProcessAsync(ref evt);

        await Assert.That(verdict).IsFalse();
    }

    [Test]
    public async Task A_Pass_Through_Middleware_Passes()
    {
        var middleware = new PassThroughEventBusMiddleware();
        AgentEvent evt = new TurnStartEvent(1);

        bool verdict = await middleware.ProcessAsync(ref evt);

        await Assert.That(verdict).IsTrue();
    }

    [Test]
    public async Task A_Transforming_Middleware_Replaces_The_Event()
    {
        var replacement = new AgentErrorEvent("replaced");
        var middleware = new TransformEventBusMiddleware(replacement);
        AgentEvent evt = new TurnStartEvent(1);

        bool verdict = await middleware.ProcessAsync(ref evt);

        await Assert.That(verdict).IsTrue();
        await Assert.That(evt).IsTypeOf<AgentErrorEvent>();
    }

    [Test]
    public void A_Throwing_Middleware_Throws()
    {
        var middleware = new ThrowingEventBusMiddleware();
        AgentEvent evt = new TurnStartEvent(1);

        Assert.Throws<InvalidOperationException>(() =>
        {
            // The call throws BEFORE it can return a ValueTask, so the task is
            // never created — hence never awaited.
            ValueTask<bool> pending = middleware.ProcessAsync(ref evt);
            _ = pending;
        });
    }

    [Test]
    public async Task A_Middleware_Without_A_Verdict_Inherits_Mandatory()
    {
        IEventBusMiddleware middleware = new UndeclaredSinkEventBusMiddleware();

        await Assert.That(middleware.SinkKind).IsEqualTo(EventBusSinkKind.Mandatory)
            .Because("the interface default is the conservative direction: a sink that has not thought about it can never be bypassed by accident");
    }

    [Test]
    public async Task A_Gated_Middleware_Waits_For_Its_Gate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new GatedEventBusMiddleware(gate.Task);
        AgentEvent evt = new TurnStartEvent(1);

        ValueTask<bool> pending = middleware.ProcessAsync(ref evt);
        await Assert.That(pending.IsCompleted).IsFalse();

        gate.TrySetResult();
        bool verdict = await pending;

        await Assert.That(verdict).IsTrue();
        await Assert.That(middleware.Seen).IsEqualTo(1);
    }

    [Test]
    public async Task The_Git_Query_Answers_And_Counts()
    {
        var query = new TestGitQuery(
            GitWorkspaceStatus.None,
            [new GitWorktreeInfo("/repo/.worktrees/other", null, false)]);

        IReadOnlyList<GitWorktreeInfo> worktrees = query.ListWorktrees("/repo");
        query.ListWorktrees("/repo");

        await Assert.That(worktrees.Count).IsEqualTo(1);
        await Assert.That(query.ListWorktreesCalls).IsEqualTo(2);
        await Assert.That(query.AskedDirectories).IsEquivalentTo(new[] { "/repo", "/repo" });
        await Assert.That(query.AskedFor).IsEqualTo("/repo");
    }

    [Test]
    public async Task The_Git_Query_Reports_The_Status_It_Was_Given()
    {
        var status = new GitWorkspaceStatus("main", true, 2, "2 minutes ago");
        var query = new TestGitQuery(status);

        await Assert.That(query.GetStatus("/repo")).IsEqualTo(status);
    }

    [Test]
    public void A_Throwing_Git_Query_Throws()
    {
        var query = new ThrowingGitQuery();

        Assert.Throws<InvalidOperationException>(() => { _ = query.ListWorktrees("/repo"); });
    }

    [Test]
    public async Task The_Auth_Resolver_Hands_Out_Its_Key()
    {
        var resolver = new TestAuthResolver("k");

        Result<string> result = await resolver.ResolveApiKeyAsync("openai");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("k");
        await Assert.That(resolver.Requested).IsEquivalentTo(new[] { "openai" });
    }

    [Test]
    public async Task The_Failing_Auth_Resolver_Fails_And_Names_The_Provider()
    {
        var resolver = new FailingAuthResolver();

        Result<string> result = await resolver.ResolveApiKeyAsync("openai");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("openai")
            .Because("a failure message that does not say WHICH provider failed is a failure message that cannot be acted on");
    }

    [Test]
    public async Task The_Empty_Container_Resolves_Nothing()
    {
        var provider = new EmptyServiceProvider();

        await Assert.That(provider.GetService(typeof(string))).IsNull();
    }

    [Test]
    public async Task The_Http_Stub_Captures_And_Counts()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        using HttpResponseMessage response = await client.GetAsync("https://example.invalid/x");

        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.OK);
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(handler.CapturedRequests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task The_Http_Stub_Surfaces_A_Throwing_Responder_As_A_Fault()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("transport"));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GetAsync("https://example.invalid/x"));
    }
}
