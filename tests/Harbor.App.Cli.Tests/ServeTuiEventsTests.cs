using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;
using Harbor.App.Cli.Commands;
using Harbor.Ipc;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     Tests for the v0.9 UX surface over the existing IPC layer:
///     verb argument parsing, the JSON event formatter on a fake stream,
///     and the attach/watch runners against a stub client.
/// </summary>
public class ServeTuiEventsTests
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    // ── serve ────────────────────────────────────────────────────────────────

    [Test]
    public async Task ServeOptions_Parse_HelpFlag()
    {
        await Assert.That(ServeOptions.Parse(["--help"]).ShowHelp).IsTrue();
        await Assert.That(ServeOptions.Parse(["-h"]).ShowHelp).IsTrue();
    }

    [Test]
    public async Task ServeOptions_Parse_PassesThroughUnknownArgs()
    {
        await Assert.That(ServeOptions.Parse([]).ShowHelp).IsFalse();
        await Assert.That(ServeOptions.Parse(["--loglevel", "Debug"]).ShowHelp).IsFalse();
    }

    // ── tui attach options ───────────────────────────────────────────────────

    [Test]
    public async Task TuiAttachOptions_Parse_SessionAndSince()
    {
        bool ok = TuiAttachOptions.TryParse(
            ["--session", "abc123", "--since", "42"], out var options, out string? error);
        await Assert.That(ok).IsTrue();
        await Assert.That(error is null).IsTrue();
        await Assert.That(options!.SessionId == "abc123").IsTrue();
        await Assert.That(options.SinceSequence == 42UL).IsTrue();
        await Assert.That(options.ShowHelp).IsFalse();
    }

    [Test]
    public async Task TuiAttachOptions_Parse_InlineAndShortForms()
    {
        bool ok = TuiAttachOptions.TryParse(
            ["-s", "s1", "--since=7"], out var options, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(options!.SessionId == "s1").IsTrue();
        await Assert.That(options.SinceSequence == 7UL).IsTrue();
    }

    [Test]
    public async Task TuiAttachOptions_Parse_Help()
    {
        bool ok = TuiAttachOptions.TryParse(["--help"], out var options, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(options!.ShowHelp).IsTrue();
    }

    [Test]
    public async Task TuiAttachOptions_Parse_UnknownFlagFails()
    {
        bool ok = TuiAttachOptions.TryParse(["--bogus"], out _, out string? error);
        await Assert.That(ok).IsFalse();
        await Assert.That(error!.Contains("--bogus")).IsTrue();
    }

    [Test]
    public async Task TuiAttachOptions_Parse_BadSinceFails()
    {
        bool ok = TuiAttachOptions.TryParse(["--since", "nope"], out _, out string? error);
        await Assert.That(ok).IsFalse();
        await Assert.That(error!.Contains("--since")).IsTrue();
    }

    [Test]
    public async Task TuiAttachOptions_Parse_MissingSessionValueFails()
    {
        bool ok = TuiAttachOptions.TryParse(["--session"], out _, out string? error);
        await Assert.That(ok).IsFalse();
        await Assert.That(error!.Contains("--session")).IsTrue();
    }

    // ── events watch options ─────────────────────────────────────────────────

    [Test]
    public async Task EventsWatchOptions_Parse_WatchAndCount()
    {
        bool ok = EventsWatchOptions.TryParse(
            ["--watch", "--count", "10"], out var options, out string? error);
        await Assert.That(ok).IsTrue();
        await Assert.That(error is null).IsTrue();
        await Assert.That(options!.Watch).IsTrue();
        await Assert.That(options.Count == 10).IsTrue();
    }

    [Test]
    public async Task EventsWatchOptions_Parse_ShortAndInlineForms()
    {
        bool ok = EventsWatchOptions.TryParse(["-w", "-n", "3"], out var options, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(options!.Watch).IsTrue();
        await Assert.That(options.Count == 3).IsTrue();

        ok = EventsWatchOptions.TryParse(["--watch", "--count=5"], out options, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(options!.Count == 5).IsTrue();
    }

    [Test]
    public async Task EventsWatchOptions_Parse_BareArgsMeansUsage()
    {
        bool ok = EventsWatchOptions.TryParse([], out var options, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(options!.Watch).IsFalse();
        await Assert.That(options.ShowHelp).IsFalse();
    }

    [Test]
    public async Task EventsWatchOptions_Parse_BadCountFails()
    {
        bool ok = EventsWatchOptions.TryParse(["--watch", "--count", "0"], out _, out string? error);
        await Assert.That(ok).IsFalse();
        await Assert.That(error!.Contains("--count")).IsTrue();
    }

    [Test]
    public async Task EventsWatchOptions_Parse_UnknownFlagFails()
    {
        bool ok = EventsWatchOptions.TryParse(["--json"], out _, out string? error);
        await Assert.That(ok).IsFalse();
        await Assert.That(error!.Contains("--json")).IsTrue();
    }

    // ── formatter ────────────────────────────────────────────────────────────

    [Test]
    public async Task HarborEventJson_Format_AllKindsCarryKindAndFields()
    {
        var partial = AssistantMessage.Empty("s1", "m");
        (HarborEvent Event, string Kind, string Field)[] cases =
        [
            new (new HarborEvent.AgentStarted("s1"), "agent_started", "s1"),
            new (new HarborEvent.MessageUpdate(partial, "hel"), "message_update", "hel"),
            new (new HarborEvent.MessageEnd(partial), "message_end", "s1"),
            new (new HarborEvent.ToolStart("tc1", "read"), "tool_start", "read"),
            new (new HarborEvent.ToolEnd("tc1", ToolResult.Success("out")), "tool_end", "out"),
            new (new HarborEvent.TurnStart(2), "turn_start", "2"),
            new (new HarborEvent.TurnEnd(2), "turn_end", "2"),
            new (new HarborEvent.AgentEnded("s1"), "agent_ended", "s1"),
            new (new HarborEvent.AgentError("boom"), "agent_error", "boom"),
            new (new HarborEvent.CompactionStarted("s1"), "compaction_started", "s1"),
            new (new HarborEvent.CompactionCompleted("s1", 3, 100), "compaction_completed", "100"),
        ];
        foreach (var (evt, kind, field) in cases)
        {
            string line = HarborEventJson.Format(evt);
            await Assert.That(line.Contains($"\"kind\":\"{kind}\"")).IsTrue();
            await Assert.That(line.Contains(field)).IsTrue();
            await Assert.That(line.Contains('\n')).IsFalse();
        }
    }

    [Test]
    public async Task HarborEventJson_WriteEventLines_FakeStreamWritesJsonLines()
    {
        var events = StreamOf(
            new HarborEvent.TurnStart(1),
            new HarborEvent.AgentError("x"),
            new HarborEvent.TurnEnd(1));

        int written = await HarborEventJson.WriteEventLinesAsync(events, _out, maxEvents: null);

        await Assert.That(written).IsEqualTo(3);
        string[] lines = SplitLines(_out.ToString());
        await Assert.That(lines.Length).IsEqualTo(3);
        await Assert.That(lines[0]).Contains("turn_start");
        await Assert.That(lines[1]).Contains("agent_error");
    }

    [Test]
    public async Task HarborEventJson_WriteEventLines_RespectsMaxEvents()
    {
        var events = StreamOf(
            new HarborEvent.TurnStart(1),
            new HarborEvent.TurnStart(2),
            new HarborEvent.TurnStart(3));

        int written = await HarborEventJson.WriteEventLinesAsync(events, _out, maxEvents: 2);

        await Assert.That(written).IsEqualTo(2);
        await Assert.That(SplitLines(_out.ToString()).Length).IsEqualTo(2);
    }

    // ── runners ──────────────────────────────────────────────────────────────

    [Test]
    public async Task EventsWatchRunner_NotConnected_ReturnsOneWithHint()
    {
        var client = new FakeHarborClient { Connected = false };

        int exit = await EventsWatchRunner.RunAsync(
            _out, _err, client, new EventsWatchOptions(true, null, false));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_err.ToString()).Contains("harbor serve");
    }

    [Test]
    public async Task EventsWatchRunner_BareArgs_PrintsUsage()
    {
        var client = new FakeHarborClient();

        int exit = await EventsWatchRunner.RunAsync(
            _out, _err, client, new EventsWatchOptions(false, null, false));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_out.ToString()).Contains("harbor events --watch");
    }

    [Test]
    public async Task EventsWatchRunner_Watch_StreamsFormattedLinesToStdout()
    {
        var client = new FakeHarborClient
        {
            Events =
            [
                new HarborEvent.TurnStart(1),
                new HarborEvent.TurnEnd(1),
            ]
        };

        int exit = await EventsWatchRunner.RunAsync(
            _out, _err, client, new EventsWatchOptions(true, 2, false));

        await Assert.That(exit).IsEqualTo(0);
        string[] lines = SplitLines(_out.ToString());
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("turn_start");
        await Assert.That(_err.ToString()).Contains("stopped after 2");
    }

    [Test]
    public async Task TuiAttachRunner_AttachWithSession_BindsAndStreams()
    {
        var session = Session.Create("/tmp", "code", "ollama", "test-model");
        var client = new FakeHarborClient
        {
            Sessions = [session],
            Events = [new HarborEvent.AgentStarted(session.Id)],
        };

        int exit = await TuiAttachRunner.RunAsync(
            _out, _err, client, new TuiAttachOptions(session.Id, null, false));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(client.Binds.Count).IsEqualTo(1);
        await Assert.That(client.Binds[0].SessionId == session.Id).IsTrue();
        await Assert.That(client.Binds[0].Agent == "code").IsTrue();
        await Assert.That(SplitLines(_out.ToString()).Length).IsEqualTo(1);
        await Assert.That(_out.ToString()).Contains("agent_started");
    }

    [Test]
    public async Task TuiAttachRunner_MissingSession_ReturnsOne()
    {
        var client = new FakeHarborClient();

        int exit = await TuiAttachRunner.RunAsync(
            _out, _err, client, new TuiAttachOptions("nope", null, false));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_err.ToString()).Contains("not found");
    }

    [Test]
    public async Task TuiAttachRunner_SinceSequence_ForwardedToSubscribe()
    {
        var client = new FakeHarborClient();

        int exit = await TuiAttachRunner.RunAsync(
            _out, _err, client, new TuiAttachOptions(null, 42, false));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(client.Subscriptions).IsEqualTo(1);
        await Assert.That(client.LastSince == 42UL).IsTrue();
        await Assert.That(_err.ToString()).Contains("sequence 42");
    }

    [Test]
    public async Task TuiAttachRunner_NotConnected_ReturnsOneWithHint()
    {
        var client = new FakeHarborClient { Connected = false };

        int exit = await TuiAttachRunner.RunAsync(
            _out, _err, client, new TuiAttachOptions(null, null, false));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_err.ToString()).Contains("harbor serve");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<HarborEvent> StreamOf(params HarborEvent[] events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private sealed class FakeHarborClient : IHarborClient
    {
        public bool Connected = true;
        public List<Session> Sessions = [];
        public List<(string SessionId, string Agent)> Binds = [];
        public List<HarborEvent> Events = [];
        public ulong? LastSince;
        public int Subscriptions;

        public bool IsConnected => Connected;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<Result> StartAgentAsync(string sessionId, string agentName, CancellationToken ct = default)
        {
            Binds.Add((sessionId, agentName));
            return Task.FromResult(Result.Success());
        }

        public Task<Result> AbortAgentAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> SendPromptAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<Session>> CreateSessionAsync(string dir, string agent, string provider, string model, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(Session.Create(dir, agent, provider, model)));

        public Task<Result<IReadOnlyList<Session>>> ListSessionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Session>>(Sessions));

        public Task<Result<Session>> GetSessionAsync(string sessionId, CancellationToken ct = default)
        {
            Session? match = Sessions.Find(s => s.Id == sessionId);
            return Task.FromResult(
                match is not null ? Result.Success(match) : Result.Failure<Session>($"Session '{sessionId}' not found."));
        }

        public Task<Result> DeleteSessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>([]));

        public Task<Result<IReadOnlyList<ProviderId>>> ListProvidersAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<ProviderId>>([]));

        public Task<Result<IReadOnlyList<ModelInfo>>> ListModelsAsync(string? providerId = null, CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>([]));

        public Task<Result<IReadOnlyList<ToolDescriptor>>> ListToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<ToolDescriptor>>([]));

        public async IAsyncEnumerable<HarborEvent> SubscribeToEventsAsync(
            CancellationToken ct = default, ulong? sinceSequence = null)
        {
            LastSince = sinceSequence;
            Subscriptions++;
            foreach (var evt in Events)
            {
                ct.ThrowIfCancellationRequested();
                yield return evt;
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
