using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     /exit and /quit must produce a managed quit signal
///     (<see cref="SlashCommandOutcome" />) that the REPL runner turns into a
///     normal shutdown (IPC stop, host dispose) — they must NOT kill the
///     process via Environment.Exit. If any of these tests regressed to
///     Environment.Exit, the test host itself would die and the run would fail.
/// </summary>
public class SlashCommandDispatcherExitTests
{
    private static SlashCommandDispatcher CreateDispatcher() =>
        new(NullLoggerFactory.Instance.CreateLogger<SlashCommandDispatcher>());

    private static async Task<SlashCommandOutcome> DispatchAsync(string input)
    {
        using var sp = new ServiceCollection().BuildServiceProvider();
        var dispatcher = CreateDispatcher();
        return await dispatcher.HandleAsync(
            input,
            sp,
            new FakeRenderer(),
            new FakeAgent(),
            new FakeAgentRegistry(),
            new JsonConfigStore(),
            new AuthStore(new JsonConfigStore()),
            new FakeProviderRegistry(),
            Session.Create("/tmp/harbor-tests", "code", "test-provider", "test-model"));
    }

    [Test]
    public async Task HandleAsync_Exit_ReturnsQuitSignalWithZeroExitCode()
    {
        SlashCommandOutcome outcome = await DispatchAsync("/exit");
        await Assert.That(outcome.ShouldQuit).IsTrue();
        await Assert.That(outcome.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_Quit_ReturnsQuitSignalWithZeroExitCode()
    {
        SlashCommandOutcome outcome = await DispatchAsync("/quit");
        await Assert.That(outcome.ShouldQuit).IsTrue();
        await Assert.That(outcome.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_Help_DoesNotRequestQuit()
    {
        SlashCommandOutcome outcome = await DispatchAsync("/help");
        await Assert.That(outcome.ShouldQuit).IsFalse();
    }

    [Test]
    public async Task HandleAsync_UnknownCommand_DoesNotRequestQuit()
    {
        SlashCommandOutcome outcome = await DispatchAsync("/definitely-not-a-command");
        await Assert.That(outcome.ShouldQuit).IsFalse();
    }

    [Test]
    public async Task HandleAsync_EmptyInput_DoesNotRequestQuit()
    {
        SlashCommandOutcome outcome = await DispatchAsync("/");
        await Assert.That(outcome.ShouldQuit).IsFalse();
    }

    // ── Minimal fakes: the quit path touches no dependencies, but the handler
    //    signature requires instances. /help exercises the renderer writer. ──

    private sealed class FakeRenderer : ITuiRenderer
    {
        public ITuiRenderContext Context { get; } = new CaptureRenderContext();

        public ViewRegistry Views { get; } = new();

        public ViewModelRegistry ViewModels { get; } = new();

        public Task<Result> InitializeAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public Task RenderAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(string.Empty));

        public Task<Result> WriteAsync(string text, CancellationToken ct = default) => Task.FromResult(Result.Success());

        public Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> ClearAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public void Dispose() { }
    }

    private sealed class FakeAgent : IAgent
    {
        public CancellationTokenSource AbortSource { get; } = new();

        public AgentState State => throw new NotSupportedException("Not used in these tests.");

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new NopDisposable();

        public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> PromptAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task WaitForIdleAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void ResetAbortSource() { }

        public void Initialize(Session session, AgentDefinition agent) { }

        public void Steer(AgentMessage message) { }


        public void Dispose() { }

        private sealed class NopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FakeAgentRegistry : IAgentRegistry
    {
        public IReadOnlyList<AgentDefinition> GetAllAgents() => Array.Empty<AgentDefinition>();

        public Result<AgentDefinition> GetAgent(AgentName name) =>
            Result.Failure<AgentDefinition>($"No agents registered in tests: {name.Value}");

        public Result Register(AgentDefinition agent) => Result.Failure("Registration is not supported in tests.");

        public Result Unregister(AgentName name) => Result.Failure("Unregistration is not supported in tests.");
    }

    private sealed class FakeProviderRegistry : IProviderRegistry
    {
        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => Array.Empty<ProviderId>();

        public Result<ILlmClient> GetClient(ProviderId providerId) =>
            Result.Failure<ILlmClient>("No providers registered in tests.");

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<IReadOnlyList<ModelInfo>>("No providers registered in tests."));

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<IReadOnlyList<ModelInfo>>("No providers registered in tests."));

        public void Register(ProviderId providerId, Func<ILlmClient> factory) { }

        public Result Unregister(ProviderId providerId) => Result.Failure("Unregistration is not supported in tests.");
    }
}
