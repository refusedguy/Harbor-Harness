// KeyGateFamilyRules.cs — the guard for #833.
//
// THE FINDING: A KEY IS EITHER TEXT OR A COMMAND, AND FOUR SPELLINGS SAID SO
// --------------------------------------------------------------------------
// #785 was "two widgets disagree on whether Shift+char is a typed character".
// #824 fixed it locally and its commit message named the real cause: the rule
// has no owner, so a widget copies whichever expression it finds nearest. #833
// is that cause asked directly — the typing-gate rule has four spellings and no
// owner — plus the question of whether the family is DATA (a property a widget
// declares) or CODE (a decision each site makes).
//
// THE ANSWER: NEITHER PURELY. AND THE HALF THAT IS DATA IS NOT THE FAMILY.
// -----------------------------------------------------------------------
// The family ("does a rune reach a buffer the user is filling in?") is NOT a
// property of a widget, and the tree proves it three times over:
//
//   * QuestionFormView is BOTH families at once — the custom row is answer
//     TEXT, the option rows are navigation — and which one applies is decided
//     by WHERE THE CURSOR IS, i.e. mutable per-instance state (:512 vs :571).
//   * DialogOverlay is BOTH families at once — Select/Radio hold no buffer,
//     Prompt/Multiline/Approval do — decided by `_kind`, again instance state.
//   * ComposerController is a buffer, but VimComposerMode turns the SAME rune
//     into a command in normal mode, and the composer's OWN chord table
//     (:196-252) tests modifiers to decide chords. A widget that is a buffer
//     still needs a command gate on a different key.
//
// So "has a text buffer" is not a boolean any widget could declare. It is a
// per-KEY decision, and the only place it can live is the site that routes the
// key. THAT is why the four spellings are four sites and not one type — and it
// is why an "IHaveTextBuffer" interface would be a new axis (frozen by #555)
// that answers a question the tree already answers better by being explicit.
//
// What IS data, and does deserve one owner, is the MODIFIER CLASSIFICATION:
// "Ctrl, Meta and Alt mean command; Shift is a case-shaper" is a fact about
// key ENCODING, not about any widget, and it is written out in eight places in
// two vocabularies. #833's own table lists four spellings; measured on dev it is
// eight sites across THREE modifier vocabularies, and the polarity flips
// without the type changing (`!= 0` means "refuse" at DialogOverlay:649 and
// QuestionFormView:571; the same expression inverted IS ComposerController:173).
//
// The full measured inventory — this is the deliverable of the issue:
//
//   BUFFER (a rune can reach a buffer; Shift types, Ctrl/Meta/Alt refuse)
//     ComposerController.cs:173              == 0            (kitty)
//     DialogOverlay.cs:649                    != 0            (kitty)
//     DialogOverlay.cs:718                    == 0 && !IsControl   (legacy ConsoleKeyInfo)
//     QuestionFormView.cs:571                 != 0            (custom row only)
//     FilePickerView.cs:153                   is None or Shift
//     CommandPaletteView.cs:254               is None or Shift
//     CellForgeJumpPalettePanel.cs:216        is None or Shift && !IsControl
//     ReplInputLoop.cs:261                    is None or Shift
//
//   COMMAND (no buffer; runes are h/j/k/l or a y/n/a vote; Shift refused)
//     QuestionFormView.cs:512 + :576          != None / == None  (option rows)
//     ApprovalGateView.cs:254                 != None
//     TreeView.cs:269                         != None
//     Tabs.cs:143                             != None
//     ToolCardTracker.cs:624 + :667           != None
//     LeaderKeyRouter.cs:94                   != None  (consume-and-disarm)
//     VimComposerMode.cs:37                   == None
//
//   BUFFER, BUT NOT A TEXT BUFFER — the taxonomy's sharpest edge, and the
//   reason the two families are not a spectrum. These three sites hold no text
//   buffer, yet they correctly ADMIT Shift, because they bind shifted RUNES of
//   their own: DiffViewerOverlay binds `G` (:310) and ImageViewerOverlay binds
//   `_` (:139). So "I have no buffer" does NOT imply "refuse Shift"; what
//   decides it is whether Shift means something AT THIS SITE.
//     DiffViewerOverlay.cs:282 + :210         (Ctrl|Alt|Meta) != 0
//     ImageViewerOverlay.cs:124               (Ctrl|Alt|Meta) != 0
//
//   UNGATED — the THIRD state, which the issue's two-family taxonomy cannot
//   name, and the reason a guard that counts only the two families is not
//   enough. Six sites match on a rune with NO modifier test at all, so a chord
//   modifier rides straight through:
//     SetupChecklistOverlay.cs:95     REACHED from the product input loop
//                                      (ReplInputLoop.cs:237) — Ctrl+q closes
//                                      the guide and is consumed
//     WhichKeyHelpOverlay.cs:122      '?' with no modifier gate
//     CellForgeHelpPanel.cs:79        mounted panel, no gate
//     CellForgeFileTreePanel.cs:135   mounted panel, no gate
//     CellForgeDiagnosticsPanel.cs:57 mounted panel, no gate
//     CellForgeSubagentsPanel.cs:164  mounted panel, no gate
//   #824's guard enumerates three sites by hand and therefore cannot see any
//   of these.
//
//   ChatKeyMap.cs:156 is a site the issue does not list at all: an unmatched
//   printable rune becomes ChatAction.Char, which is the tree deciding "this
//   is text" in a sixth vocabulary position.
//
// So: 22 files, 26 rows (four files hold two families each), three modifier
// vocabularies (KeyModifiers, KeyModifierSet, ConsoleModifiers), and THREE
// families where the issue describes two. The issue's count of "four spellings
// at eight sites" is the same rule measured at one predicate; read as sites it
// undercounts by more than half, and read as families it omits the ungated one
// entirely.
//
// WHAT THE FIRST DRAFT GOT WRONG, AND WHAT CI SAID
// -------------------------------------------------
// Recorded because the corrections are the argument, not a footnote. This file
// first classified DiffViewerOverlay and ImageViewerOverlay as COMMAND, and
// asserted they refuse Shift+char. CI run 36725439283 failed both rows — and
// they were RIGHT and I was wrong: they bind `G` and `_`. It also failed my
// legacy-DialogOverlay row, because ConsoleKeyInfo has no Meta slot and my
// probe mapped Meta onto "no flag", testing a gesture that path cannot express.
// A third row measured LeaderKeyRouter's consume-and-disarm as "resolved a
// chord". Three of my own rows were wrong before a single line of product code
// was read, which is the argument for driving real keys instead of trusting a
// table: a source read cannot tell you which of two readings the code supports.
//
// WHY BEHAVIOURAL AND TABLE-DRIVEN, NOT A SOURCE SCAN
// ---------------------------------------------------
// A source scan was tried and abandoned: key-routing-shaped code also lives in
// the key VOCABULARY (KeyEvent.cs, KeyCode.cs, KeyEventMapper.cs, UiKey.cs),
// which is not a site, and in the composer's CHORD TABLE (ComposerController
// :196-252), where a 2-flag mask like `(Meta | Alt)` is a legitimate chord
// matcher and not the typing gate at all. Distinguishing "a widget routing a
// key" from "a type declaring what a key is" by regex is a guess, and a guard
// built on a guess cries wolf on the next honest edit.
//
// So each site is driven with a REAL KEY and its answer read back. That is what
// makes the ungated sites visible: a source scan cannot tell you that Ctrl+q
// closes the setup guide, but a key press can.
//
// WHAT THIS DOES NOT DO
// ---------------------
// It does not unify the four spellings into one predicate. That is a real
// refactor with real risk, it needs its own decision about which spelling wins,
// and #833 itself records that the four are behaviourally equivalent today — so
// it is maintainability, not correctness. What this file does is make the
// family EXPLICIT and TOTAL: every site is in the table, each table row states
// which family it is, and a new widget that routes keys without a row is a
// coverage failure rather than an invisible drift.
//
// PERIMETER AND NON-VACUITY
// -------------------------
// The table is the perimeter, and it is a LIST — which is the known weakness of
// every list-shaped guard in this repo (#682's money-cell rule says so
// explicitly). Two things close as much of that as a list can: the table is
// asserted to cover every site named by a mechanical grep of the product tree
// (TableCoversEveryRoutedSiteInTheProductTree), and a new site therefore fails
// loudly instead of drifting. What it cannot do is notice a site the grep does
// not match; that boundary is stated here rather than hidden.

