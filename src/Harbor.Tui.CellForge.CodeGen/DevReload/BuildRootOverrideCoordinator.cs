using System;
using System.Collections.Generic;
using Harbor.Abstractions.Dsl;

namespace Harbor.Tui.CellForge.CodeGen.DevReload;

/// <summary>
///     Whether a dev override keeps the live widget instances and only feeds
///     them new values, or recreates them. The Blazor
///     <c>HasRemovedDirectParameters</c> rule decides: a removed parameter
///     invalidates an instance, so it must be recreated; otherwise the
///     instance is kept and re-rendered.
/// </summary>
public enum OverrideTransition
{
    /// <summary>Same instance, new values. Widget state, focus and handlers survive.</summary>
    Rerender,

    /// <summary>Fresh instances. Widget state, focus and handlers are reset.</summary>
    Recreate
}

/// <summary>
///     The explicit list of what a dev override resets. Everything not named
///     here belongs to the host and survives: the host itself, the event loop,
///     the capability probe and the terminal mode.
/// </summary>
/// <param name="ResetWidgetState">Per-widget view-model state is dropped.</param>
/// <param name="ResetFocus">Keyboard focus is dropped.</param>
/// <param name="ResetHandlers">Event handlers are unbound and rebound.</param>
public sealed record OverrideResetEffects(bool ResetWidgetState, bool ResetFocus, bool ResetHandlers)
{
    /// <summary>A re-render keeps every piece of host state.</summary>
    public static readonly OverrideResetEffects Rerender = new(false, false, false);

    /// <summary>A recreate drops all three and leaves the host intact.</summary>
    public static readonly OverrideResetEffects Recreate = new(true, true, true);

    /// <summary>The reset list implied by <paramref name="transition"/>.</summary>
    public static OverrideResetEffects For(OverrideTransition transition) =>
        transition == OverrideTransition.Recreate ? Recreate : Rerender;
}

/// <summary>
///     The four resources whose lifetime is the host's, not the tree's. Their
///     identities are captured before a swap so a receipt can prove they were
///     neither replaced nor restarted.
/// </summary>
public sealed record LiveHostResources(
    object Host,
    object EventLoop,
    object CapabilityProbe,
    object TerminalMode);

/// <summary>
///     Deterministic outcome of installing a dev root override.
/// </summary>
/// <param name="Transition">Rerender or recreate.</param>
/// <param name="Reset">The exact state dropped by this swap.</param>
/// <param name="Host">The same host resources the coordinator was built with.</param>
/// <param name="Signature">Template signature of the installed tree.</param>
/// <param name="Generation">Monotonic swap counter, for stale-override checks.</param>
public sealed record OverrideSwapReceipt(
    OverrideTransition Transition,
    OverrideResetEffects Reset,
    LiveHostResources Host,
    string Signature,
    int Generation);

/// <summary>
///     DEV-ONLY coordinator for a #2435 <c>BuildRoot</c> override. It installs
///     a delegate into <see cref="RootBuilderOverride.Current"/> and records
///     what the swap resets, without touching the host, its event loop, the
///     capability probe or the terminal mode.
/// </summary>
/// <remarks>
///     This type lives in the build-time tooling assembly, which the CLI
///     references only with <c>HarborWithDslDev=true</c>. It never runs in an
///     AOT publish. The delegate it installs is plain <see cref="Action"/> over
///     method groups: no reflection, no emit and no compiled markup at run
///     time. The #2433 interpreter supplies the tree and the #2434 watcher
///     decides when to call this; neither is duplicated here.
/// </remarks>
public sealed class BuildRootOverrideCoordinator
{
    private readonly LiveHostResources _host;
    private Action<Action<int>, Action<int, string>, Action>? _previous;
    private bool _installed;
    private int _generation;

