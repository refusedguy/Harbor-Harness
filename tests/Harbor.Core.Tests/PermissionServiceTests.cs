using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Application.Configuration;
using Harbor.Application.Permissions;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Core.Tests;
/// <summary>
///     Tests for <see cref="PermissionService" /> — verifies the allow / ask / deny branches
///     of <see cref="PermissionService.CheckAsync" /> against rulesets configured on the agent.
/// </summary>
public class PermissionServiceTests
{
    private static AgentDefinition AgentWithRuleset(params PermissionRule[] rules) => new(
        AgentName.Create("code"),
        "Code",
        "Default coding agent.",
        "test-model",
        "test",
        new PermissionRuleset(rules));

    private static JsonElement Args(params (string key, string value)[] pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach ((string k, string v) in pairs)
            dict[k] = v;
        return JsonDocument.Parse(JsonSerializer.Serialize(dict)).RootElement.Clone();
    }

    private static (PermissionService svc, AgentRegistry registry) CreateService(
        AgentDefinition agent,
        Func<PermissionRequest, CancellationToken, Task<PermissionResponse>>? asker = null)
    {
        var registry = new AgentRegistry();
        registry.Register(agent);
        var svc = new PermissionService(registry, NullLogger<PermissionService>.Instance, asker);
        return (svc, registry);
    }

