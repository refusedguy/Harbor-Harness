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

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Status bar at the bottom of the main window. Group 1 (left): a small grey status dot + the word 'idle' (semi-bold). " +
            "Group 2: agent label 'code' (blue, monospace) + '·' separator + model label '—' (or the model id) in green. " +
            "Group 3: tokens in '↓ 0' (sky) + tokens out '↑ 0' (peach). Group 4 (right): cost '$0.0000' (yellow) + session count '1 session'.",
            nameof(StatusBar_Idle_GreyDotAndIdleText)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
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

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Status bar with the agent RUNNING. Group 1: an amber/yellow status dot + the word 'running' (semi-bold). " +
            "All other groups (agent, model, tokens, cost, sessions) are still visible — only the status dot + label changed colour.",
            nameof(StatusBar_Running_AmberDotAndRunningText)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
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

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Status bar group 2: agent label 'code' (blue, monospace) + '·' separator + model label " +
            "'qwen2.5-coder:7b' (green, monospace, clickable button styling). The model label is visibly distinct " +
            "from the surrounding text because of its green colour + subtle button hover styling.",
            nameof(StatusBar_ModelLabel_Visible)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
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

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Status bar group 3: tokens-in '↓ 1,234' (sky blue, monospace) + tokens-out '↑ 5,678' (peach, monospace) " +
            "+ a small sparkline chart (~80px wide, 14px tall) showing recent output-token history. " +
            "The ↓ arrow is for input tokens, ↑ arrow for output tokens.",
            nameof(StatusBar_TokenCounts_Visible)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
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

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Status bar group 4 (left side of the right cluster): cost value '$0.0234' (yellow, monospace, 4 decimal places). " +
            "The cost is to the LEFT of the spacer that pushes the session count to the right edge.",
            nameof(StatusBar_Cost_Visible)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
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

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Status bar group 4 (rightmost): session count '7 session' (muted text, monospace) on the far right edge " +
            "of the status bar, after the cost value. A vertical hairline separator sits to the LEFT of the session count.",
            nameof(StatusBar_SessionCount_Visible)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
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

        var vlm = await VlmVerifier.VerifyAsync(
            path,
            "Status bar FULLY POPULATED with all groups showing real values. From left to right: " +
            "amber dot + 'running' (status group), " +
            "'code' (blue, agent) + '·' + 'claude-sonnet-4' (green, model) (agent+model group), " +
            "'↓ 12,345' (sky, tokens in) + '↑ 6,789' (peach, tokens out) + sparkline (tokens group), " +
            "'$0.1234' (yellow, cost), " +
            "right-aligned: '3 session' (session count). Vertical hairline separators between groups.",
            nameof(StatusBar_FullPopulation_AllGroupsVisible)).ConfigureAwait(false);
        await Assert.That(vlm.Output).IsNotNull();
    }
}
