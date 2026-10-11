using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.App.Cli.Hosting;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Permissions;
using Harbor.Registries.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #1125: CellForge-оверрайд <c>IPermissionService</c> обязан прокидывать
///     <c>IConfigStore</c> — иначе persisted-правила из <c>config.json</c> в
///     интерактивном пути не читаются и чек упирается в approval-карточку.
///     Фабрика вызывается напрямую: env-гейт
///     <c>CellForgeModule.IsApprovalPromptAvailable</c> в CI недоступен
///     (перенаправленный stdin), поэтому через <c>AddCellForge</c> оверрайд
///     там не зарегистрируется.
/// </summary>
public class CellForgePermissionStoreTests
{
    private sealed class StubConfigStore : IConfigStore
    {
        private readonly HarborConfig _config;

        public StubConfigStore(HarborConfig config) => _config = config;

        public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Result.Success(_config));

        public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult(Result.Failure<string>($"No API key for '{providerId}'."));
    }

    [Test]
    public async Task CreatePermissionService_AppliesPersistedRule_WithoutAsking()
    {
        var registry = new AgentRegistry();
        registry.Register(new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "Default coding agent.",
            "test-model",
            "test",
            new PermissionRuleset(
                new[] { new PermissionRule("bash", "*", PermissionAction.Ask) })));

        var config = new HarborConfig { Provider = "kilocode", Model = "test-model" };
        config.Permissions["code"] = new List<PermissionRule>(
            new[] { new PermissionRule("bash", "make build", PermissionAction.Allow) });

        var services = new ServiceCollection();
        services.AddSingleton<IAgentRegistry>(registry);
        services.AddSingleton<IConfigStore>(new StubConfigStore(config));
        services.AddSingleton<ILogger<PermissionService>>(NullLogger<PermissionService>.Instance);
        services.AddSingleton(new CellForgePermissionAsker(
            () => throw new InvalidOperationException("asker must not be called for a persisted rule"),
            new ApprovalCoordinator(NullLogger<ApprovalCoordinator>.Instance)));
        using var provider = services.BuildServiceProvider();

        PermissionService svc = CellForgeModule.CreatePermissionService(provider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var doc = JsonDocument.Parse("""{"command":"make build"}""");
        var result = await svc.CheckAsync("code", "bash", doc.RootElement, cts.Token);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Action).IsEqualTo(PermissionAction.Allow);
    }
}