    [Test]
    public async Task CheckAsync_AllowRule_ReturnsAllow()
    {
        var agent = AgentWithRuleset(
            new PermissionRule("read", "*", PermissionAction.Allow));

        var (svc, _) = CreateService(agent);
        var args = Args(("path", "some/file.txt"));

        var result = await svc.CheckAsync("code", "read", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CheckAsync_AbsolutePath_EscapesToDeny_WithoutAsker()
    {
        // A1 security hardening: rooted paths carry no workspace-relative meaning,
        // so glob Allow rules are skipped; with no user asker configured the
        // resulting Ask escalates to Deny.
        var agent = AgentWithRuleset(
            new PermissionRule("read", "*", PermissionAction.Allow));

        var (svc, _) = CreateService(agent);
        var args = Args(("path", "/some/file.txt"));

        var result = await svc.CheckAsync("code", "read", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task CheckAsync_DenyRule_ReturnsDeny()
    {
        var agent = AgentWithRuleset(
            new PermissionRule("edit", "*.env", PermissionAction.Deny),
            new PermissionRule("edit", "*", PermissionAction.Allow));

        var (svc, _) = CreateService(agent);
        var args = Args(("path", "/repo/secrets.env"));

        var result = await svc.CheckAsync("code", "edit", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task CheckAsync_AskRule_DelegatesToUserAsker()
    {
        var agent = AgentWithRuleset(
            new PermissionRule("write", "*", PermissionAction.Ask));

        PermissionRequest? captured = null;

        Task<PermissionResponse> Asker(PermissionRequest req, CancellationToken ct)
        {
            captured = req;
            return Task.FromResult(new PermissionResponse(PermissionAction.Allow, true));
        }

        var (svc, _) = CreateService(agent, Asker);
        var args = Args(("path", "/repo/file.txt"));

        var result = await svc.CheckAsync("code", "write", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
        await Assert.That(result.Value.PersistDecision).IsTrue();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Permission).IsEqualTo("write");
    }

    [Test]
    public async Task CheckAsync_AskRule_NoAskerConfigured_DefaultsToDeny()
    {
        var agent = AgentWithRuleset(
            new PermissionRule("write", "*", PermissionAction.Ask));

        var (svc, _) = CreateService(agent);
        var args = Args(("path", "/repo/file.txt"));

        var result = await svc.CheckAsync("code", "write", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task CheckAsync_AskRule_UserAskerThrows_FallsBackToDeny()
    {
        var agent = AgentWithRuleset(
            new PermissionRule("bash", "*", PermissionAction.Ask));

        Task<PermissionResponse> ThrowingAsker(PermissionRequest req, CancellationToken ct)
            => throw new InvalidOperationException("UI not available");

        var (svc, _) = CreateService(agent, ThrowingAsker);
        var args = Args(("command", "rm -rf /tmp/harbor-test"));

        var result = await svc.CheckAsync("code", "bash", args);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task CheckAsync_UnknownAgent_ReturnsFailure()
    {
        var (svc, _) = CreateService(AgentWithRuleset(new PermissionRule("read", "*", PermissionAction.Allow)));
        var args = Args(("path", "/some/file.txt"));

        var result = await svc.CheckAsync("nonexistent", "read", args);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("nonexistent");
    }

    [Test]
    public async Task CheckAsync_ExtractsPathForFileTools()
    {
        var agent = AgentWithRuleset(
            new PermissionRule("edit", "safe/*", PermissionAction.Allow),
            new PermissionRule("edit", "*", PermissionAction.Deny));

        var (svc, _) = CreateService(agent);

        var allowedResult = await svc.CheckAsync("code", "edit", Args(("path", "safe/file.txt")));
        await Assert.That(allowedResult.IsSuccess).IsTrue();
        await Assert.That(allowedResult.Value.Action).IsEqualTo(PermissionAction.Allow);

        var deniedResult = await svc.CheckAsync("code", "edit", Args(("path", "unsafe/file.txt")));
        await Assert.That(deniedResult.IsSuccess).IsTrue();
        await Assert.That(deniedResult.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task CheckAsync_ExtractsCommandForBashTool()
    {
        var agent = AgentWithRuleset(
            new PermissionRule("bash", "ls *", PermissionAction.Allow),
            new PermissionRule("bash", "*", PermissionAction.Deny));

        var (svc, _) = CreateService(agent);

        var allowedResult = await svc.CheckAsync("code", "bash", Args(("command", "ls -la /tmp")));
        await Assert.That(allowedResult.IsSuccess).IsTrue();
        await Assert.That(allowedResult.Value.Action).IsEqualTo(PermissionAction.Allow);

        var deniedResult = await svc.CheckAsync("code", "bash", Args(("command", "rm -rf /")));
        await Assert.That(deniedResult.IsSuccess).IsTrue();
        await Assert.That(deniedResult.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task CheckAsync_NoMatchingRule_DefaultsToAsk()
    {
        // Empty ruleset: every action falls through to Ask in the ruleset evaluator.
        var agent = AgentWithRuleset();
        var (svc, _) = CreateService(agent);

        var result = await svc.CheckAsync("code", "task", Args());

        await Assert.That(result.IsSuccess).IsTrue();
        // No asker → Ask falls back to Deny for safety.
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    [Test]
    public async Task GetRuleset_ReturnsAgentRuleset()
    {
        var agent = AgentWithRuleset(new PermissionRule("read", "*", PermissionAction.Allow));
        var (svc, _) = CreateService(agent);

        var ruleset = svc.GetRuleset("code");

        await Assert.That(ruleset.Rules.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task GetRuleset_UnknownAgent_ReturnsEmpty()
    {
        var (svc, _) = CreateService(AgentWithRuleset(new PermissionRule("read", "*", PermissionAction.Allow)));

        var ruleset = svc.GetRuleset("nonexistent");

        await Assert.That(ruleset.Rules.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AskUserAsync_NoAsker_ReturnsDeny()
    {
        var (svc, _) = CreateService(AgentWithRuleset(new PermissionRule("read", "*", PermissionAction.Allow)));

        var result = await svc.AskUserAsync(new PermissionRequest("read", "*", JsonDocument.Parse("{}").RootElement, Array.Empty<string>()));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Deny);
    }

    /// <summary>
    ///     Config store whose load blocks on a gate (#82 regression harness).
    ///     A blocking ctor would hang construction until <see cref="Release" />.
    /// </summary>
    private sealed class GatedConfigStore : IConfigStore
    {
        private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HarborConfig _config;

        public GatedConfigStore(HarborConfig config) => _config = config;

        public int LoadCalls { get; private set; }

        public void Release() => _gate.TrySetResult(true);

        public async Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default)
        {
            LoadCalls++;
            await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return Result.Success(_config);
        }

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default)
        {
            updater(_config);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<string>($"No API key for '{providerId}'."));
    }

    private static HarborConfig ConfigWith(params PermissionRule[] persisted)
    {
        var config = new HarborConfig { Provider = "kilocode", Model = "test-model" };
        config.Permissions["code"] = new List<PermissionRule>(persisted);
        return config;
    }

    [Test]
    public async Task Ctor_WithSlowConfigStore_ReturnsWithoutBlocking()
    {
        var registry = new AgentRegistry();
        registry.Register(AgentWithRuleset(new PermissionRule("read", "*", PermissionAction.Allow)));
        var store = new GatedConfigStore(ConfigWith(new PermissionRule("read", "*", PermissionAction.Allow)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ctorTask = Task.Run(
            () => new PermissionService(registry, NullLogger<PermissionService>.Instance, configStore: store),
            CancellationToken.None);
        var firstDone = await Task.WhenAny(ctorTask, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);

        await Assert.That(ReferenceEquals(firstDone, ctorTask)).IsTrue();
        var svc = await ctorTask.ConfigureAwait(false);
        await Assert.That(store.LoadCalls).IsEqualTo(1);
        store.Release();
        _ = await svc.SaveAsync(cts.Token).ConfigureAwait(false);
    }

    [Test]
    public async Task CheckAsync_WaitsForBackgroundPersistedLoad()
    {
        var registry = new AgentRegistry();
        registry.Register(AgentWithRuleset(new PermissionRule("bash", "*", PermissionAction.Ask)));
        var store = new GatedConfigStore(ConfigWith(new PermissionRule("bash", "make build", PermissionAction.Allow)));
        var svc = new PermissionService(registry, NullLogger<PermissionService>.Instance, configStore: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var checkTask = svc.CheckAsync("code", "bash", Args(("command", "make build")), cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None).ConfigureAwait(false);
        store.Release();
        var result = await checkTask.ConfigureAwait(false);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task CreateAsync_LoadsPersistedRulesBeforeReturning()
    {
        var registry = new AgentRegistry();
        registry.Register(AgentWithRuleset(new PermissionRule("read", "*", PermissionAction.Allow)));
        var store = new GatedConfigStore(ConfigWith(new PermissionRule("bash", "make *", PermissionAction.Allow)));
        store.Release();

        var svc = await PermissionService.CreateAsync(
            registry, NullLogger<PermissionService>.Instance, configStore: store).ConfigureAwait(false);
        var ruleset = svc.GetRuleset("code");

        await Assert.That(ruleset.Evaluate("bash", "make build")).IsEqualTo(PermissionAction.Allow);
        await Assert.That(ruleset.Evaluate("read", "any.txt")).IsEqualTo(PermissionAction.Allow);
    }

    [Test]
    public async Task AskUserAsync_CancelledCaller_ThrowsInsteadOfDeny()
    {
        Task<PermissionResponse> CancelledAsker(PermissionRequest req, CancellationToken ct) =>
            Task.FromCanceled<PermissionResponse>(ct);

        var (svc, _) = CreateService(
            AgentWithRuleset(new PermissionRule("bash", "*", PermissionAction.Ask)),
            CancelledAsker);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var request = new PermissionRequest("bash", "*", JsonDocument.Parse("{}").RootElement, Array.Empty<string>());

        Exception? caught = null;
        try
        {
            _ = await svc.AskUserAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNotNull();
    }

    [Test]
    public async Task CheckAsync_CancelledCaller_PropagatesCancellation()
    {
        Task<PermissionResponse> CancelledAsker(PermissionRequest req, CancellationToken ct) =>
            Task.FromCanceled<PermissionResponse>(ct);

        var (svc, _) = CreateService(
            AgentWithRuleset(new PermissionRule("bash", "*", PermissionAction.Ask)),
            CancelledAsker);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Exception? caught = null;
        try
        {
            _ = await svc.CheckAsync("code", "bash", Args(("command", "make build")), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNotNull();
    }
}
