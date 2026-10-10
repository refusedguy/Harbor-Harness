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
// QuestionFormView:571; the same expression inverted IS ComposerController:194).
//
// The full measured inventory — this is the deliverable of the issue:
//
//   BUFFER (a rune can reach a buffer; Shift types, Ctrl/Meta/Alt refuse)
//     ComposerController.cs:194              == 0            (kitty)
//     DialogOverlay.cs:649                    != 0            (kitty)
//     DialogOverlay.cs:718                    == 0 && !IsControl   (legacy ConsoleKeyInfo)
//     QuestionFormView.cs:571                 != 0            (custom row only)
//     FilePickerView.cs:153                   is None or Shift
//     CommandPaletteView.cs:254               is None or Shift
//     CellForgeJumpPalettePanel.cs:216        is None or Shift && !IsControl
// check-doc-cites: record-drift CellForgeJumpPalettePanel.cs:216 now="ReseedLocked(ctx);" [#947: written over `}`; repair deferred to the owner's symbol-rename decision] -->
//     ReplInputLoop.cs:403                    is None or Shift
//
//   COMMAND (no buffer; runes are h/j/k/l or a y/n/a vote; Shift refused)
//     QuestionFormView.cs:512 + :576          != None / == None  (option rows)
//     ApprovalGateView.cs:254                 != None
//     TreeView.cs:269                         != None
//     Tabs.cs:143                             != None
//     ToolCardTracker.cs:624 + :667           != None
//     LeaderKeyRouter.cs:94                   != None  (consume-and-disarm)
//     VimComposerMode.cs:38                   == None  (NORMAL MODE ONLY)
//
//   BUFFER, BUT NOT A TEXT BUFFER — the taxonomy's sharpest edge, and the
//   reason the two families are not a spectrum. These sites hold no text
//   buffer, yet they correctly ADMIT Shift, because Shift means something at
//   THEM: DiffViewerOverlay binds `G` (:310), ImageViewerOverlay binds `_`
//   (:139), and VimComposerMode FALLS THROUGH to the composer for any key its
//   own command gate rejects (:37 → composer :173). So "I have no buffer" does
//   NOT imply "refuse Shift".
//     DiffViewerOverlay.cs:282 + :210         (Ctrl|Alt|Meta) != 0
//     ImageViewerOverlay.cs:124               (Ctrl|Alt|Meta) != 0
// check-doc-cites: record-drift ImageViewerOverlay.cs:124 now="case KeyCode.Left:" [#947: written over `if (key.Modifiers.IsCommandModifier())`; repair deferred to the owner's symbol-rename decision] -->
//     VimComposerMode.cs:38 (fall-through)    delegates to the composer
//
//   UNGATED — a third state the issue's two-family taxonomy cannot name, and the
//   reason a guard that counts only the two families is not enough. Six sites
//   matched a rune with NO modifier test at all, so a chord modifier rode
//   straight through. ONE WAS A LIVE DEFECT: ReplInputLoop.cs:379 calls
//   SetupChecklistController.HandleKey, so Ctrl+q closed the setup guide AND was
//   consumed — the chord never reached the composer behind it.
//     SetupChecklistOverlay.cs:95     product input loop — LIVE
//     WhichKeyHelpOverlay.cs:122      '?', legacy ConsoleKeyInfo
//     CellForgeHelpPanel.cs:79        mounted panel
//     CellForgeFileTreePanel.cs:135   mounted panel
//     CellForgeDiagnosticsPanel.cs:57 mounted panel
//     CellForgeSubagentsPanel.cs:164  mounted panel
// check-doc-cites: record-drift CellForgeSubagentsPanel.cs:164 now="" [#947: cited line is blank; repair deferred to the owner's decision] -->
//
//   THE SIXTH CORRECTION, AND IT OVERTURNED THE MODEL: those six are NOT a third
//   family. Measured, none of the six holds a text buffer, and five of the six
//   bind shifted runes of their own ('?', 'J', 'K', 'H', 'R') — so all six want
//   the BUFFER gate, and they are filed under it. What they lacked was not a
//   family but a DECISION. "Ungated" is the absence of one, and a guard can make
//   absence unreachable, so it is now a rule (zero rows may be Ungated) rather
//   than a family a site may declare. The first draft of this file got this
//   backwards — it filed them as a third family on the reasoning that a two-family
//   taxonomy has no name for them — and CI caught it, because the first draft's
//   rule demanded they refuse chords without asking what they bind.
//   #824's guard enumerates three sites by hand and therefore cannot see any
//   of these.
//
//   ChatKeyMap.cs:172 is a site the issue does not list at all: an unmatched
//   printable rune becomes ChatAction.Char, which is the tree deciding "this
//   is text" in a sixth vocabulary position.
//
// So: 23 files, 30 rows (six files hold two or more rows each), three modifier
// vocabularies (KeyModifiers, KeyModifierSet, ConsoleModifiers), and — after
// measuring the six ungated sites — TWO families, with "ungated" demoted from a
// family to the absence of a decision. The issue's count of "four spellings at
// eight sites" is the same rule measured at one predicate; read as sites it
// undercounts the surface by more than half, and read as families it says the
// tree has two when the honest answer is "it has one rule and thirty sites that
// each have to apply it, and six of them had not".
//
// WHAT THE FIRST DRAFTS GOT WRONG, AND WHAT CI SAID
// ---------------------------------------------------
// Recorded because the corrections are the argument, not a footnote. Two CI runs
// executed this file and failed four of my own rows before a single line of
// product code was read. Every one of them was me mis-reading the site, and
// every correction made the taxonomy sharper:
//
//   run 36725439283
//     * DiffViewerOverlay / ImageViewerOverlay filed as COMMAND. They hold no
//       text buffer, but they bind `G` and `_`, so admitting Shift is correct.
//     * the legacy-DialogOverlay row iterated the kitty mask {Ctrl, Meta, Alt}
//       and mapped Meta onto "no flag" — ConsoleKeyInfo has no Meta slot, so
//       the probe tested a gesture that path cannot express.
//     * the LeaderKeyRouter row read "returned true" as "resolved the chord";
//       the router returns true to CONSUME AND DISARM (:96).
//
//   run 36726768405
//     * VimComposerMode filed as COMMAND. Its gate at :37 is the command one,
//       but anything that FAILS that gate falls through to the composer, which
//       types it — so Shift+j is consumed as TEXT. It is a fourth widget
//       wearing both families, split by NormalMode.
//
// The pattern: in four of four cases the source supported two readings and only
// running it settled which. That is the argument for driving real keys here
// rather than asserting a table's opinion — a table I wrote is exactly the
// thing #833 is complaining about.
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
// THE PREDICATE DID LAND, AND WHY IT IS THREE CLASSES AND NOT ONE
// -------------------------------------------------------------
// The first draft of this file refused to extract the rule, on the grounds that
// collapsing the four spellings needs its own decision about which one wins.
// That was half right and it stalled: the fix for the live defect had nowhere to
// live except a sixth hand-written mask. So the half of the rule that IS data
// — the modifier classification, which is a fact about key ENCODING rather than
// about any widget — now has an owner, one class per vocabulary:
//
//   KeyModifierGate      (KeyModifiers)      Ctrl|Alt|Meta    Rendering/Input
//   KeyModifierSetGate   (KeyModifierSet)    Ctrl|Alt         Ui.Framework.State
//   ConsoleModifierGate  (ConsoleModifiers)  Control|Alt      Rendering/Input
//
// Three and not one, because the vocabularies are not interchangeable: the
// latter two have no Meta slot (KeyEventMapper folds Meta into Alt crossing into
// State), so a single helper would have to invent a Meta one of them cannot
// express and would be wrong in that one. Each class lives in the assembly that
// owns its vocabulary, and each states BOTH halves of the rule by name —
// AcceptsTypedChar (refuse Ctrl/Alt/Meta, admit Shift) and IsUnmodified (refuse
// everything) — so a call site says which family it is in instead of writing a
// bare `!= KeyModifiers.None` a reader has to interpret.
//
// NOT refactored, deliberately: the composer's chord TABLE (:196-252) and the
// remaining `(mods & KeyModifiers.Ctrl) != 0` forms. Those match a SPECIFIC
// chord, which is a different question from "is this a command or text" — Ctrl+C
// means "clear", not "this is a command" — and routing them through the gate
// would erase the binding.
//
// One site changed SEMANTICS, not just spelling, and it is written down here so
// a reviewer does not have to find it in the diff: ComposerController's history
// gate moved from `mods & (Shift|Ctrl|Alt|Meta) != 0` to `mods != None`. The two
// are identical for every modifier combination the kitty encoding can produce
// (bits 1-4, super/hyper collapsed into Meta) and differ only on a bit outside
// that range — where `!= None` refuses and the mask passed the key through. The
// stricter reading is the one wanted there, but "no behaviour change" would have
// been the false claim.
//
// WHAT THIS FILE STILL IS
// ----------------------
// A table in which every site is named and its family stated, plus a coverage
// scan so a NEW widget that routes keys without a row is a red build. That is
// still the part a predicate cannot do: the predicate names the RULE, and
// nothing in the rule can tell you which of the two halves a given widget wants,
// because that answer is not a property of the widget (see QuestionFormView by
// cursor, DialogOverlay by _kind, VimComposerMode by NormalMode). A new key
// widget has to declare its family HERE, out loud, and the review of that
// declaration is the whole value — the rule it declares is already named.
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

    /// <summary>
    /// No modifier test at all. NOT a family a site may declare — it is the
    /// absence of a decision, and the guard exists to make it unreachable. It
    /// was in the first draft of this file as a third family; that was wrong,
    /// because every site in it turned out to want the BUFFER gate once measured
    /// (they bind shifted runes of their own). See the header.
    /// </summary>
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
        // list: ChatKeyMap.cs:172 turns an unmatched printable rune into
        // ChatAction.Char, i.e. it is the place the tree decides "this is
        // text". It is a buffer site by the same test as the composer.
        new("ChatKeyMap", KeyGateFamily.Buffer, "src/Harbor.Ui.Framework.State/State/ChatKeyMap.cs"),

        // ---- COMMAND: no buffer; runes navigate or vote ------------------------
        new("QuestionFormView(option rows)", KeyGateFamily.Command, "src/Harbor.Ui.Framework.Rendering/Widgets/QuestionFormView.cs"),
        new("VimComposerMode(normal mode)", KeyGateFamily.Command, "src/Harbor.Tui.CellForge.Engine/Rendering/VimComposerMode.cs"),
        new("VimComposerMode(fall-through)", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge.Engine/Rendering/VimComposerMode.cs"),
        new("ApprovalGateView", KeyGateFamily.Command, "src/Harbor.Ui.Framework.Rendering/Widgets/ApprovalGateView.cs"),
        new("TreeView", KeyGateFamily.Command, "src/Harbor.Ui.Framework.Rendering/Widgets/TreeView.cs"),
        new("Tabs", KeyGateFamily.Command, "src/Harbor.Ui.Framework.Rendering/Widgets/Tabs.cs"),
        new("ToolCardTracker(expand)", KeyGateFamily.Command, "src/Harbor.Tui.CellForge/Chat/Streaming/ToolCardTracker.cs"),
        new("ToolCardTracker(image)", KeyGateFamily.Command, "src/Harbor.Tui.CellForge/Chat/Streaming/ToolCardTracker.cs"),
        new("LeaderKeyRouter", KeyGateFamily.Command, "src/Harbor.Tui.CellForge/Chat/Widgets/LeaderKeyRouter.cs"),

        // ReplInputLoop's two palette chords (#857 added ctrl+j beside the
        // existing ctrl+p). Neither holds a text buffer and both require an
        // EXACT modifier set, so an unmodified 'p'/'j' is not claimed and
        // Shift is refused — the COMMAND gate, in a file whose slash shortcut
        // is a BUFFER site. Fourth widget to wear both families, so the
        // both-families assertion above counts the named three by name rather
        // than over the whole table; this row is the declaration the coverage
        // check cannot make for a file that already has one.
        new("ReplInputLoop(ctrl+p palette)", KeyGateFamily.Command, "apps/Harbor.App.Cli/Repl/ReplInputLoop.cs"),
        new("ReplInputLoop(ctrl+j jump, #857)", KeyGateFamily.Command, "apps/Harbor.App.Cli/Repl/ReplInputLoop.cs"),

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

        // MarkupOverlay (#400) is the viewer shape with a buffer added:
        // Shift+arrows resize in EVERY tool state (:82-94), the text tool
        // feeds PendingText (:300), and command chords are refused at the
        // gate (:267) — so the family is Buffer, not Command. Ctrl+R redo
        // (:273) is the one consumed chord, claimed the way the viewer's
        // own shifted rune is.
        new("MarkupOverlay", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Widgets/MarkupOverlay.cs"),

        // ---- UNGATED: no modifier test at all (#833's real finding) ------------
        // SetupChecklistOverlay is REACHED from the product input loop
        // (ReplInputLoop.cs:379 → SetupChecklistController), so its missing
        // gate is a live chord-swallowing defect. The rest are reachable only
        // from tests today; they are listed because the gate is absent in code
        // regardless of who calls it, and because a wiring change is exactly
        // when that would start to matter.
        new("SetupChecklistOverlay", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Widgets/SetupChecklistOverlay.cs"),
        new("WhichKeyHelpOverlay", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Widgets/WhichKeyHelpOverlay.cs"),
        new("CellForgeHelpPanel", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Panels/CellForgeHelpPanel.cs"),
        new("CellForgeFileTreePanel", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Panels/CellForgeFileTreePanel.cs"),
        new("CellForgeDiagnosticsPanel", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Panels/CellForgeDiagnosticsPanel.cs"),
        new("CellForgeSubagentsPanel", KeyGateFamily.Buffer, "src/Harbor.Tui.CellForge/Chat/Panels/CellForgeSubagentsPanel.cs"),
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
        // not one. A declared boolean could not carry it. Three widgets are in
        // this position (QuestionFormView by cursor, DialogOverlay by _kind,
        // VimComposerMode by NormalMode), so the count is a measurement, not a
        // claim about one lucky example.
        await Assert.That(Table.Count(r => r.Where.EndsWith("QuestionFormView.cs", StringComparison.Ordinal)))
            .IsEqualTo(2)
            .Because("one file holds both families, which is the proof the family is not a per-type property");

        string[] bothFamilies =
        [
            "src/Harbor.Ui.Framework.Rendering/Widgets/QuestionFormView.cs",
            "src/Harbor.Tui.CellForge/Chat/Widgets/DialogOverlay.cs",
            "src/Harbor.Tui.CellForge.Engine/Rendering/VimComposerMode.cs",
        ];

        foreach (string file in bothFamilies)
        {
            int rows = Table.Count(r => string.Equals(r.Where, file, StringComparison.Ordinal));
            await Assert.That(rows).IsEqualTo(2)
                .Because($"{file} is a widget that wears both families at once, so it must be two rows");
        }
    }

    // ------------------------------------------------- FORMERLY UNGATED --

    /// <summary>
    /// The six sites that had NO modifier gate at all, which is the finding
    /// #833's two-family taxonomy could not name. One of them was a LIVE defect:
    /// <c>ReplInputLoop.cs:379</c> calls <c>SetupChecklistController.HandleKey</c>,
    /// so <c>Ctrl+q</c> closed the setup guide AND was consumed — the chord never
    /// reached the composer behind it.
    /// <para>
    /// The first draft of this file filed these as a THIRD family, on the
    /// reasoning that a two-family taxonomy has no name for them. That was wrong,
    /// and measuring said so: none of the six holds a text buffer, and five of
    /// six bind shifted runes of their own (<c>?</c>, <c>J</c>, <c>K</c>,
    /// <c>H</c>, <c>R</c>). So they want the BUFFER gate — refuse
    /// <c>Ctrl|Alt|Meta</c>, admit Shift — and they are filed as such. What they
    /// lacked was not a third family but a decision at all, which is why the
    /// honest rule is the one below: a site may not consume a rune without
    /// having chosen.
    /// </para>
    /// </summary>
    [Test]
    public async Task The_Formerly_Ungated_Sites_Choose_The_Buffer_Gate()
    {
        // The live defect, now closed: the chord falls through and the guide stays.
        var checklist = new SetupChecklistOverlay();
        checklist.Show();
        await Assert.That(checklist.HandleKey(KeyEvent.Char(new Rune('q'), KeyModifiers.Ctrl))).IsFalse()
            .Because("the setup guide holds no buffer, so Ctrl+q is a chord that belongs to the host — "
                + "consuming it here was the live defect (#833)");
        await Assert.That(checklist.Visible).IsTrue()
            .Because("the chord must fall through to the composer behind the guide, not close it");

        // Control for the control: the gate MOVED, it was not deleted. An
        // unmodified 'q' still dismisses...
        var plain = new SetupChecklistOverlay();
        plain.Show();
        await Assert.That(plain.HandleKey(KeyEvent.Char(new Rune('q')))).IsTrue();
        await Assert.That(plain.Visible).IsFalse()
            .Because("the unmodified dismiss key is the gesture this widget exists for");

        // ...and so does '?', which arrives CARRYING Shift. This is why the gate
        // is the buffer one and not IsUnmodified: a strict "no modifiers at all"
        // rule would refuse the widget's own dismiss key. The #824 trap, one
        // layer up.
        var shifted = new SetupChecklistOverlay();
        shifted.Show();
        await Assert.That(shifted.HandleKey(KeyEvent.Char(new Rune('?'), KeyModifiers.Shift))).IsTrue()
            .Because("'?' is a shifted rune — a real encoder sends the rune AND Shift — so a strict "
                + "no-modifier gate would refuse the widget's own dismiss key");

        // The legacy ConsoleKeyInfo site, same shape, on the overload it has. The
        // narrower vocabulary is the point: ConsoleKeyInfo has no Meta slot.
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
    }

    /// <summary>
    /// THE RULE, in its enforceable form: a site that consumes a rune may not do
    /// so without having chosen a family. Zero sites may be ungated — that is
    /// the state this guard was written to make unreachable, and it is the whole
    /// of what #833 adds to the two families the issue named.
    /// <summary>
    [Test]
    public async Task No_Site_Consumes_A_Rune_Without_Having_Chosen_A_Family()
    {
        List<string> ungated = Table
            .Where(r => r.Family == KeyGateFamily.Ungated)
            .Select(r => r.Name)
            .ToList();

        await Assert.That(string.Join(", ", ungated)).IsEmpty()
            .Because("a site that matches a rune with no modifier test is a chord-swallowing bug waiting to be "
                + "reported; six of them shipped that way, one of them live on the product input loop");
    }

    /// <summary>
    /// The four mounted panels, on the third vocabulary
    /// (<c>KeyModifierSet</c>, which has no <c>Meta</c>). All four bind shifted
    /// runes of their own — <c>FileTreePanel</c> binds <c>J</c>/<c>K</c>/<c>H</c>/
    /// <c>R</c>, <c>SubagentsPanel</c> binds <c>R</c>/<c>J</c>/<c>K</c> — so they
    /// want the buffer gate, and a strict one would refuse their own keys.
    /// </summary>
    [Test]
    public async Task The_Formerly_Ungated_Panels_Choose_The_Buffer_Gate()
    {
        var store = new UiStore();
        _ = store.Dispatch(new AppMsg.FocusPanel("help"));
        var ctx = new PanelContext(store.State, 80, 24, new PanelServices { Store = store });

        await Assert.That(new CellForgeHelpPanel().OnKey(UiKey.ForChar('?'), ctx)).IsTrue()
            .Because("the plain '?' still toggles the help panel");
        await Assert.That(new CellForgeHelpPanel().OnKey(UiKey.ForChar('?', KeyModifierSet.Ctrl), ctx)).IsFalse()
            .Because("Ctrl+? is a chord; the panel holds no buffer and must not claim it");
        await Assert.That(new CellForgeHelpPanel().OnKey(UiKey.ForChar('?', KeyModifierSet.Shift), ctx)).IsTrue()
            .Because("'?' IS a shifted rune, so the gate must admit Shift — the same reason the setup guide "
                + "keeps its '?' dismiss key");

        // The three cursor panels. Each is probed with a rune it actually binds,
        // which is not the same rune for all three: SubagentsPanel binds only
        // 'R' in its list view — its 'J'/'K' live in the transcript view, behind
        // a different state this test does not enter. Asserting 'J' on all three
        // would have measured the panel, not the gate.
        (IPanelProvider Panel, char Chord, char Shifted)[] cursors =
        [
            (new CellForgeFileTreePanel(), 'j', 'J'),
            (new CellForgeDiagnosticsPanel(), 'j', 'J'),
            (new CellForgeSubagentsPanel(), 'r', 'R'),
        ];

        foreach ((IPanelProvider panel, char chord, char shifted) in cursors)
        {
            string name = panel.GetType().Name;

            await Assert.That(panel.OnKey(UiKey.ForChar(chord, KeyModifierSet.Ctrl), ctx)).IsFalse()
                .Because($"{name} holds no buffer, so Ctrl+{chord} is a chord the host owns and the "
                    + "panel must not claim it");

            await Assert.That(panel.OnKey(UiKey.ForChar(shifted, KeyModifierSet.Shift), ctx)).IsTrue()
                .Because($"{name} binds '{shifted}' explicitly, so the gate must admit Shift — a strict "
                    + "no-modifier rule would refuse the panel's own key");

            await Assert.That(panel.OnKey(UiKey.ForChar(shifted), ctx)).IsTrue()
                .Because($"{name} must still work unmodified; the gate MOVED, it was not deleted");
        }
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
        ];
    }

    /// <summary>
    /// <see cref="VimComposerMode" /> is a FOURTH widget wearing both families,
    /// and the split is a mode flag rather than a cursor. In NORMAL mode a rune
    /// is a command and the gate is <c>Modifiers == None</c> (:37); anything
    /// that fails that test FALLS THROUGH to the composer, which is a text
    /// buffer and types the rune.
    /// <para>
    /// So <c>Shift+j</c> is consumed — as TEXT, not as a command. Filing this
    /// site as command-only was my error, and CI run 36726768405 caught it: the
    /// strict table said "a command site must not consume Shift+char" and the
    /// site consumed it correctly, as a letter in the composer's buffer.
    /// </para>
    /// </summary>
    [Test]
    public async Task VimComposer_Mode_Flag_Picks_The_Family_Not_The_Type()
    {
        // Normal mode + unmodified rune: a command, and the buffer stays empty.
        var normal = NormalModeComposer();
        var normalComposer = new ComposerController();
        _ = normal.HandleKey(KeyEvent.Char(new Rune('j')), normalComposer);
        await Assert.That(normalComposer.Buffer.SnapshotText()).IsEqualTo(string.Empty)
            .Because("in normal mode 'j' is history recall, not text (:76)");

        // Same widget, normal mode, Shift held: the command gate is skipped and
        // the key falls through to the composer, which TYPES it.
        var shifted = NormalModeComposer();
        var shiftedComposer = new ComposerController();
        _ = shifted.HandleKey(KeyEvent.Char(new Rune('J'), KeyModifiers.Shift), shiftedComposer);
        await Assert.That(shiftedComposer.Buffer.SnapshotText()).IsEqualTo("J")
            .Because("Shift+J fails the 'Modifiers == None' test, so it is not a command — and the "
                + "composer's own gate admits Shift, so it is a capital letter. One type, two families, "
                + "decided by NormalMode: the fourth proof the family is not a per-type property");

        // …and the buffer half still refuses a command modifier.
        var ctrl = NormalModeComposer();
        var ctrlComposer = new ComposerController();
        _ = ctrl.HandleKey(KeyEvent.Char(new Rune('j'), KeyModifiers.Ctrl), ctrlComposer);
        await Assert.That(ctrlComposer.Buffer.SnapshotText()).IsEqualTo(string.Empty)
            .Because("Ctrl+j is a chord the composer's gate refuses (:173)");
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