    /// <param name="host">The long-lived host object.</param>
    /// <param name="eventLoop">The render/event loop, which keeps running.</param>
    /// <param name="capabilityProbe">The already-run capability probe.</param>
    /// <param name="terminalMode">The active terminal mode controller.</param>
    public BuildRootOverrideCoordinator(object host, object eventLoop, object capabilityProbe, object terminalMode)
    {
        _host = new LiveHostResources(
            host ?? throw new ArgumentNullException(nameof(host)),
            eventLoop ?? throw new ArgumentNullException(nameof(eventLoop)),
            capabilityProbe ?? throw new ArgumentNullException(nameof(capabilityProbe)),
            terminalMode ?? throw new ArgumentNullException(nameof(terminalMode)));
    }

    /// <summary>The captured host resources, readable before any swap.</summary>
    public LiveHostResources Host => _host;

    /// <summary>Number of successful swaps installed through this coordinator.</summary>
    public int Generation => _generation;

    /// <summary>Whether an override is currently installed.</summary>
    public bool IsInstalled => _installed;

    /// <summary>
    ///     Chooses rerender versus recreate from the parameter lists. Removing
    ///     a parameter recreates the instance; adding or changing one only
    ///     re-renders it.
    /// </summary>
    public static OverrideTransition Decide(
        IReadOnlyList<string> beforeParameters,
        IReadOnlyList<string> afterParameters)
    {
        ArgumentNullException.ThrowIfNull(beforeParameters);
        ArgumentNullException.ThrowIfNull(afterParameters);

        var after = new HashSet<string>(afterParameters, StringComparer.Ordinal);
        foreach (string name in beforeParameters)
        {
            if (!after.Contains(name))
                return OverrideTransition.Recreate;
        }

        return OverrideTransition.Rerender;
    }

    /// <summary>
    ///     A rejected parse or a logic change cannot be overridden: the former
    ///     has no new tree, the latter needs generated delegates that a
    ///     delegate swap cannot supply.
    /// </summary>
    public static bool CanOverride(ReloadOutcome outcome) =>
        outcome is not (ReloadOutcome.Rejected or ReloadOutcome.RestartRequired);

    /// <summary>
    ///     Installs <paramref name="builder"/> as the compiled root's override
    ///     and returns what the swap reset. Call <see cref="Restore"/> to put
    ///     the previous builder back.
    /// </summary>
    public OverrideSwapReceipt Install(
        Action<Action<int>, Action<int, string>, Action> builder,
        ReloadOutcome outcome,
        IReadOnlyList<string> beforeParameters,
        IReadOnlyList<string> afterParameters,
        string signature)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(signature);
        if (!CanOverride(outcome))
            throw new ArgumentException(
                $"outcome {outcome} cannot be applied as a root override", nameof(outcome));
        if (_installed)
            throw new InvalidOperationException("an override is already installed; restore it first");

        var transition = Decide(beforeParameters, afterParameters);
        _previous = RootBuilderOverride.Install(builder);
        _installed = true;
        _generation++;
        return new OverrideSwapReceipt(
            transition,
            OverrideResetEffects.For(transition),
            _host,
            signature,
            _generation);
    }

    /// <summary>Restores the builder that was installed before this swap.</summary>
    public void Restore()
    {
        if (!_installed)
            return;
        RootBuilderOverride.Install(_previous);
        _previous = null;
        _installed = false;
    }

    /// <summary>
    ///     Builds the override delegate from a #2433 interpreter's live tree.
    ///     The replay emits the same dense pre-order sequence numbers the
    ///     generated <c>BuildRoot</c> uses as cell-diff keys.
    /// </summary>
    public static Action<Action<int>, Action<int, string>, Action> FromInterpreter(DevReloadInterpreter source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return (begin, text, end) =>
        {
            int sequence = 0;
            if (source.LiveRootId != 0)
                Replay(source, source.LiveRootId, begin, text, end, ref sequence);
        };
    }

    private static void Replay(
        DevReloadInterpreter source,
        int id,
        Action<int> begin,
        Action<int, string> text,
        Action end,
        ref int sequence)
    {
        begin(sequence++);
        string? value = source.TextOf(id);
        if (value is not null)
            text(sequence++, value);

        foreach (int child in source.ChildrenOf(id))
            Replay(source, child, begin, text, end, ref sequence);

        end();
    }
}
