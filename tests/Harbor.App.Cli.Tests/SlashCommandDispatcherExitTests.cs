using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.App.Cli.Repl;
using Harbor.Application.Configuration;
using Harbor.Application.Permissions;
using Harbor.Terminal.Abstractions;
using Harbor.Terminal.Abstractions.Renderers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.TestKit;
using FakeProviderRegistryBase = Harbor.TestKit.Generated.FakeProviderRegistry; // #1769: generated double

using FakeAgentBase = Harbor.TestKit.Generated.FakeAgent; // #1769: generated double


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
    private static SlashCommandDispatcher CreateDispatcher()
    {
        var configStore = new JsonConfigStore();
        var agents = new FakeAgentRegistry();
        return new SlashCommandDispatcher(
            NullLoggerFactory.Instance.CreateLogger<SlashCommandDispatcher>(),
            new FakeToolRegistry(),
            new FakeSessionStore(),
            new OnboardingWizard(configStore, new AuthStore(configStore)),
            new PermissionService(agents, NullLogger<PermissionService>.Instance));
    }

    private static async Task<SlashCommandOutcome> DispatchAsync(string input)
    {
        var dispatcher = CreateDispatcher();
        return await dispatcher.HandleAsync(
            input,
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

        public Task<Maybe<string>> ReadLineAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult(Maybe.From(string.Empty));

        public Task<Result> WriteAsync(string text, CancellationToken ct = default) => Task.FromResult(Result.Success());

        public Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> ClearAsync(CancellationToken ct = default) => Task.FromResult(Result.Success());

        public void Dispose() { }
    }

    private sealed class FakeAgent : FakeAgentBase
    {
        private readonly CancellationTokenSource _abortSource = new();

        public override CancellationToken AbortToken => _abortSource.Token;

        public override void RequestAbort() => _abortSource.Cancel();

        public override Maybe<AgentState> State => throw new NotSupportedException("Not used in these tests.");

        public override IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => new NopDisposable();

        public override Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public override Task<Result> PromptAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        private sealed class NopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FakeProviderRegistry : FakeProviderRegistryBase
    {
        public FakeProviderRegistry()
        {
            GetAllModelsAsyncResult = Result.Failure<IReadOnlyList<ModelInfo>>("No providers registered in tests.");
            GetModelsCachedAsyncResult = Result.Failure<IReadOnlyList<ModelInfo>>("No providers registered in tests.");
            UnregisterResult = Result.Failure("Unregistration is not supported in tests.");
        }

        public override Result<ILlmClient> GetClient(ProviderId providerId) =>
            Result.Failure<ILlmClient>("No providers registered in tests.");
    }
}
