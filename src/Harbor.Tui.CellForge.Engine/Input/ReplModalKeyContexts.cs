// #3617: the REPL's modal barriers as declarative key contexts. One context
// per modal region; the host asks Active(...) for the topmost open context and
// calls KeyContext.TryMatch on it. BCL-only, AOT-compatible, zero Harbor refs —
// same leaf rule as KeyContext.cs / KeyBindings.cs.
//
// This is NOT a second input router and NOT a second action vocabulary: the
// action ids below are opaque strings the HOST switches on, ChatKeyMap still
// owns the composer's action names, and LeaderKeyRouter still owns chords.
// The contexts only group gestures and name the barrier order.

namespace Harbor.Tui.CellForge.Input;

/// <summary>
/// Which modal barriers are currently up. Built fresh from live host state on
/// each keystroke so the context selection is a pure function of that snapshot.
/// </summary>
public readonly record struct ReplModalState(
    bool TerminalAttached,
    bool SheetOpen,
    bool MarkupVisible,
    bool ImageVisible,
    bool OnboardingActive,
    bool PaletteVisible,
    bool SetupVisible);

/// <summary>
/// The REPL's named modal key contexts (#3617). Gestures the host itself owns
/// (markup save/send, image annotate) are bound here; every other key is left
/// to the modal's own handler, and the barrier still swallows the key when no
/// binding matches. The barrier order in <see cref="Active"/> is the same order
/// <c>ReplInputLoop.HandleKeyAsync</c> has always used.
/// </summary>
public sealed class ReplModalKeyContexts
{
    /// <summary>Opaque action id: bake the annotated markup copy (host effect).</summary>
    public const string MarkupSave = "markup.save";

    /// <summary>Opaque action id: send the saved annotated copy to the agent (host effect).</summary>
    public const string MarkupSend = "markup.send";

    /// <summary>Opaque action id: swap the image viewer for the markup overlay (host effect).</summary>
    public const string ImageAnnotate = "image.annotate";

    /// <summary>Floating terminal (#1491): the leader router and live session own every key.</summary>
    public KeyContext Terminal { get; } = new("terminal");

    /// <summary>Open sheet (#2376): the overlay owns its focus trap and dismiss.</summary>
    public KeyContext Sheet { get; } = new("sheet");

    /// <summary>Markup overlay (#400): the two host-effect chords live here.</summary>
    public KeyContext Markup { get; } = new("markup");

    /// <summary>Image viewer (#387): the annotate gesture lives here.</summary>
    public KeyContext Image { get; } = new("image");

    /// <summary>Onboarding flow (#1248): the flow consumes its own keys.</summary>
    public KeyContext Onboarding { get; } = new("onboarding");

    /// <summary>Command palette: frame-carried continuations stay with the widget.</summary>
    public KeyContext Palette { get; } = new("palette");

    /// <summary>Setup checklist (#383): dismiss keys stay with the overlay.</summary>
    public KeyContext Setup { get; } = new("setup");

    /// <summary>
    /// Binds the host-owned modal gestures. The two Ctrl+S spellings keep the
    /// legacy "s or S with Ctrl" check honest for both kitty and legacy
    /// encoders; Enter is named (not a char) so Ctrl+Enter is one gesture.
    /// </summary>
    public ReplModalKeyContexts()
    {
        Markup.Bind(KeyGesture.ForChar('s', KeyModifiers.Ctrl), MarkupSave);
        Markup.Bind(KeyGesture.ForChar('S', KeyModifiers.Ctrl | KeyModifiers.Shift), MarkupSave);
        Markup.Bind(KeyGesture.ForKey(KeyCode.Enter, KeyModifiers.Ctrl), MarkupSend);
        Image.Bind(KeyGesture.ForChar('m'), ImageAnnotate);
    }

    /// <summary>
    /// Topmost open context, in the barrier order the REPL has always used:
    /// terminal → sheet → markup → image → onboarding → palette → setup.
    /// Null when no modal is up. Selection only; no key is dispatched here.
    /// </summary>
    public KeyContext? Active(in ReplModalState state)
    {
        if (state.TerminalAttached)
        {
            return Terminal;
        }

        if (state.SheetOpen)
        {
            return Sheet;
        }

        if (state.MarkupVisible)
        {
            return Markup;
        }

        if (state.ImageVisible)
        {
            return Image;
        }

        if (state.OnboardingActive)
        {
            return Onboarding;
        }

        if (state.PaletteVisible)
        {
            return Palette;
        }

        if (state.SetupVisible)
        {
            return Setup;
        }

        return null;
    }
}