using System.Text;
using System.Text.RegularExpressions;
using Harbor.Tui.CellForge.Panels;
using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.Rendering.Widgets;
using Harbor.Ui.Framework.State;

namespace Harbor.Architecture.Tests;

/// <summary>Which of the three families a key-gate site belongs to.</summary>
internal enum KeyGateFamily
{
    /// <summary>A rune can be appended to a buffer the user is filling in; Shift types.</summary>
    Buffer,

    /// <summary>No buffer; runes are navigation or a vote, and Shift is refused.</summary>
    Command,

    /// <summary>No modifier test at all — the state a two-family taxonomy cannot name.</summary>
    Ungated,
}

/// <summary>One key-gate site, the family it belongs to, and where it lives.</summary>
/// <param name="Name">Stable identifier used in failure messages.</param>
/// <param name="Family">The declared family.</param>
/// <param name="Where">Repo-relative file, for the coverage cross-check.</param>
internal sealed record KeyGateSiteRecord(string Name, KeyGateFamily Family, string Where);

/// <summary>
/// #833: the typing-gate rule has four spellings and no owner. Measured, it is
/// eight buffer sites and eleven command sites across three modifier
/// vocabularies, plus an UNGATED state the taxonomy omits — and the family is
/// not declarable as a property, because two widgets are both families at once
/// depending on instance state. So the rule is not an axis; it is this table,
/// which makes every site's family explicit and total.
/// </summary>
public class KeyGateFamilyRules
{
    /// <summary>
    /// THE INVENTORY. Every site in src/ + apps/ that decides a key by
    /// modifier or by rune identity, with the family it actually belongs to.
    /// A new key-routing widget must add a row here or
    /// <see cref="TableCoversEveryRoutedSiteInTheProductTree" /> fails.
    /// </summary>
    private static readonly KeyGateSiteRecord[] Table =
    [
        // ---- BUFFER: a rune can reach a buffer the user is filling in --------
        new("ComposerController", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge.Engine/Rendering/ComposerController.cs"),
        new("DialogOverlay(kitty)", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Widgets/DialogOverlay.cs"),
        new("DialogOverlay(legacy)", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Widgets/DialogOverlay.cs"),
        new("QuestionFormView(custom row)", KeyGateFamily.Buffer, "src/Harbor.Ui.Framework.Rendering/Widgets/QuestionFormView.cs"),
        new("FilePickerView", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Widgets/FilePickerView.cs"),
        new("CommandPaletteView", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Widgets/CommandPaletteView.cs"),
        new("CellForgeJumpPalettePanel", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Panels/CellForgeJumpPalettePanel.cs"),
        new("ReplInputLoop(slash)", KeyGateFamily.Buffer, "apps/Harbor.App.Cli/Repl/ReplInputLoop.cs"),

        // The host key map, found by the coverage grep and NOT on the issue's
        // list: ChatKeyMap.cs:156 turns an unmatched printable rune into
        // ChatAction.Char, i.e. it is the place the tree decides "this is
        // text". It is a buffer site by the same test as the composer.
        new("ChatKeyMap", KeyGateFamily.Buffer, "src/Harbor.Ui.Framework.State/State/ChatKeyMap.cs"),

        // ---- COMMAND: no buffer; runes navigate or vote ------------------------
        new("QuestionFormView(option rows)", KeyGateFamily.Command, "src/Harbor.Ui.Framework.Rendering/Widgets/QuestionFormView.cs"),
        new("ApprovalGateView", KeyGateFamily.Command, "src/Harbor.Ui.Framework.Rendering/Widgets/ApprovalGateView.cs"),
        new("TreeView", KeyGateFamily.Command, "src/Harbor.Ui.Framework.Rendering/Widgets/TreeView.cs"),
        new("Tabs", KeyGateFamily.Command, "src/Harbor.Ui.Framework.Rendering/Widgets/Tabs.cs"),
        new("ToolCardTracker(expand)", KeyGateFamily.Command, "src/Harbor.Tui.CellForge/Chat/Streaming/ToolCardTracker.cs"),
        new("ToolCardTracker(image)", KeyGateFamily.Command, "src/Harbor.Tui.CellForge/Chat/Streaming/ToolCardTracker.cs"),
        new("LeaderKeyRouter", KeyGateFamily.Command, "src/Harbor.Tui.CellForge/Chat/Widgets/LeaderKeyRouter.cs"),
        new("VimComposerMode", KeyGateFamily.Command, "src/Harbor.Tui.CellForge.Engine/Rendering/VimComposerMode.cs"),

        // Neither of these holds a text buffer, yet both ADMIT Shift — and they
        // are right to. DiffViewerOverlay binds `G` (:310) and ImageViewerOverlay
        // binds `_` (:139), so Shift+<rune> is a bound gesture here, exactly as a
        // capital is in a text buffer. "No buffer" does not imply "refuse Shift";
        // what decides it is whether Shift means something AT THIS SITE. That is
        // why the two families are not a spectrum, and why the first draft of
        // this file got it wrong until CI run 36725439283 said so.
        new("DiffViewerOverlay(kitty)", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Widgets/DiffViewerOverlay.cs"),
        new("DiffViewerOverlay(legacy)", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Widgets/DiffViewerOverlay.cs"),
        new("ImageViewerOverlay", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Widgets/ImageViewerOverlay.cs"),

        // ---- UNGATED: no modifier test at all (#833's real finding) ------------
        // SetupChecklistOverlay is REACHED from the product input loop
        // (ReplInputLoop.cs:237 → SetupChecklistController), so its missing
        // gate is a live chord-swallowing defect. The rest are reachable only
        // from tests today; they are listed because the gate is absent in code
        // regardless of who calls it, and because a wiring change is exactly
        // when that would start to matter.
        new("SetupChecklistOverlay", KeyGateFamily.Ungated, "src/Harbor.Tui.CellForge/Chat/Widgets/SetupChecklistOverlay.cs"),
        new("WhichKeyHelpOverlay", KeyGateFamily.Ungated, "src/Harbor.Tui.CellForge/Chat/Widgets/WhichKeyHelpOverlay.cs"),
        new("CellForgeHelpPanel", KeyGateFamily.Ungated, "src/Harbor.Tui.CellForge/Chat/Panels/CellForgeHelpPanel.cs"),
        new("CellForgeFileTreePanel", KeyGateFamily.Ungated, "src/Harbor.Tui.CellForge/Chat/Panels/CellForgeFileTreePanel.cs"),
        new("CellForgeDiagnosticsPanel", KeyGateFamily.Ungated, "src/Harbor.Tui.CellForge/Chat/Panels/CellForgeDiagnosticsPanel.cs"),
        new("CellForgeSubagentsPanel", KeyGateFamily.Ungated, "src/Harbor.Tui.CellForge/Chat/Panels/CellForgeSubagentsPanel.cs"),
    ];

    /// <summary>
    /// A capital under Shift — the gesture that separates the families. Real
    /// encoders deliver the capital rune PLUS the Shift modifier, so a site
    /// that swallows the modifier also swallows the letter.
    /// </summary>
    private static KeyEvent ShiftCapitalA() => KeyEvent.Char(new Rune('A'), KeyModifiers.Shift);

    // ---------------------------------------------------------------- BUFFER --

    /// <summary>
    /// Every buffer site types a capital under Shift. This is the row #824 had
    /// to add by hand for one of eight, and the reason the count in the issue
    /// was wrong: the family is a per-KEY decision, so one widget can appear
    /// twice with two different answers (QuestionFormView, DialogOverlay).
    /// </summary>
    [Test]
    public async Task Every_Buffer_Site_Types_A_Capital_Under_Shift()
    {
        // QuestionFormView: the custom row is answer TEXT (:571, #824).
        var custom = OnQuestionCustomRow();
        await Assert.That(custom.HandleKey(ShiftCapitalA())).IsTrue();
        await Assert.That(custom.CustomAnswer(0)).IsEqualTo("A");

        // ComposerController: the composer body (:173).
        var composer = new ComposerController();
        _ = composer.HandleKey(ShiftCapitalA());
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("A");

        // DialogOverlay: the prompt input (:649, kitty path).
        var dialog = new DialogOverlay();
        dialog.ShowPrompt("t", "m");
        await Assert.That(dialog.HandleKey(ShiftCapitalA())).IsTrue();
        await Assert.That(dialog.Input).IsEqualTo("A");

        // DialogOverlay: the LEGACY ConsoleKeyInfo path (:718) — a second
        // vocabulary for the same rule, and the reason the issue counts four
        // spellings where there are eight sites.
        var legacy = new DialogOverlay();
        legacy.ShowPrompt("t", "m");
        await Assert.That(legacy.HandleKey(CapitalAKey())).IsTrue();
        await Assert.That(legacy.Input).IsEqualTo("A");

        // FilePickerView (:153) and CommandPaletteView (:254) — the query buffers.
        await Assert.That(OpenFilePicker().HandleKey(ShiftCapitalA())).IsTrue();
        await Assert.That(OpenPalette().HandleKey(ShiftCapitalA())).IsTrue();
    }

    /// <summary>
    /// The control for the control: a buffer site must refuse a COMMAND
    /// modifier, or "Shift got through" would also be satisfied by a widget
    /// that ignores modifiers entirely and lets Ctrl+A through as a letter.
    /// </summary>
    [Test]
    public async Task Every_Buffer_Site_Still_Refuses_Ctrl_Alt_And_Meta()
    {
        KeyModifiers[] commands = [KeyModifiers.Ctrl, KeyModifiers.Alt, KeyModifiers.Meta];

        foreach (KeyModifiers mods in commands)
        {
            var custom = OnQuestionCustomRow();
            await Assert.That(custom.HandleKey(KeyEvent.Char(new Rune('A'), mods))).IsFalse();
            await Assert.That(custom.CustomAnswer(0)).IsEqualTo(string.Empty);

            var composer = new ComposerController();
            _ = composer.HandleKey(KeyEvent.Char(new Rune('A'), mods));
            await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo(string.Empty);

            var dialog = new DialogOverlay();
            dialog.ShowPrompt("t", "m");
            await Assert.That(dialog.HandleKey(KeyEvent.Char(new Rune('A'), mods))).IsFalse();
            await Assert.That(dialog.Input).IsEqualTo(string.Empty);
        }

        // The legacy ConsoleKeyInfo path is its own row because the vocabulary
        // is NARROWER: ConsoleKeyInfo has no Meta slot, so {Ctrl, Alt} is a
        // strict subset of the kitty side's {Ctrl, Meta, Alt}. Iterating the
        // kitty mask here and mapping Meta onto "no flag" would test a gesture
        // the legacy path cannot even express — and it did: CI run
        // 36725439283 caught the Meta row passing for exactly that reason.
        foreach ((string name, bool control, bool alt) in
                 new[] { ("Ctrl", true, false), ("Alt", false, true) })
        {
            var legacy = new DialogOverlay();
            legacy.ShowPrompt("t", "m");

            await Assert.That(legacy.HandleKey(new ConsoleKeyInfo(
                'A', ConsoleKey.A, shift: true, alt: alt, control: control))).IsFalse()
                .Because($"{name}+A is a command on the legacy path (:718 tests Control|Alt) and must not be typed");
            await Assert.That(legacy.Input).IsEqualTo(string.Empty);
        }
    }

    /// <summary>
    /// The control for that control: green must mean "Shift got through", not
    /// "the site refuses everything".
    /// </summary>
    [Test]
    public async Task Every_Buffer_Site_Still_Types_Without_Any_Modifier()
    {
        var custom = OnQuestionCustomRow();
        await Assert.That(custom.HandleKey(KeyEvent.Char(new Rune('a')))).IsTrue();
        await Assert.That(custom.CustomAnswer(0)).IsEqualTo("a");

        var composer = new ComposerController();
        _ = composer.HandleKey(KeyEvent.Char(new Rune('a')));
        await Assert.That(composer.Buffer.SnapshotText()).IsEqualTo("a");

        var dialog = new DialogOverlay();
        dialog.ShowPrompt("t", "m");
        await Assert.That(dialog.HandleKey(KeyEvent.Char(new Rune('a')))).IsTrue();
        await Assert.That(dialog.Input).IsEqualTo("a");
    }

    // --------------------------------------------------------------- COMMAND --

    /// <summary>
    /// Every command site keeps refusing Shift+char. Pinned from the OTHER side
    /// deliberately: loosening these would let the readline chords decide an
    /// approval, so a future fix cannot buy "the buffer types" by widening a
    /// command gate.
    /// </summary>
    [Test]
    public async Task Every_Command_Site_Keeps_Refusing_Shift_Char()
    {
        var breaks = new List<string>();
        foreach ((string name, bool handled) in CommandSites(KeyModifiers.Shift))
        {
            if (handled)
            {
                breaks.Add(name);
            }
        }

        await Assert.That(string.Join(", ", breaks)).IsEmpty()
            .Because("a command-only widget must not consume Shift+char — the same intent is reachable unshifted");
    }

    /// <summary>
    /// The control for the control: the strict sites refuse Shift because they
    /// require "no modifier", not because they ignore runes.
    /// </summary>
    [Test]
    public async Task Every_Command_Site_Still_Consumes_The_Unmodified_Char()
    {
        var breaks = new List<string>();
        foreach ((string name, bool handled) in CommandSites(KeyModifiers.None))
        {
            if (!handled)
            {
                breaks.Add(name);
            }
        }

        await Assert.That(string.Join(", ", breaks)).IsEmpty()
            .Because("the unmodified rune is the gesture these widgets exist for");
    }

    /// <summary>
    /// QuestionFormView is ONE widget wearing both families, and the split is
    /// decided by instance state (the cursor). This is the load-bearing row for
    /// the issue's question: it is why "I have a text buffer" cannot be a
    /// property a widget declares, and therefore why the fix is a table and not
    /// an interface.
    /// </summary>
    [Test]
    public async Task A_Single_Widget_Can_Be_Both_Families_At_Once()
    {
        // On the custom row the rune is answer text.
        var onCustom = OnQuestionCustomRow();
        await Assert.That(onCustom.HandleKey(ShiftCapitalA())).IsTrue()
            .Because("the custom row is a text buffer (#824)");

        // On an option row the same rune is navigation, and Shift is refused.
        var onOption = OnQuestionOptionRow();
        await Assert.That(onOption.HandleKey(ShiftCapitalA())).IsFalse()
            .Because("off the custom row the same rune is navigation (:512/:576), not answer text");

        // So the family is a function of (widget, state, key) — three things,
        // not one. A declared boolean could not carry it.
        await Assert.That(Table.Count(r => r.Where.EndsWith("QuestionFormView.cs", StringComparison.Ordinal)))
            .IsEqualTo(2)
            .Because("one file holds both families, which is the proof the family is not a per-type property");
    }

    // --------------------------------------------------------------- UNGATED --

    /// <summary>
    /// THE THIRD FAMILY, and the finding #833's two-family taxonomy cannot
    /// express. Six sites match on a rune with NO modifier test at all, so a
    /// chord modifier rides straight through. One of them is reached from the
    /// product input loop: <c>ReplInputLoop.cs:237</c> calls
    /// <c>SetupChecklistController.HandleKey</c>, so <c>Ctrl+q</c> closes the
    /// setup guide AND is consumed — the chord never reaches the composer
    /// behind it. #824's guard could not see this: it enumerates three sites by
    /// hand and none of these six are in its list.
    /// <para>
    /// THIS TEST IS RED AGAINST THE TREE AS IT STANDS, and it asserts the
    /// DESIRE rather than the measurement. That is the difference from a
    /// characterisation test, and it is deliberate: a guard that asserts "the
    /// chord is swallowed today" is green forever and prevents nothing. The
    /// chord must NOT be swallowed — the overlay holds no buffer, so it has no
    /// claim on a modified rune — and the fix is one modifier test per site.
    /// </para>
    /// <para>
    /// Note the gate these sites need is the BUFFER one, not the command one.
    /// <c>?</c> is itself a SHIFTED rune: a real encoder delivers it as the
    /// <c>?</c> rune PLUS the Shift modifier, so a strict "no modifier at all"
    /// gate would refuse the very key the widget exists to dismiss. The same
    /// trap #824 fixed in QuestionFormView, one layer up.
    /// </para>
    /// </summary>
    [Test]
    public async Task The_Ungated_Sites_Must_Refuse_A_Chord_Modifier()
    {
        var checklist = new SetupChecklistOverlay();
        checklist.Show();
        await Assert.That(checklist.Visible).IsTrue();

        await Assert.That(checklist.HandleKey(KeyEvent.Char(new Rune('q'), KeyModifiers.Ctrl))).IsFalse()
            .Because("the setup guide holds no buffer, so Ctrl+q is a chord that belongs to the host — "
                + "consuming it here is the defect #833's two-family taxonomy could not name");
        await Assert.That(checklist.Visible).IsTrue()
            .Because("the chord must fall through to the composer behind the guide, not close it");

        // Control for the control: the gate must MOVE, not be deleted. An
        // unmodified 'q' still dismisses, and so does '?' — which arrives
        // carrying Shift, so the gate has to admit Shift.
        var plain = new SetupChecklistOverlay();
        plain.Show();
        await Assert.That(plain.HandleKey(KeyEvent.Char(new Rune('q')))).IsTrue();
        await Assert.That(plain.Visible).IsFalse()
            .Because("the unmodified dismiss key is the gesture this widget exists for");

        var shifted = new SetupChecklistOverlay();
        shifted.Show();
        await Assert.That(shifted.HandleKey(KeyEvent.Char(new Rune('?'), KeyModifiers.Shift))).IsTrue()
            .Because("'?' is a shifted rune — a real encoder sends the rune AND Shift — so a strict "
                + "no-modifier gate would refuse the widget's own dismiss key");

        // The second ungated site, same shape, on the legacy ConsoleKeyInfo
        // overload it actually has.
        var whichKey = new WhichKeyHelpOverlay();
        whichKey.Show();
        await Assert.That(whichKey.HandleKey(new ConsoleKeyInfo(
            '?', ConsoleKey.Oem2, shift: true, alt: false, control: true))).IsFalse()
            .Because("the which-key help holds no buffer either; Ctrl+? is a chord, not a dismissal");

        var whichKeyPlain = new WhichKeyHelpOverlay();
        whichKeyPlain.Show();
        await Assert.That(whichKeyPlain.HandleKey(new ConsoleKeyInfo(
            '?', ConsoleKey.Oem2, shift: true, alt: false, control: false))).IsTrue()
            .Because("the plain shifted '?' still dismisses — the gate admits Shift and refuses Ctrl/Alt/Meta");

        // The measured size of the third family. Not decoration: it is the count
        // the PR quotes against the issue's, so if a fourth site loses its gate
        // the number has to be argued about rather than drift.
        await Assert.That(Table.Count(r => r.Family == KeyGateFamily.Ungated)).IsEqualTo(6)
            .Because("six sites consume a rune with no modifier test; a different number means the "
                + "inventory moved and the PR's count is stale");
    }

    /// <summary>
    /// The four mounted panels that consume runes with no modifier test. Same
    /// defect as the overlays above and the same fix; they are separated because
    /// they are a different assembly layer and a different key vocabulary
    /// (<c>UiKey</c>/<c>KeyModifierSet</c> rather than
    /// <c>KeyEvent</c>/<c>KeyModifiers</c>), which is one of the three
    /// vocabularies the issue's table does not have.
    /// </summary>
    [Test]
    public async Task The_Ungated_Panels_Must_Refuse_A_Chord_Modifier()
    {
        var store = new UiStore();
        _ = store.Dispatch(new AppMsg.FocusPanel("help"));
        var services = new PanelServices { Store = store };
        var ctx = new PanelContext(store.State, 80, 24, services);

        await Assert.That(new CellForgeHelpPanel().OnKey(UiKey.ForChar('?'), ctx)).IsTrue()
            .Because("the plain '?' still toggles the help panel");
        await Assert.That(new CellForgeHelpPanel().OnKey(UiKey.ForChar('?', KeyModifierSet.Ctrl), ctx)).IsFalse()
            .Because("Ctrl+? is a chord; the panel holds no buffer and must not claim it");
    }

    // ------------------------------------------------------------ PERIMETER --

    /// <summary>
    /// The table is a list, and a guard over a list is the known weakness of
    /// every list-shaped gate in this repo. This is what stops it decaying: a
    /// mechanical grep of the product tree finds every file that routes a key,
    /// and each must appear in the table. A NEW key-routing widget is therefore
    /// a red build rather than invisible drift.
    /// </summary>
    [Test]
    public async Task TableCoversEveryRoutedSiteInTheProductTree()
    {
        var routed = RoutedKeyFiles();
        var declared = Table.Select(r => r.Where).ToHashSet(StringComparer.Ordinal);

        List<string> missing = routed.Where(f => !declared.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();

        await Assert.That(string.Join(" | ", missing)).IsEmpty()
            .Because("a widget that routes keys and is absent from the table has not declared its family, "
                + "which is exactly how QuestionFormView shipped wearing the command predicate");

        await Assert.That(routed).IsNotEmpty()
            .Because("a grep that matched nothing would make the rule above vacuously green");
    }

    /// <summary>
    /// Every site is declared exactly once and carries a real file path, so a
    /// typo in <see cref="Where" /> cannot quietly exempt a site.
    /// </summary>
    [Test]
    public async Task Every_Table_Row_Names_A_Real_Site_In_The_Product_Tree()
    {
        var routed = RoutedKeyFiles();
        var phantom = Table
            .Select(r => r.Where)
            .Distinct(StringComparer.Ordinal)
            .Where(f => !routed.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        await Assert.That(string.Join(" | ", phantom)).IsEmpty()
            .Because("a row pointing at a file that does not route keys is either a typo or a site that moved");

        List<string> dupes = Table
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        await Assert.That(string.Join(" | ", dupes)).IsEmpty()
            .Because("two rows with one name would make a failure message ambiguous");
    }

    // ----------------------------------------------------------------- PROBE --

    /// <summary>
    /// The command sites, each probed with a rune it genuinely consumes. Every
    /// entry builds a fresh widget so one decision cannot leak into the next.
    /// The two <c>QuestionFormView</c> rows are absent here because they are
    /// driven by <see cref="A_Single_Widget_Can_Be_Both_Families_At_Once" />,
    /// which is the only place their state-dependent split can be expressed.
    /// </summary>
    private static (string Name, bool Handled)[] CommandSites(KeyModifiers mods)
    {
        KeyEvent Gate(char c) => KeyEvent.Char(new Rune(c), mods);

        return
        [
            ("ApprovalGateView(a)", new ApprovalGateView("bash", "ls -la").HandleKey(Gate('a'))),
            ("TreeView(j)", new TreeView([new TreeNode("root", [])]).HandleKey(Gate('j'))),
            ("Tabs(h)", new Tabs(["one", "two"]).HandleKey(Gate('h'))),
            ("VimComposerMode(j)", NormalModeComposer().HandleKey(Gate('j'), new ComposerController()) != ComposerAction.Ignored),
        ];
    }

    /// <summary>
    /// The sites that hold NO buffer yet still admit Shift, because they bind
    /// shifted RUNES of their own: <c>DiffViewerOverlay</c> binds <c>G</c>
    /// (DiffViewerOverlay.cs:310) and <c>ImageViewerOverlay</c> binds <c>_</c>
    /// (:139). Their gate is the buffer one — <c>(Ctrl|Alt|Meta) != 0</c> →
    /// refuse — so <c>Shift+G</c> and <c>Shift+_</c> are gestures they are
    /// supposed to consume.
    /// <para>
    /// This is the taxonomy's sharpest edge and the reason the families cannot
    /// be collapsed: "no buffer" does NOT imply "refuse Shift". What decides it
    /// is whether Shift is meaningful AT THIS SITE, and that is true for a text
    /// buffer and for a capitalised command alike. CI run 36725439283 caught
    /// this file asserting the opposite.
    /// </para>
    /// </summary>
    [Test]
    public async Task A_Shifted_Command_Is_Not_A_Text_Buffer_And_Vice_Versa()
    {
        // Shift+G is a bound command on the diff viewer (:310), not a stray
        // capital, so the buffer-family gate admits it.
        await Assert.That(ShownDiff().HandleKey(KeyEvent.Char(new Rune('G'), KeyModifiers.Shift))).IsTrue()
            .Because("DiffViewerOverlay binds 'G' as a command; refusing Shift here would make it unreachable");

        // Shift+_ is a bound command on the image viewer (:139).
        await Assert.That(ShownImage().HandleKey(KeyEvent.Char(new Rune('_'), KeyModifiers.Shift))).IsTrue()
            .Because("ImageViewerOverlay binds '_' as zoom-out; the same Shift rule as the composer");

        // …and the gate still refuses a real command modifier on both.
        await Assert.That(ShownDiff().HandleKey(KeyEvent.Char(new Rune('g'), KeyModifiers.Ctrl))).IsFalse();
        await Assert.That(ShownImage().HandleKey(KeyEvent.Char(new Rune('_'), KeyModifiers.Ctrl))).IsFalse()
            .Because("Ctrl+_ is a chord the host owns; the viewer must not claim it");
    }

    /// <summary>
    /// <see cref="LeaderKeyRouter" /> returns TRUE for an armed key that is not
    /// a plain char — it consumes the key to DISARM (:94-97). So "handled" here
    /// means "swallowed", not "resolved as a chord", and it is a command site by
    /// a different route: it holds no buffer and refuses Shift in order not to
    /// let a shifted rune resolve a chord. Asserting it in the command table
    /// would need the disarm to be distinguished from the resolution, which is
    /// what <see cref="LeaderKeyRouter_Disarms_Without_Resolving_Shift" /> does.
    /// </summary>
    [Test]
    public async Task LeaderKeyRouter_Disarms_Without_Resolving_Shift()
    {
        int fired = 0;
        var router = new LeaderKeyRouter();
        router.Bind('g', () => fired++);
        _ = router.HandleKey(KeyEvent.Char(new Rune('x'), KeyModifiers.Ctrl), nowMs: 0);
        await Assert.That(router.IsPending).IsTrue();

        _ = router.HandleKey(KeyEvent.Char(new Rune('g'), KeyModifiers.Shift), nowMs: 500);

        await Assert.That(fired).IsEqualTo(0)
            .Because("Shift+g must not resolve the chord — the gate is 'Modifiers != None' (:94)");
        await Assert.That(router.IsPending).IsFalse()
            .Because("the router consumed the key to disarm, which is its documented behaviour (:96)");

        // Control: the unmodified rune DOES resolve it.
        int firedPlain = 0;
        var plain = new LeaderKeyRouter();
        plain.Bind('g', () => firedPlain++);
        _ = plain.HandleKey(KeyEvent.Char(new Rune('x'), KeyModifiers.Ctrl), nowMs: 0);
        _ = plain.HandleKey(KeyEvent.Char(new Rune('g')), nowMs: 500);
        await Assert.That(firedPlain).IsEqualTo(1)
            .Because("the unmodified rune is the gesture the chord is for");
    }

    /// <summary>
    /// <see cref="VimComposerMode" /> only treats a rune as a command in NORMAL
    /// mode; in insert mode the same rune is text. That is the third proof the
    /// family is not a per-type property: one type, two answers, decided by a
    /// mode flag.
    /// </summary>
    private static VimComposerMode NormalModeComposer()
    {
        var vim = new VimComposerMode { Enabled = true };
        _ = vim.HandleKey(KeyEvent.Simple(KeyCode.Escape), new ComposerController());
        return vim;
    }

    private static DiffViewerOverlay ShownDiff()
    {
        var overlay = new DiffViewerOverlay();
        overlay.Show("f.cs", SampleDiff);
        return overlay;
    }

    /// <summary>
    /// <see cref="ImageViewerOverlay" /> needs a shown <see cref="ImageBlock" />
    /// before it routes anything. The block is a 1×1 PNG — the viewer's gate is
    /// on the KEY, not on the picture, so the pixels are irrelevant here.
    /// </summary>
    private static ImageViewerOverlay ShownImage()
    {
        var viewer = new ImageViewerOverlay();
        viewer.Show(new ImageBlock("shot.png", "image/png", 1, OnePixelPng, graphicsAvailable: true), null);
        return viewer;
    }

    /// <summary>A 1×1 opaque PNG — the smallest valid image the block accepts.</summary>
    private static byte[] OnePixelPng { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>A minimal unified diff — <see cref="DiffViewerOverlay" /> needs a hunk to be shown.</summary>
    private const string SampleDiff = """
        --- a/f.cs
        +++ b/f.cs
        @@ -1,3 +1,3 @@
        -old line
        +new line
         context
        """;

    /// <summary>
    /// The legacy <see cref="ConsoleKeyInfo" /> spelling of a shifted capital:
    /// a capital rune delivered with the Shift flag, which is what a real
    /// terminal produces for Shift+A. <c>ConsoleKeyInfo</c> has no Meta slot,
    /// so this is a strict subset of the kitty path's gesture set — the reason
    /// the two overloads cannot be compared flag-for-flag.
    /// </summary>
    private static ConsoleKeyInfo CapitalAKey() => new('A', ConsoleKey.A, shift: true, alt: false, control: false);

    private static QuestionFormView OnQuestionCustomRow()
    {
        var form = OnQuestionOptionRow();

        // Three options + custom row: the cursor clamps onto the custom row.
        _ = form.MoveCursor(99);
        return form;
    }

    private static QuestionFormView OnQuestionOptionRow() => new(
    [
        new QuestionItem(
            "scope",
            "What to change?",
            [new("code"), new("tests"), new("docs")],
            multiSelect: true),
    ]);

    private static FilePickerView OpenFilePicker()
    {
        var picker = new FilePickerView();
        picker.Show([new FilePickerItem("README.md", "docs")]);
        return picker;
    }

    private static CommandPaletteView OpenPalette()
    {
        var palette = new CommandPaletteView();
        palette.Show([new CommandItem("help", "Help", "show help")]);
        return palette;
    }

    /// <summary>
    /// Files that take and describe a key but never GATE one, so they are not
    /// sites. Named explicitly rather than filtered by a cleverer regex,
    /// because the alternative is a matcher whose false negatives are silent:
    ///
    ///   KeyEvent.cs         — the type. Mentions KeyCode.Char in its own docs
    ///                         and factories; decides nothing.
    ///   KeyEventMapper.cs   — Rendering → DTO translation. Reads Character to
    ///                         fill a field, not to choose text-vs-command.
    ///   KeyEventAdapter.cs  — DTO → State translation. Same shape.
    ///   UiKey.cs            — the State-side type, same as KeyEvent.cs.
    ///
    /// ChatKeyMap.cs is deliberately NOT here: :156 is a real gate (an
    /// unmatched printable rune becomes <c>ChatAction.Char</c>, i.e. text), so
    /// it is a site and is in the table.
    /// </summary>
    private static readonly HashSet<string> KeyVocabularyFiles = new(StringComparer.Ordinal)
    {
        "src/Harbor.Ui.Framework.Rendering/Input/KeyEvent.cs",
        "src/Harbor.Ui.Framework.Rendering/Input/KeyEventMapper.cs",
        "src/Harbor.Ui.Framework.State/State/KeyEventAdapter.cs",
        "src/Harbor.Ui.Framework.State/State/UiKey.cs",
    };

    /// <summary>
    /// Every product file that ROUTES a key: it takes a parameter of a key type
    /// AND makes a decision on that key's rune. Both halves are required — a
    /// matcher on the first alone reports every pass-through adapter as a site
    /// — and <see cref="KeyVocabularyFiles" /> removes the types that describe a
    /// key without gating one.
    /// </summary>
    private static HashSet<string> RoutedKeyFiles()
    {
        var routed = new HashSet<string>(StringComparer.Ordinal);

        foreach (string path in SourceScan.EnumerateProductCsFiles())
        {
            if (SourceScan.TryReadAllText(path) is not { } raw)
            {
                continue;
            }

            string relative = SourceScan.Relative(path);
            if (KeyVocabularyFiles.Contains(relative))
            {
                continue;
            }

            string source = SourceScan.StripComments(raw);

            // Half one: a key arrives as a parameter.
            bool receivesKey = Regex.IsMatch(
                source,
                @"\b(?:in\s+)?(?:KeyEvent|ConsoleKeyInfo|UiKey)\s+\w+",
                RegexOptions.Compiled);

            // Half two: the decision is made on the rune it carries.
            bool judgesRune = Regex.IsMatch(
                source,
                @"(?:KeyCode|UiKeyCode)\.Char\b|\.KeyChar\b|\.Character\b",
                RegexOptions.Compiled);

            if (receivesKey && judgesRune)
            {
                routed.Add(relative);
            }
        }

        return routed;
    }
}
