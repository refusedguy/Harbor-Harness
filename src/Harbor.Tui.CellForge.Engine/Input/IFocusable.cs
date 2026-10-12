// TR4 (#1223, epic #1195): focusable input vocabulary —
// IInputHandler three-phase contract (PreviewKey capture / HandleKey bubble /
// Unhandled remainder) + IFocusable + FocusPolicy + FocusableBase.
// Behavior port of Termina's two-phase routing (Page.KeyBindings capture ->
// focused bubble -> ViewModel.Input remainder) and ConsoleEx's
// PreviewKeyPressed tunnel, minus both frameworks' hosts: the remainder here
// is a plain Action, not an R3 stream, and there are no per-window threads
// (explicit non-goal from the issue). Same leaf rule as every file in this
// directory: BCL-only, AOT-compatible, zero Harbor references,
// render-thread owned, chat untouched.

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// Which end of the focus order owns new focus (Termina analogue:
/// <c>ChatPage.FocusPolicy = FirstFocusable</c> on navigate).
/// The policy names the SEED only — traversal itself lives in
/// <see cref="FocusRouter"/> (flat Tab order) and <see cref="FocusStack"/>
/// (modal stack). The host applies the seed after rebuilding either.
/// </summary>
public enum FocusPolicy : byte
{
    /// <summary>No automatic focus (host drives everything).</summary>
    None = 0,

    /// <summary>Focus the first focusable entry.</summary>
    FirstFocusable = 1,

    /// <summary>Focus the last focusable entry.</summary>
    LastFocusable = 2,
}

/// <summary>
/// Three-phase key handling (ConsoleEx <c>PreviewKeyPressed</c> tunnel +
/// <c>HandleKey</c> bubble, Termina capture-then-focused two-phase):
/// <list type="number">
///   <item><description><see cref="PreviewKey"/> — capture/tunnel. The router
///     walks the stack bottom-up; the first <c>true</c> consumes the key and
///     the bubble never runs. Must stay pure-ish: no focus moves, no stack
///     mutation — observe and veto only.</description></item>
///   <item><description><see cref="HandleKey"/> — bubble. The router walks the
///     stack top-down from the focused entry; the first <c>true</c> consumes
///     the key.</description></item>
///   <item><description><see cref="Unhandled"/> — remainder (Termina
///     <c>ViewModel.Input</c> analogue, minus R3). Runs only when capture,
///     preview and bubble all declined. Default is a no-op.</description></item>
/// </list>
/// Exceptions propagate to the render loop — same contract as
/// <c>Rendering.INode.Paint</c>. A handler that cannot fail should not throw.
/// </summary>
public interface IInputHandler
{
    /// <summary>Capture phase. Returns true when the key is consumed.</summary>
    bool PreviewKey(KeyEvent key);

    /// <summary>Bubble phase. Returns true when the key is consumed.</summary>
    bool HandleKey(KeyEvent key);

    /// <summary>Remainder: capture, preview and bubble all declined.</summary>
    void Unhandled(KeyEvent key);
}

/// <summary>
/// Focusable input target: the flat-order <see cref="IFocusTarget"/> contract
/// plus the three-phase <see cref="IInputHandler"/> contract plus the two
/// focus-state reads the stack needs. <see cref="FocusRouter"/> keeps working
/// on bare <see cref="IFocusTarget"/> — nothing there was widened.
/// </summary>
public interface IFocusable : IFocusTarget, IInputHandler
{
    /// <summary>Whether this entry may own focus right now (dynamic: a
    /// disabled control keeps its seat but is skipped by routing).</summary>
    bool CanFocus { get; }

    /// <summary>Whether this entry owns focus (set via
    /// <see cref="IFocusTarget.OnFocusChanged"/>).</summary>
    bool IsFocused { get; }
}

/// <summary>
/// Boilerplate base: id storage, <see cref="IFocusable.IsFocused"/> tracking
/// through <see cref="IFocusTarget.OnFocusChanged"/>, capture/bubble decline
/// by default, remainder no-op. Subclasses override the phases they own.
/// </summary>
public abstract class FocusableBase : IFocusable
{
    /// <summary>Creates an entry with the given stable id.</summary>
    /// <param name="id">Stable id (same uniqueness rule as
    /// <see cref="FocusRouter"/> entries).</param>
    protected FocusableBase(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public virtual bool CanFocus => true;

    /// <inheritdoc />
    public bool IsFocused { get; private set; }

    /// <inheritdoc />
    public virtual void OnFocusChanged(bool focused) => IsFocused = focused;

    /// <inheritdoc />
    public virtual bool PreviewKey(KeyEvent key) => false;

    /// <inheritdoc />
    public virtual bool HandleKey(KeyEvent key) => false;

    /// <inheritdoc />
    public virtual void Unhandled(KeyEvent key)
    {
    }
}
