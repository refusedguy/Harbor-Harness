using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.E2E.Framework;

/// <summary>
///     Base class for renderer-agnostic, state-based E2E tests. Extends
///     <see cref="E2eTestBase" /> with the <see cref="StateTestRunner" />
///     helpers so tests can assert on <see cref="UiState" /> snapshots
///     instead of hardcoding renderer-specific screen text.
/// </summary>
/// <remarks>
///     <para>
///         <b>Usage:</b> derive from this class and call
///         <see cref="AssertStateRenderedAsync" /> / <see cref="RunStateSequenceAsync" />
///         with <see cref="UiState" /> snapshots built via the
///         <see cref="StateTestRunner" /> factory methods.
///     </para>
///     <para>
///         <b>Renderer coverage:</b> the same test class can run against
///         multiple drivers (TUI, Avalonia, CLI) by parameterising the
///         <see cref="IE2eDriver" /> — the state-to-text mapping is
///         renderer-agnostic.
///     </para>
/// </remarks>
public abstract class StateTestBase : E2eTestBase
{
    /// <summary>
    ///     Assert that <paramref name="driver" /> renders the expected text
    ///     derived from <paramref name="state" /> within <paramref name="timeout" />.
    /// </summary>
    /// <remarks>
    ///     Delegates to <see cref="StateTestRunner.AssertStateRenderedAsync" />.
    ///     Pass an explicit <paramref name="expectedText" /> to override the
    ///     auto-derived value when the renderer formats text differently.
    /// </remarks>
    protected async Task<bool> AssertStateRenderedAsync(
        IE2eDriver driver,
        UiState state,
        string? expectedText = null,
        TimeSpan? timeout = null)
    {
        return await StateTestRunner.AssertStateRenderedAsync(driver, state, expectedText, timeout).ConfigureAwait(false);
    }

    /// <summary>
    ///     Drive <paramref name="driver" /> through a sequence of
    ///     <see cref="UiState" /> snapshots, asserting each expected text
    ///     appears before moving to the next step.
    /// </summary>
    protected async Task<bool> RunStateSequenceAsync(
        IE2eDriver driver,
        params (UiState state, string? expectedText)[] steps)
    {
        return await StateTestRunner.RunStateSequenceAsync(driver, steps).ConfigureAwait(false);
    }

    /// <summary>
    ///     Extract the expected visible text from a <see cref="UiState" />
    ///     snapshot (streaming buffer, thinking buffer, transcript lines,
    ///     status, session chrome).
    /// </summary>
    protected static string ExtractExpectedText(UiState state) => StateTestRunner.ExtractExpectedText(state);
}
