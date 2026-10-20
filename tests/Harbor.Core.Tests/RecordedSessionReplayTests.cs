using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Application.Agents;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Registries.Agents;
using Harbor.Registries.Events;
using Harbor.Registries.Providers;
using Harbor.Registries.Tools;
using Harbor.Storage.Jsonl;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Core.Tests;

/// <summary>
///     The replay side of the keyless recorded-session tier (#1740): a session
///     log written in the keyless tier is both the INPUT of a replay and the
///     expected OUTPUT of it, and the replay needs no provider key.
/// </summary>
/// <remarks>
///     <para>
///         The test records a run, scrubs it on the way to disk, reads the log
///         back and replays it through the agent loop. The provider registry
///         holds ONE client — the replay client — so a replay that reached for
///         a real provider would fail rather than quietly succeed.
///     </para>
/// </remarks>
public class RecordedSessionReplayTests
{
    /// <summary>
    ///     The credential the recorded run read out of a file. The log must
    ///     not hold it; the replay must not need it.
    /// </summary>
    private const string RecordedKey = "sk-realkey-0123456789abcdef";

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ModelInfo TestModel = new(
        "test-model",
        "test",
        "Test Model",
        200_000,
        4096,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    private static (AgentLoop loop, InMemoryEventBus bus) CreateLoop(ILlmClient client, ITool tool)
    {
        var providers = new ProviderRegistry();
        providers.Register(ProviderId.Create("test"), () => client);

        var tools = new ToolRegistry();
        tools.Register(tool);
        tools.Freeze();

        var agents = new AgentRegistry();
        agents.Register(AgentDefinition.CodeDefault("test-model", "test"));

        var bus = new InMemoryEventBus();
        var permissions = new PermissionService(agents, NullLogger<PermissionService>.Instance);
        var loop = new AgentLoop(
            providers,
            tools,
            agents,
            new SystemPromptBuilder(),
            new CompactionService(
                new TokenTracker(),
                providers,
                NullLogger<CompactionService>.Instance),
            new TokenTracker(),
            new RetryPolicy(),
            bus,
            permissions,
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance);

        return (loop, bus);
    }

    /// <summary>
    ///     A tool that returns what the recording says it returned. The
    ///     recorded tool result is the oracle: the replay has to reproduce it,
    ///     and there is no live file behind it any more.
    /// </summary>
    private sealed class RecordedOutputTool(string output) : ITool
    {
        public int Executions { get; private set; }

        public ToolName Name => ToolName.Create("read");

        public string DisplayName => "Read";

        public string Description => "Returns the recorded output.";

        public JsonDocument ParameterSchema { get; } = JsonDocument.Parse(
            """{"type":"object","properties":{"path":{"type":"string"}}}""");

        public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

        public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

        public string? PromptSnippet => null;

        public IReadOnlyList<string> PromptGuidelines => [];

        public Result ValidateArguments(JsonElement args) => Result.Success();

        public Task<ToolResult> ExecuteAsync(
            JsonElement args,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            Executions++;
            return Task.FromResult(ToolResult.Success(output));
        }
    }

    [Test]
    public async Task AKeylessLog_ReplaysTheRunItRecords_WithoutAProviderKey()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"harbor-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var store = new JsonlSessionStore(dir, NullLogger<JsonlSessionStore>.Instance, SessionLogTier.Keyless);

        try
        {
            var session = (await store.CreateAsync(dir, "code", "test", "test-model")).Value;

            // --- RECORD ----------------------------------------------------
            await Assert.That((await store.AppendMessageAsync(session.Id, new UserMessage(
                "m-user",
                session.Id,
                Epoch.AddSeconds(1),
                "read the env file",
                "code",
                "test-model"))).IsSuccess).IsTrue();

            using var args = JsonDocument.Parse("""{"path":".env"}""");
            await Assert.That((await store.AppendMessageAsync(session.Id, new AssistantMessage(
                "m-assistant-1",
                session.Id,
                Epoch.AddSeconds(2),
                [new TextPart("looking"), ToolCallPart.Create("call-1", "read", args.RootElement)],
                StopReason.ToolUse,
                new Usage(10, 5),
                "test-model"))).IsSuccess).IsTrue();

            await Assert.That((await store.AppendMessageAsync(session.Id, new ToolResultMessage(
                "m-tool",
                session.Id,
                Epoch.AddSeconds(3),
                [new ToolResultEntry("call-1", "read", $"OPENAI_API_KEY={RecordedKey}", false)]))).IsSuccess).IsTrue();

            await Assert.That((await store.AppendMessageAsync(session.Id, new AssistantMessage(
                "m-assistant-2",
                session.Id,
                Epoch.AddSeconds(4),
                [new TextPart($"the key is {RecordedKey}")],
                StopReason.Stop,
                new Usage(3, 2),
                "test-model"))).IsSuccess).IsTrue();

            // --- READ BACK --------------------------------------------------
            var recorded = await store.GetMessagesAsync(session.Id);
            await Assert.That(recorded.IsSuccess).IsTrue();
            await Assert.That(recorded.Value.Count).IsEqualTo(4);

            // The log is the artifact that leaves the machine, and it holds no
            // credential: that is the whole point of the tier.
            string raw = await File.ReadAllTextAsync(Path.Combine(dir, $"{session.Id}.jsonl"));
            await Assert.That(raw).DoesNotContain(RecordedKey);
            await Assert.That(raw).Contains("\"sharing\":\"keyless\"");

            // --- REPLAY -----------------------------------------------------
            var tool = new RecordedOutputTool(((ToolResultMessage)recorded.Value[2]).Results[0].Output);
            var client = new RecordedSessionLlmClient(recorded.Value, TestModel);
            var (loop, _) = CreateLoop(client, tool);

            // The replay starts from the recorded INPUT — the user's turn — and
            // the rest has to come out of the log.
            var replay = new TestSessionContext(
                Session.Create(dir, "code", "test", "test-model"),
                [recorded.Value[0]]);

            var result = await loop.RunAsync(replay, AgentDefinition.CodeDefault("test-model", "test"));
            await Assert.That(result.IsSuccess).IsTrue();

            // The replay served exactly the turns the recording holds.
            await Assert.That(client.TurnsServed).IsEqualTo(2);
            await Assert.That(client.TurnCount).IsEqualTo(2);
            await Assert.That(tool.Executions).IsEqualTo(1);

            var assistants = replay.Messages.OfType<AssistantMessage>().ToList();
            await Assert.That(assistants.Count).IsEqualTo(2);

            // Turn 1: the same text, the same tool, the same arguments.
            await Assert.That(assistants[0].Parts.OfType<TextPart>().Single().Text).IsEqualTo("looking");
            ToolCallPart replayedCall = assistants[0].Parts.OfType<ToolCallPart>().Single();
            await Assert.That(replayedCall.ToolName).IsEqualTo("read");
            await Assert.That(replayedCall.Id).IsEqualTo("call-1");
            await Assert.That(replayedCall.Args.GetProperty("path").GetString()).IsEqualTo(".env");

            // Turn 2: the same text — and it is the SCRUBBED text, because the
            // log is the only thing a replay reads.
            await Assert.That(assistants[1].Parts.OfType<TextPart>().Single().Text)
                .IsEqualTo("the key is <redacted:api-key>");

            // And the tool result the replay produced is the recorded one.
            var replayedToolResult = replay.Messages.OfType<ToolResultMessage>().Single();
            await Assert.That(replayedToolResult.Results[0].Output)
                .IsEqualTo(((ToolResultMessage)recorded.Value[2]).Results[0].Output);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task AReplayWithNoRecording_FailsLoudly()
    {
        var client = new RecordedSessionLlmClient([], TestModel);

        await Assert.That(async () =>
        {
            await foreach (LlmEvent _ in client.StreamAsync(
                               new LlmRequest("test-model", [], string.Empty, [])))
            {
                // The stream must not produce anything: there is no recording.
            }
        }).Throws<InvalidOperationException>();
    }
}
