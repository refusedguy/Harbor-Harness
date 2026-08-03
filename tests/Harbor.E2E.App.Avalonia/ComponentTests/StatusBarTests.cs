using Harbor.App.Avalonia.ViewModels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core.Enums;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     StatusBar component E2E tests — every visible state of the bottom
///     status bar.
/// </summary>
/// <remarks>
///     <para>
///         Tests cover: idle (grey dot, 'idle' text), running (amber dot,
///         'running' text), model label visible, token counts visible, cost
///         visible, session count visible, and a fully-populated status bar
///         with all groups showing real values.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class StatusBarTests : ComponentTestBase
{
    [Before(HookType.Test)]
    public async Task SetupAsync() => await GetDriverAsync().ConfigureAwait(false);

    /// <summary>
    ///     Idle state: grey status dot + 'idle' text.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_Idle_GreyDotAndIdleText()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.StatusText = "idle");
        await Task.Delay(200).ConfigureAwait(false);

        var hasIdle = await Driver.WaitForTextAsync("idle", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasIdle).IsTrue();

        var path = await CaptureAsync("statusbar-idle").ConfigureAwait(false);
    }

    /// <summary>
    ///     Running state: amber status dot + 'running' text.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_Running_AmberDotAndRunningText()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.StatusText = "running";
            Vm.IsRunning = true;
        });
        await Task.Delay(200).ConfigureAwait(false);

        var hasRunning = await Driver.WaitForTextAsync("running", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasRunning).IsTrue();

        var path = await CaptureAsync("statusbar-running").ConfigureAwait(false);

        UI(() =>
        {
            Vm.StatusText = "idle";
            Vm.IsRunning = false;
        });
    }

    /// <summary>
    ///     Model label: shows the active provider/model.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_ModelLabel_Visible()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.ProviderLabel = "ollama";
            Vm.ModelLabel = "qwen2.5-coder:7b";
        });
        await Task.Delay(200).ConfigureAwait(false);

        var hasModel = await Driver.WaitForTextAsync("qwen2.5-coder:7b", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasModel).IsTrue();

        var path = await CaptureAsync("statusbar-model-label").ConfigureAwait(false);
    }

    /// <summary>
    ///     Token counts: shows non-zero in/out values.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_TokenCounts_Visible()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.TokensIn = 1234;
            Vm.TokensOut = 5678;
        });
        await Task.Delay(200).ConfigureAwait(false);

        var hasIn = await Driver.WaitForTextAsync("1,234", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasOut = await Driver.WaitForTextAsync("5,678", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasIn && hasOut).IsTrue();

        var path = await CaptureAsync("statusbar-token-counts").ConfigureAwait(false);
    }

    /// <summary>
    ///     Cost: shows a non-zero dollar value with 4 decimal places.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_Cost_Visible()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.CostUsd = 0.0234m);
        await Task.Delay(200).ConfigureAwait(false);

        var hasCost = await Driver.WaitForTextAsync("$0.0234", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasCost).IsTrue();

        var path = await CaptureAsync("statusbar-cost").ConfigureAwait(false);
    }

    /// <summary>
    ///     Session count: shows the active session count.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_SessionCount_Visible()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() => Vm.ActiveSessionCount = 7);
        await Task.Delay(200).ConfigureAwait(false);

        var hasSession = await Driver.WaitForTextAsync("7 session", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasSession).IsTrue();

        var path = await CaptureAsync("statusbar-session-count").ConfigureAwait(false);
    }

    /// <summary>
    ///     Fully-populated: all groups have meaningful values simultaneously.
    /// </summary>
    [Test]
    [Category("E2E")]
    [Category("Component")]
    public async Task StatusBar_FullPopulation_AllGroupsVisible()
    {
        await Driver.ResetStateAsync().ConfigureAwait(false);

        UI(() =>
        {
            Vm.StatusText = "running";
            Vm.IsRunning = true;
            Vm.ProviderLabel = "anthropic";
            Vm.ModelLabel = "claude-sonnet-4";
            Vm.AgentLabel = "code";
            Vm.TokensIn = 12_345;
            Vm.TokensOut = 6_789;
            Vm.CostUsd = 0.1234m;
            Vm.ActiveSessionCount = 3;
        });
        await Task.Delay(250).ConfigureAwait(false);

        var hasRunning = await Driver.WaitForTextAsync("running", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        var hasCost = await Driver.WaitForTextAsync("$0.1234", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        await Assert.That(hasRunning && hasCost).IsTrue();

        var path = await CaptureAsync("statusbar-full-population").ConfigureAwait(false);

        UI(() =>
        {
            Vm.StatusText = "idle";
            Vm.IsRunning = false;
        });
    }
}
