using System.Text;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// [#784] <c>DialogOverlay.HandleKey(in KeyEvent)</c> never reads
/// <see cref="KeyEvent.EventType"/>, so a key RELEASE applies the same
/// transition its PRESS applied.
///
/// THE QUESTION #784 ASKS, AND THE ANSWER
/// --------------------------------------
/// #784 asks whether press/release are told apart on this path at all, or
/// whether the distinction is lost at a boundary. It is not lost — the type
/// carries it, and the loss is in the one method that ignores it:
///
///   * <c>KeyEvent.EventType</c> exists (<c>KeyEventType.Press/Repeat/
///     Release</c>), <c>EscapeSequenceParser.DecodeKittyKey</c> decodes the
///     kitty event-type sub-parameter into it (EscapeSequenceParser.cs:690),
///     and <c>KeyEventMapper.ToDto</c> DROPS releases by returning null
///     (KeyEventMapper.cs:26). So the vocabulary is present and one consumer
///     already uses it — <c>ApprovalGateView.HandleKey</c> gates on
///     <c>Press or Repeat</c> (ApprovalGateView.cs:249).
///   * <c>DialogOverlay.HandleKey(in KeyEvent)</c> is the outlier: it switches
///     on <c>key.Key</c> alone. A release of <c>KeyCode.Char</c> reaches the
///     <c>_input += text</c> arm and types the character a SECOND time.
///
/// So the defect is in <c>HandleKey</c>, not in a converter, and the fix
/// belongs next to the gate <c>ApprovalGateView</c> already has.
///
/// THE REACHABILITY VERDICT — MEASURED, NOT ASSUMED
/// -------------------------------------------------
/// The issue's reporter wrote "reachability not proven". Measured against the
/// tree, a release cannot REACH this method from the product today, and the
/// reason is upstream of the method: the app never asks the terminal for
/// event types at all.
///
///   * <c>TerminalQueries.KittyPush</c> (CSI <c>&gt; flags u</c>) is defined
///     and referenced only from tests. The enter-alt-screen sequence
///     (CellForgeReplRunner.cs:81) is alt-screen + hide-cursor + paste +
///     mouse, and no kitty push. The prober QUERIES the current flags
///     (CapabilityProber.cs:87) and records them, then pushes nothing.
///   * Without the push there is no report-event-types flag, so a conforming
///     terminal never sends the `;3` event-type sub-parameter, so
///     <c>KeyEventType.Release</c> is never produced by the input stream.
///   * Independently, this overload has no product caller either. There is no
///     route into <c>DialogOverlay</c>'s kitty overload at all:
///     <c>DialogOverlayLayer</c> declares no <c>OnKey</c> member — it takes the
///     <c>IOverlayLayer.OnKey</c> default (<c>=&gt; false</c>) — and
///     <c>OverlayStack.RouteKey</c>, the only caller that would ever offer a key
///     to a layer, has NO call site outside tests. The two <c>DialogOverlay</c>
///     instances are <c>ChatScreen.Dialog</c> (never shown: no product code calls
///     any <c>Show*</c> on it) and <c>OnboardingFlow.Dialog</c> (a class with no
///     product reference at all, and its only ingress is the legacy
///     <c>ConsoleKeyInfo</c> overload, which has no event type to lose).
///     (#858 corrected this sentence: it used to name a
///     <c>DialogOverlayLayer.OnKey</c> that has never existed, so it was wrong
///     whichever way #812 resolves.)
///
/// So this test is a CONTRACT, not a reproduction of a live crash: it pins the
/// rule "a release applies nothing" on the one method that would break it the
/// day a key source is wired up. It is written to be red against the tree as
/// it stands, and it goes green when the gate lands.
///
/// NOTE ON THE LEGACY OVERLOAD: <c>HandleKey(ConsoleKeyInfo)</c> cannot have
/// this bug. <see cref="ConsoleKeyInfo"/> has no event-type field — the BCL
/// only ever reports presses — so there is nothing there to drop. The defect
/// is specific to the kitty overload, and that asymmetry is itself the reason
/// the two cannot be compared gesture-for-gesture without this gate.
/// </summary>
public class DialogKeyReleaseTests
{
    private static DialogOverlay Shown(DialogKind kind)
    {
        var dialog = new DialogOverlay();
        switch (kind)
        {
            case DialogKind.Prompt:
                dialog.ShowPrompt("title", "message", string.Empty);
                break;
            case DialogKind.Select:
                dialog.ShowSelect("title", "message", ["one", "two", "three"], 1);
                break;
            case DialogKind.Radio:
                dialog.ShowRadio("title", "message", ["one", "two", "three"], 1);
                break;
            case DialogKind.Multiline:
                dialog.ShowMultiline("title", "message", string.Empty);
                break;
            case DialogKind.Approval:
                dialog.ShowApproval("bash", "rm -rf?", "+added\n-removed", "src/x.cs", 1);
                break;
        }
        return dialog;
    }

