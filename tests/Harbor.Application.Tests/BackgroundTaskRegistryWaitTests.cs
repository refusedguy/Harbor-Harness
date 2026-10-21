using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Application.Agents;
using Harbor.TestKit;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #1192 (steal/textual <c>wait_for_idle</c>): deterministic test-wait
///     pairing for <see cref="IBackgroundTaskRegistry" /> — poll the registry,
///     not the clock.
/// </summary>
public class BackgroundTaskRegistryWaitTests
{
    private static Func<CancellationToken, Task<Result<SubAgentRunResult>>> QuickRun(
        string output = "done") =>
        _ => Task.FromResult(Result.Success(new SubAgentRunResult("s", "explore", output, 0)));

    private static Func<CancellationToken, Task<Result<SubAgentRunResult>>> HangingRun() =>
        async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return Result.Success(new SubAgentRunResult("s", "explore", "unreachable", 0));
        };

    [Test]
    public async Task WaitForIdle_EmptyRegistry_IsImmediatelyIdle()
    {
        var registry = new BackgroundTaskRegistry();

        await Assert.That(await registry.WaitForIdleAsync(timeout: TimeSpan.FromMilliseconds(50))).IsTrue();
    }

    [Test]
    public async Task WaitForIdle_CompletingRun_DrainsWithoutSleep()
    {
        var registry = new BackgroundTaskRegistry();
        var gate = new TaskCompletionSource<bool>();
        var started = registry.Start(
            "explore",
            "s-1",
            async ct =>
            {
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                return Result.Success(new SubAgentRunResult("s-1", "explore", "done", 1));
            },
            CancellationToken.None);
        await Assert.That(started).IsSuccessful();

        gate.SetResult(true);
        await Assert.That(await registry.WaitForIdleAsync("s-1", TimeSpan.FromSeconds(10))).IsTrue();
    }

    [Test]
    public async Task WaitForIdle_HangingRun_TimesOutInsteadOfHanging()
    {
        var registry = new BackgroundTaskRegistry();
        var started = registry.Start("explore", "s-1", HangingRun(), CancellationToken.None);
        await Assert.That(started).IsSuccessful();

        await Assert.That(await registry.WaitForIdleAsync("s-1", TimeSpan.FromMilliseconds(50))).IsFalse();

        registry.CancelSession("s-1");
        await Assert.That(await registry.WaitForIdleAsync("s-1", TimeSpan.FromSeconds(10))).IsTrue();
    }

    [Test]
    public async Task WaitForIdle_SessionFilter_IgnoresOtherSessions()
    {
        var registry = new BackgroundTaskRegistry();
        var started = registry.Start("explore", "other", HangingRun(), CancellationToken.None);
        await Assert.That(started).IsSuccessful();

        await Assert.That(await registry.WaitForIdleAsync("mine", TimeSpan.FromMilliseconds(50))).IsTrue();
        await Assert.That(await registry.WaitForIdleAsync(timeout: TimeSpan.FromMilliseconds(20))).IsFalse();

        registry.CancelSession("other");
    }

    [Test]
    public async Task WaitForIdle_QuickRun_ResolvesTrue()
    {
        var registry = new BackgroundTaskRegistry();
        var started = registry.Start("explore", "s-1", QuickRun(), CancellationToken.None);
        await Assert.That(started).IsSuccessful();

        await Assert.That(await registry.WaitForIdleAsync("s-1", TimeSpan.FromSeconds(10))).IsTrue();
    }
}
