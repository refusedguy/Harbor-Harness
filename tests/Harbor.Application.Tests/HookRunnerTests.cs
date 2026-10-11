using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Agents;
using Harbor.Application.Hooks;
using Harbor.Application.Permissions;
using Harbor.Application.Resilience;
using Harbor.Application.Sessions;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TestSessionContext = Harbor.Application.Tests.Fakes.TestSessionContext;

namespace Harbor.Application.Tests;

/// <summary>
///     PX4 slice 1: user hooks (plain shell commands from hooks.json — not
///     plugins). Runner verdicts, fail-closed gates, and the three event
///     points (PreToolUse, PostToolUse, SessionEnd).
/// </summary>
public class HookRunnerTests
{
    private sealed class AllowPermissions : IPermissionService
    {
        public Task<Result<PermissionResponse>> CheckAsync(
            string agentName, string toolName, JsonElement args, CancellationToken ct = default,
            string? invocationId = null, int generation = 1) =>
            Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Allow, false)));

        public Task<Result<PermissionResponse>> AskUserAsync(
            PermissionRequest request, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(new PermissionResponse(PermissionAction.Deny, false)));

        public PermissionRuleset GetRuleset(string agentName) => PermissionRuleset.Empty;

        public Task<Result> SaveAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "harbor-hooks-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static HookRunner NewRunner(string dir, string hooksJson) =>
        NewRunnerAt(Path.Combine(dir, "hooks.json"), hooksJson);

    private static HookRunner NewRunnerAt(string path, string hooksJson)
    {
        File.WriteAllText(path, hooksJson);
        return new HookRunner(path, NullLogger<HookRunner>.Instance, TimeSpan.FromSeconds(10));
    }

    private static AgentDefinition CodeAgent() => new(
        AgentName.Create("code"),
        "Code",
        "hooks harness",
        "test-model",
        "test",
        new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) }));

    private static TestSessionContext NewSession() => new(
        Session.Create("/tmp/harbor-hooks-tests", "code", "test", "test-model"));

    private static ToolCallPart CounterCall() => new(
        "tc1", "counter", JsonDocument.Parse("""{"n":1}""").RootElement);

    // ── Matcher ──────────────────────────────────────────────────────────

    [Test]
    public async Task Matcher_NullOrWildcard_MatchesAll()
    {
        await Assert.That(HookConfig.Matches(null, "bash")).IsTrue();
        await Assert.That(HookConfig.Matches("", "bash")).IsTrue();
        await Assert.That(HookConfig.Matches("*", "bash")).IsTrue();
    }

    [Test]
    public async Task Matcher_PipeList_MatchesCaseInsensitively()
    {
        await Assert.That(HookConfig.Matches("bash|write", "Bash")).IsTrue();
        await Assert.That(HookConfig.Matches("bash|write", "WRITE")).IsTrue();
        await Assert.That(HookConfig.Matches("bash|write", "read")).IsFalse();
    }

    // ── PreToolUse verdicts ──────────────────────────────────────────────

    [Test]
    public async Task PreToolUse_NoConfigFile_Allows()
    {
        var runner = new HookRunner(
            Path.Combine(NewTempDir(), "hooks.json"), NullLogger<HookRunner>.Instance);
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
        await Assert.That(verdict.EditedArgs).IsNull();
    }

    [Test]
    public async Task PreToolUse_DenyHook_ReturnsDenyWithReason()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"matcher":"counter","command":"echo '{\"decision\":\"deny\",\"reason\":\"nope\"}'"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
        await Assert.That(verdict.Reason!).Contains("nope");
    }

    [Test]
    public async Task PreToolUse_SilentSuccess_Allows()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"command":"true"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
    }

    [Test]
    public async Task PreToolUse_SilentNonZeroExit_DeniesFailClosed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"command":"exit 3"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
    }

    [Test]
    public async Task PreToolUse_InvalidVerdictJson_DeniesFailClosed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"command":"echo not-json"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
    }

    [Test]
    public async Task PreToolUse_UnknownDecision_DeniesFailClosed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"command":"echo '{\"decision\":\"maybe\"}'"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
    }

    [Test]
    public async Task PreToolUse_Timeout_DeniesFailClosed()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"command":"sleep 30","timeout":1}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Deny);
        await Assert.That(verdict.Reason!).Contains("timed out");
    }

    [Test]
    public async Task PreToolUse_EditHook_ReturnsEditedArgs()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"matcher":"counter","command":"echo '{\"decision\":\"edit\",\"editedArgs\":{\"n\":2}}'"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
        await Assert.That(verdict.EditedArgs).IsNotNull();
        await Assert.That(verdict.EditedArgs!.Value.GetRawText()).Contains("2");
    }

    [Test]
    public async Task PreToolUse_NonMatchingMatcher_SkipsHook()
    {
        // The command would deny if it ran — allow proves it never spawned.
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"matcher":"other","command":"exit 3"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
    }

    [Test]
    public async Task PreToolUse_CorruptConfig_AllowsButRecordsLoadError()
    {
        string dir = NewTempDir();
        var runner = NewRunner(dir, """{not json""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        HookVerdict verdict = await runner.RunPreToolUseAsync("counter", doc.RootElement, "s1");

        await Assert.That(verdict.Decision).IsEqualTo(HookDecision.Allow);
        var config = new HookConfig(Path.Combine(dir, "hooks.json"));
        _ = config.GetHooks(HookEvents.PreToolUse, "counter");
        await Assert.That(config.LoadError).IsNotNull();
    }

    // ── Advisory points ──────────────────────────────────────────────────

    [Test]
    public async Task PostToolUse_RunsCommand()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "post-marker");
        var runner = NewRunner(dir,
            "{\"hooks\":{\"PostToolUse\":[{\"command\":\"touch " + marker + "\"}]}}");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        await runner.RunPostToolUseAsync("counter", doc.RootElement, ToolResult.Success("ok"), "s1");

        await Assert.That(File.Exists(marker)).IsTrue();
    }

    [Test]
    public async Task PostToolUse_FailingHook_DoesNotThrow()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var runner = NewRunner(NewTempDir(),
            """{"hooks":{"PostToolUse":[{"command":"exit 3"}]}}""");
        using var doc = JsonDocument.Parse("""{"n":1}""");

        await runner.RunPostToolUseAsync("counter", doc.RootElement, ToolResult.Success("ok"), "s1");
    }

    [Test]
    public async Task SessionEnd_RunsCommand()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "end-marker");
        var runner = NewRunner(dir,
            "{\"hooks\":{\"SessionEnd\":[{\"command\":\"touch " + marker + "\"}]}}");

        await runner.RunSessionEndAsync("s1");

        await Assert.That(File.Exists(marker)).IsTrue();
    }

    // ── Dispatcher integration ───────────────────────────────────────────

    private static ToolDispatcher NewDispatcher(IHookRunner? hooks, CountingTool tool) =>
        new(new FakeToolRegistry(tool), new AllowPermissions(), new FakeEventBus(),
            NullLogger<ToolDispatcher>.Instance, null, null, null, null, hooks);

    [Test]
    public async Task Dispatcher_PreToolUseDeny_ToolNeverExecutes()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var hooks = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"matcher":"counter","command":"echo '{\"decision\":\"deny\",\"reason\":\"hooked\"}'"}]}}""");
        var tool = new CountingTool();
        var dispatcher = NewDispatcher(hooks, tool);

        ToolResultMessage message = await dispatcher
            .ExecuteAsync([CounterCall()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(0);
        await Assert.That(message.Results.Count).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsTrue();
        await Assert.That(message.Results[0].Output).Contains("hooked");
    }

    [Test]
    public async Task Dispatcher_PreToolUseEdit_ToolSeesEditedArgs()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var hooks = NewRunner(NewTempDir(),
            """{"hooks":{"PreToolUse":[{"matcher":"counter","command":"echo '{\"decision\":\"edit\",\"editedArgs\":{\"n\":2}}'"}]}}""");
        var tool = new CountingTool();
        var dispatcher = NewDispatcher(hooks, tool);

        ToolResultMessage message = await dispatcher
            .ExecuteAsync([CounterCall()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsFalse();
        await Assert.That(tool.ExecutedArgs.Count).IsEqualTo(1);
        await Assert.That(tool.ExecutedArgs[0]).Contains("2");
    }

    [Test]
    public async Task Dispatcher_PostToolUse_RunsAfterExecution()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "post-marker");
        var hooks = NewRunner(dir,
            "{\"hooks\":{\"PostToolUse\":[{\"command\":\"touch " + marker + "\"}]}}");
        var tool = new CountingTool();
        var dispatcher = NewDispatcher(hooks, tool);

        ToolResultMessage message = await dispatcher
            .ExecuteAsync([CounterCall()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsFalse();
        await Assert.That(File.Exists(marker)).IsTrue();
    }

    [Test]
    public async Task Dispatcher_NoHooks_LegacyPathStillExecutes()
    {
        var tool = new CountingTool();
        var dispatcher = NewDispatcher(null, tool);

        ToolResultMessage message = await dispatcher
            .ExecuteAsync([CounterCall()], NewSession(), AssistantMessage.Empty("s", "m"), CodeAgent(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);

        await Assert.That(tool.Executions).IsEqualTo(1);
        await Assert.That(message.Results[0].IsError).IsFalse();
    }

    // ── Loop integration (SessionEnd) ────────────────────────────────────

    [Test]
    public async Task Loop_SessionEndHook_RunsAtEnd()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string dir = NewTempDir();
        string marker = Path.Combine(dir, "end-marker");
        var hooks = NewRunner(dir,
            "{\"hooks\":{\"SessionEnd\":[{\"command\":\"touch " + marker + "\"}]}}");
        AgentDefinition agent = CodeAgent();
        var agents = new FakeAgentRegistry(agent);
        var client = new ScriptedLlmClient(
        [
            new LlmEvent[]
            {
                new TextDeltaEvent("t", "done"),
                new StepFinishEvent(0, "stop", new Usage(1, 1))
            }
        ]);
        var loop = new AgentLoop(
            new FakeProviderRegistry(client),
            new FakeToolRegistry(),
            agents,
            new StubSystemPromptBuilder(),
            new FakeCompactionService(),
            new FakeTokenTracker(),
            new RetryPolicy(),
            new FakeEventBus(),
            new PermissionService(new FakeAgentRegistry(agent), NullLogger<PermissionService>.Instance),
            new MessageConverter(),
            NullLogger<AgentLoop>.Instance,
            hookRunner: hooks);

        Result result = await loop.RunAsync(NewSession(), agent);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(File.Exists(marker)).IsTrue();
    }
}