    private static KeyEvent Release(KeyCode key, Rune character = default, KeyModifiers mods = KeyModifiers.None) =>
        new(key, character, mods, KeyEventType.Release, isKittyEncoded: true);

    /// <summary>
    /// The double-apply, stated as a failing expectation. A typed character
    /// must land once: press applies the insert, and the release of the SAME
    /// physical key must not insert the same character again.
    /// </summary>
    [Test]
    public async Task Release_Of_A_Typed_Char_Does_Not_Apply_The_Transition_A_Second_Time()
    {
        var prompt = Shown(DialogKind.Prompt);
        var typed = new Rune('x');

        // Press applies it: one 'x' in the buffer.
        await Assert.That(prompt.HandleKey(KeyEvent.Char(typed))).IsTrue();
        await Assert.That(prompt.Input).IsEqualTo("x");

        // The release of the same physical key must apply nothing.
        await Assert.That(prompt.HandleKey(Release(KeyCode.Char, typed))).IsFalse();
        await Assert.That(prompt.Input).IsEqualTo("x")
            .Because("a release carries no key meaning; typing 'x' twice needs two presses");
    }

    /// <summary>
    /// The same rule for a state transition rather than a buffer edit: a
    /// selection moved by Down must not move again when Down is released.
    /// </summary>
    [Test]
    public async Task Release_Of_An_Arrow_Does_Not_Move_The_Selection_A_Second_Time()
    {
        var select = Shown(DialogKind.Select);
        int before = select.SelectedIndex;

        await Assert.That(select.HandleKey(KeyEvent.Simple(KeyCode.Down))).IsTrue();
        int afterPress = select.SelectedIndex;
        await Assert.That(afterPress).IsNotEqualTo(before);

        await Assert.That(select.HandleKey(Release(KeyCode.Down))).IsFalse();
        await Assert.That(select.SelectedIndex).IsEqualTo(afterPress)
            .Because("the release of Down is not a second Down");
    }

    /// <summary>
    /// Repeat is the other half of the rule and the reason the gate cannot be
    /// written as "ignore everything that is not a press": auto-repeat
    /// (holding the key) legitimately re-applies, so the accepted set is
    /// {Press, Repeat} — exactly what <c>ApprovalGateView</c> already accepts.
    /// </summary>
    [Test]
    public async Task Repeat_Of_An_Arrow_Still_Moves_The_Selection()
    {
        var select = Shown(DialogKind.Select);
        int before = select.SelectedIndex;

        var repeat = new KeyEvent(KeyCode.Down, default, KeyModifiers.None, KeyEventType.Repeat, isKittyEncoded: true);
        await Assert.That(select.HandleKey(repeat)).IsTrue();
        await Assert.That(select.SelectedIndex).IsNotEqualTo(before)
            .Because("auto-repeat is a real second press and must keep working");
    }

    /// <summary>
    /// Every kind, every key that mutates one, checked as a table — the shape
    /// #775 already uses for the press/press parity, applied to the phase
    /// instead. A release must return false and leave the dialog byte-identical.
    /// </summary>
    [Test]
    public async Task No_Kind_Applies_Any_Transition_On_Release()
    {
        (DialogKind Kind, KeyCode Key, Rune Char)[] cases =
        [
            (DialogKind.Prompt, KeyCode.Char, new Rune('x')),
            (DialogKind.Prompt, KeyCode.Backspace, default),
            (DialogKind.Select, KeyCode.Up, default),
            (DialogKind.Select, KeyCode.Down, default),
            (DialogKind.Select, KeyCode.PageUp, default),
            (DialogKind.Select, KeyCode.Home, default),
            (DialogKind.Select, KeyCode.End, default),
            (DialogKind.Radio, KeyCode.Up, default),
            (DialogKind.Radio, KeyCode.Down, default),
            (DialogKind.Radio, KeyCode.Left, default),
            (DialogKind.Radio, KeyCode.Right, default),
            (DialogKind.Multiline, KeyCode.Up, default),
            (DialogKind.Multiline, KeyCode.Down, default),
            (DialogKind.Multiline, KeyCode.Left, default),
            (DialogKind.Multiline, KeyCode.Right, default),
            (DialogKind.Multiline, KeyCode.Home, default),
            (DialogKind.Multiline, KeyCode.End, default),
            (DialogKind.Multiline, KeyCode.Backspace, default),
            (DialogKind.Multiline, KeyCode.Delete, default),
            (DialogKind.Multiline, KeyCode.Char, new Rune('x')),
            (DialogKind.Approval, KeyCode.Up, default),
            (DialogKind.Approval, KeyCode.Down, default),
            (DialogKind.Approval, KeyCode.Backspace, default),
            (DialogKind.Approval, KeyCode.Delete, default),
        ];

        foreach ((DialogKind kind, KeyCode key, Rune character) in cases)
        {
            var dialog = Shown(kind);
            string input = dialog.Input;
            string editor = dialog.Editor.Text;
            int selected = dialog.SelectedIndex;
            int focused = dialog.FocusedButtonIndex;

            bool consumed = dialog.HandleKey(Release(key, character));

            await Assert.That(consumed).IsFalse()
                .Because($"{kind} + {key} released must consume nothing");
            await Assert.That(dialog.Input).IsEqualTo(input)
                .Because($"{kind} + {key} released must not edit the buffer");
            await Assert.That(dialog.Editor.Text).IsEqualTo(editor)
                .Because($"{kind} + {key} released must not edit the editor");
            await Assert.That(dialog.SelectedIndex).IsEqualTo(selected)
                .Because($"{kind} + {key} released must not move the selection");
            await Assert.That(dialog.FocusedButtonIndex).IsEqualTo(focused)
                .Because($"{kind} + {key} released must not move the focus");
        }
    }

    /// <summary>
    /// <c>Escape</c> is the one that would be most damaging to double-apply,
    /// and it is also the one a host is most likely to reach for: the dialog
    /// dismisses on it, and a dismissed dialog never repaints, so a second
    /// application is invisible rather than merely redundant.
    /// </summary>
    [Test]
    public async Task Release_Of_Escape_Does_Not_Dismiss()
    {
        var prompt = Shown(DialogKind.Prompt);

        await Assert.That(prompt.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsTrue();
        await Assert.That(prompt.Visible).IsFalse();

        // A dialog that is already dismissed ignores everything, so the
        // release assertion needs a fresh dialog mid-life to be meaningful.
        var live = Shown(DialogKind.Prompt);
        await Assert.That(live.HandleKey(Release(KeyCode.Escape))).IsFalse();
        await Assert.That(live.Visible).IsTrue()
            .Because("only the press dismisses");
    }

    /// <summary>
    /// Pins WHERE the rule has to be enforced, so a future fix cannot put it
    /// in the wrong place. The two facts this issue turned up:
    ///
    ///   1. The gate belongs in <c>HandleKey</c>, not in a converter. The type
    ///      already distinguishes the phases, <c>KeyEventMapper</c> already
    ///      drops releases, and <c>ApprovalGateView</c> already gates on
    ///      {Press, Repeat} — a converter-side "fix" would be redundant with
    ///      the mapper and would still leave this method wrong.
    ///   2. The product cannot produce a release today, because nothing ever
    ///      sends the kitty event-type flag. That is the reachability verdict
    ///      #784 asked for, and it is why this is a contract rather than a
    ///      crash report. The assertion is on the DEFINITION, not on a scan:
    ///      the flag constant stays, the method stays, and if a key source
    ///      ever starts pushing flags this guard is the thing that makes the
    ///      gate mandatory rather than optional.
    /// </summary>
    [Test]
    public async Task The_Release_Gate_Belongs_In_HandleKey_Not_In_A_Converter()
    {
        string dialog = ReadRepoFile("src", "Harbor.Tui.CellForge", "Chat", "Widgets", "DialogOverlay.cs");
        string mapper = ReadRepoFile("src", "Harbor.Ui.Framework.Rendering", "Input", "KeyEventMapper.cs");
        string gate = ReadRepoFile("src", "Harbor.Ui.Framework.Rendering", "Widgets", "ApprovalGateView.cs");

        // The vocabulary and an existing consumer of it are both still there.
        await Assert.That(mapper).Contains("KeyEventType.Release")
            .Because("the type distinguishes the phase; the loss is not at the boundary");
        await Assert.That(gate).Contains("KeyEventType.Press")
            .Because("ApprovalGateView already gates on {Press, Repeat} — the precedent this fix copies");

        // The gate must live in the kitty overload's own body. Scoping the
        // search to that method (rather than the file) is what stops the
        // assertion from being satisfied by a mention in a doc comment or by
        // a gate added to the WRONG overload.
        string kittyBody = Between(
            dialog,
            "public bool HandleKey(in KeyEvent key)",
            "private bool HandlePromptKey");

        await Assert.That(kittyBody).Contains("KeyEventType.Release")
            .Because("HandleKey(in KeyEvent) must reject releases, or a release applies its press twice");

        // The legacy overload must NOT grow one: ConsoleKeyInfo carries no
        // phase (the BCL only ever reports presses), so a gate there would be
        // a claim about a field that does not exist.
        string legacyBody = Between(
            dialog,
            "public bool HandleKey(ConsoleKeyInfo key)",
            "public bool HandleKey(in KeyEvent key)");

        await Assert.That(legacyBody).DoesNotContain("KeyEventType")
            .Because("ConsoleKeyInfo has no event type; that overload cannot lose the distinction");
    }

    /// <summary>The source between two markers, or the whole text when absent.</summary>
    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0)
        {
            return text;
        }

        from += start.Length;
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? text[from..] : text[from..to];
    }

    private static string ReadRepoFile(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(segments).ToArray()));

    /// <summary>Walks up from the test binaries to the repo root (<c>Harbor.slnx</c>).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbor.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("[#784] repository root not found; see ReadRepoFile.");
    }
}
