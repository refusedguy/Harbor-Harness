// CellForgeEngineEnterPolicyParityRules.cs — issue #435.
//
// THE DEPENDENCY THIS FILE EXISTSTO KEEP HONEST
// ---------------------------------------------
// #435 deleted the last `Harbor.Ui.Framework.State` edge out of
// `Harbor.Tui.CellForge.Engine` by moving the Enter-key DECISION down into the
// BCL-only layer:
//
//     Harbor.Ui.Framework.Rendering.Input.EnterPolicy.Resolve(...)  ->  EnterDecision
//     Harbor.Ui.Framework.State.EnterKeyPolicy.Resolve(...)        ->  ChatAction
//
// That is the #162 shape applied a second time (mirror low, adapt high), and it
// is the only way #435 could go without either duplicating #359's policy or
// deleting the engine's composer behaviour. The price is a NEW dependency seam
// that did not exist before this change: two enums, in two assemblies, that must
// stay 1:1 and are connected by one `switch` with a `default` arm.
//
// The `default` arm is the hazard. `EnterDecision` is free to grow — it lives in
// the LOW layer precisely so that adding a case there is cheap and needs no
// cross-assembly negotiation. If somebody adds `EnterDecision.Save` and the
// State-side switch is not touched, the new member falls through `default`, which
// returns `ChatAction.None`. The store then receives a no-op, the reducer drops
// the key, and NOTHING fails: no compile error (the switch is exhaustive over the
// members it knows), no test failure (nothing asserted the new member), no warning.
// The feature is silently dead. That is a worse failure than a build break,
// because a build break announces itself and this does not.
//
// So the seam needs a predicate. These three are it, and none of them is the
// #989 shape: #989 is a dictionary of things that must be ABSENT (which types,
// which names, which imports). This is a check on a RELATIONSHIP between two
// types that both legitimately exist — an atomicity property, not an absence one.
//
//   P1 COVERAGE     — every `EnterDecision` member is actually PRODUCED by
//                     `EnterPolicy.Resolve` somewhere in the 16-cell modifier
//                     matrix. An enum member nothing can return is either dead
//                     vocabulary or a sign the policy and the enum have drifted
//                     apart; both are worth a red row.
//   P2 NO SWALLOW    — across the same matrix, whenever the low layer returns a
//                     non-`None` decision, the high layer must NOT answer
//                     `ChatAction.None`. This is the `default`-arm failure,
//                     stated as a predicate.
//   P3 THE TABLE     — the full 16 rows, asserted explicitly, so a behaviour
//                     change on EITHER side of the seam is caught even when both
//                 sides change together.
//
// WHY THE MATRIX, AND WHY ALL SIXTEEN CELLS
// ------------------------------------------
// `KeyModifiers` is a 4-flag enum, so the Enter family's input space is 2^4 = 16
// cells and every one of them is reachable from a real terminal (Ctrl+Shift+Alt+
// Enter is a thing kitty will happily send). Testing a sample would leave the
// precedence order untested, and precedence IS the policy: #359's contract is
// "Ctrl wins, then Shift/Alt/Meta, else submit". Enumerating all 16 is cheaper
// than reasoning about which 3 would have been representative.
//
// The `KeyModifierSet` overload of `EnterKeyPolicy.Resolve` takes a State's own
// modifier enum, which has NO Meta member — so the Meta cell is unobservable
// through it, and this file exercises the raw-flags overload instead. That is
// also the overload the composer calls, so it is the one that matters.
//
// NON-VACUITY
// -----------
// `NonVacuityTheSwallowPredicateFiresOnAPlantedAdapterOnly` does not test the
// real enums; it tests the PREDICATE against a synthetic adapter table in which
// a non-`None` decision maps to `ChatAction.None`, which is a guaranteed hit by
// construction, plus the same table with that one cell repaired, which is a
// guaranteed miss. A guard that has never been seen to go red is the merged #591
// failure — there, the tool halved the rule twice and reported plausible zeros,
// so a real violation read as a clean tree. The planted pair here is chosen so
// that P2 firing and P2 staying silent are both witnessed, in the same run, on
// data no amount of editing the product can make ambiguous.

using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #435: the <c>EnterDecision</c> → <c>ChatAction</c> seam that replaced
///     the engine's <c>Harbor.Ui.Framework.State</c> edge must not be able to
///     swallow a decision silently.
/// </summary>
public sealed class CellForgeEngineEnterPolicyParityRules
{
    /// <summary>
    ///     One cell of the modifier matrix. The four flags are the whole input
    ///     space of the Enter family; see the header note on why all sixteen are
    ///     enumerated.
    /// </summary>
    /// <param name="Ctrl">Ctrl held.</param>
    /// <param name="Shift">Shift held.</param>
    /// <param name="Alt">Alt held.</param>
    /// <param name="Meta">Meta held.</param>
    private readonly record struct Modifiers(bool Ctrl, bool Shift, bool Alt, bool Meta)
    {
        /// <summary>How the cell is named in a failure message.</summary>
        public override string ToString()
        {
            string Flags()
            {
                List<string> on = [];
                if (Ctrl) on.Add("Ctrl");
                if (Shift) on.Add("Shift");
                if (Alt) on.Add("Alt");
                if (Meta) on.Add("Meta");
                return on.Count == 0 ? "none" : string.Join('+', on);
            }

            return Flags();
        }
    }

    /// <summary>
    ///     All 2^4 modifier combinations, in a stable order. Built by nested loops
    ///     rather than a query so the order — and therefore the order of every
    ///     failure message this file produces — is fixed by the loops.
    /// </summary>
    private static readonly Modifiers[] Matrix = BuildMatrix();

    /// <summary>Enumerates the 16 cells, least-significant flag varying fastest.</summary>
    private static Modifiers[] BuildMatrix()
    {
        bool[] flags = [false, true];
        var cells = new List<Modifiers>(16);

        foreach (bool ctrl in flags)
        {
            foreach (bool shift in flags)
            {
                foreach (bool alt in flags)
                {
                    foreach (bool meta in flags)
                    {
                        cells.Add(new Modifiers(ctrl, shift, alt, meta));
                    }
                }
            }
        }

        return [.. cells];
    }

    /// <summary>
    ///     What #359 promised, restated independently of both implementations so
    ///     that a change on either side of the seam is visible as a diff here.
    /// </summary>
    /// <param name="mods">The modifier cell.</param>
    private static EnterDecision Expected(Modifiers mods) =>
        mods.Ctrl ? EnterDecision.None
        : mods.Shift || mods.Alt || mods.Meta ? EnterDecision.InsertNewline
        : EnterDecision.Submit;

    /// <summary>
    ///     What the store is supposed to be told, derived from the decision rather
    ///     than written out as a second table. Deriving it is deliberate: a second
    ///     hand-written table could be edited in the same commit as the first and
    ///     agree with itself, which is exactly the "two copies drift" failure this
    ///     whole file is about.
    /// </summary>
    /// <param name="decision">The low-layer decision.</param>
    private static ChatAction ActionFor(EnterDecision decision) => decision switch
    {
        EnterDecision.InsertNewline => ChatAction.InsertNewline,
        EnterDecision.Submit => ChatAction.Submit,
        _ => ChatAction.None,
    };

    /// <summary>
    ///     P2 as a function, so the planted control can call the same code the
    ///     real rule calls. Takes the observed (decision, action) pairs and returns
    ///     the ones where a real decision was answered with the store's no-op.
    /// </summary>
    /// <param name="observed">Decision/action pairs, one per matrix cell.</param>
    private static List<string> Swallowed(IEnumerable<(EnterDecision Decision, ChatAction Action)> observed)
    {
        var swallowed = new List<string>();

        foreach ((EnterDecision decision, ChatAction action) in observed)
        {
            if (decision != EnterDecision.None && action == ChatAction.None)
            {
                swallowed.Add($"{decision} -> {action}");
            }
        }

        return swallowed;
    }

    /// <summary>
    ///     The matrix, read off BOTH sides of the seam. One place produces the
    ///     pairs, so P2 and the planted control cannot disagree about what the
    ///     data is.
    /// </summary>
    private static (EnterDecision Decision, ChatAction Action)[] Observe()
    {
        var observed = new List<(EnterDecision Decision, ChatAction Action)>(Matrix.Length);

        foreach (Modifiers mods in Matrix)
        {
            observed.Add((
                EnterPolicy.Resolve(mods.Ctrl, mods.Shift, mods.Alt, mods.Meta),
                EnterKeyPolicy.Resolve(mods.Ctrl, mods.Shift, mods.Alt, mods.Meta)));
        }

        return [.. observed];
    }

    // =====================================================================
    // P1 — every enum member is reachable.
    // =====================================================================

    /// <summary>
    ///     Every <see cref="EnterDecision" /> member must be produced by
    ///     <see cref="EnterPolicy.Resolve" /> for at least one modifier cell. A
    ///     member no cell produces is dead vocabulary on a type whose whole reason
    ///     to exist is to be the low-layer half of a two-member contract.
    /// </summary>
    [Test]
    public async Task EveryEnterDecisionMemberIsReachableFromTheModifierMatrix()
    {
        EnterDecision[] produced = [.. Observe().Select(o => o.Decision).Distinct()];
        EnterDecision[] declared = [.. Enum.GetValues<EnterDecision>()];

        var orphans = declared.Where(d => !produced.Contains(d)).ToArray();

        await Assert.That(orphans).IsEmpty()
            .Because(
                "EnterDecision is the low half of the #435 EnterPolicy -> ChatAction seam. A member "
                + "no modifier combination can produce is either dead vocabulary or evidence that "
                + "the enum and the policy have drifted apart — and if it is meant to be produced, "
                + "its cell is missing and the precedence order is wrong. Declared: "
                + string.Join(", ", declared) + "; produced: " + string.Join(", ", produced)
                + ". Unreachable: " + (orphans.Length == 0 ? "(none)" : string.Join(", ", orphans)));
    }

    // =====================================================================
    // P2 — the `default` arm must not be able to eat a real decision.
    // =====================================================================

    /// <summary>
    ///     Across the whole matrix, a non-<see cref="EnterDecision.None" /> decision
    ///     must never come back as <see cref="ChatAction.None" />.
    /// </summary>
    [Test]
    public async Task NoRealEnterDecisionIsSwallowedByTheStateAdapter()
    {
        List<string> swallowed = Swallowed(Observe());

        await Assert.That(swallowed).IsEmpty()
            .Because(
                "EnterKeyPolicy adapts EnterDecision to ChatAction through a switch with a default "
                + "arm. EnterDecision lives in the LOW layer on purpose, so adding a member there is "
                + "cheap and needs no cross-assembly negotiation — which is exactly what makes the "
                + "default arm dangerous. A new member nobody routed falls through to ChatAction.None, "
                + "the reducer drops the key, and nothing anywhere fails: the switch is exhaustive "
                + "over the members it knows, no test asserted the new member, and no warning fires. "
                + "The feature is silently dead, which is worse than a build break because a build "
                + "break announces itself. Add the case to EnterKeyPolicy.ToAction. Swallowed: "
                + (swallowed.Count == 0 ? "(none)" : string.Join("; ", swallowed)));
    }

    // =====================================================================
    // P3 — the table itself, so both sides changing together is still visible.
    // =====================================================================

    /// <summary>
    ///     All sixteen cells, checked against #359's contract on both sides of the
    ///     seam independently. A commit that changes the policy AND the adapter
    ///     together still shows up here, because the expected values are written
    ///     out and neither implementation can be edited into agreeing with them.
    /// </summary>
    [Test]
    public async Task TheEnterTableMatchesTheContractOnBothSidesOfTheSeam()
    {
        var wrong = new List<string>();

        foreach (Modifiers mods in Matrix)
        {
            EnterDecision expectedDecision = Expected(mods);
            ChatAction expectedAction = ActionFor(expectedDecision);

            EnterDecision actualDecision = EnterPolicy.Resolve(mods.Ctrl, mods.Shift, mods.Alt, mods.Meta);
            ChatAction actualAction = EnterKeyPolicy.Resolve(mods.Ctrl, mods.Shift, mods.Alt, mods.Meta);

            if (actualDecision != expectedDecision)
            {
                wrong.Add($"policy: {mods} => {actualDecision}, expected {expectedDecision}");
            }

            if (actualAction != expectedAction)
            {
                wrong.Add($"adapter: {mods} => {actualAction}, expected {expectedAction}");
            }
        }

        await Assert.That(wrong).IsEmpty()
            .Because(
                "#359's Enter contract: Ctrl wins and is ignored, Shift/Alt/Meta insert a newline, "
                + "unmodified Enter submits. Both halves of the #435 seam are checked against that "
                + "stated contract rather than against each other, because a test that only compares "
                + "the two implementations is satisfied by both being wrong in the same way. All "
                + Matrix.Length + " cells checked. Mismatches: "
                + (wrong.Count == 0 ? "(none)" : string.Join("; ", wrong)));
    }

    // =====================================================================
    // Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The swallow predicate must fire on an adapter that really does drop a
    ///     decision, and must stay silent on one where that same cell is repaired.
    ///     Both halves are guaranteed by construction, so this test cannot pass
    ///     vacuously and cannot be satisfied by a predicate that always returns an
    ///     empty list.
    /// </summary>
    [Test]
    public async Task NonVacuityTheSwallowPredicateFiresOnAPlantedAdapterOnly()
    {
        (EnterDecision Decision, ChatAction Action)[] plantedSwallow =
        [
            (EnterDecision.None, ChatAction.None),
            (EnterDecision.InsertNewline, ChatAction.None), // the bug: default arm ate it
            (EnterDecision.Submit, ChatAction.Submit),
        ];

        (EnterDecision Decision, ChatAction Action)[] plantedRepaired =
        [
            (EnterDecision.None, ChatAction.None),
            (EnterDecision.InsertNewline, ChatAction.InsertNewline),
            (EnterDecision.Submit, ChatAction.Submit),
        ];

        await Assert.That(Swallowed(plantedSwallow)).IsNotEmpty()
            .Because(
                "'plantedSwallow' maps the real EnterDecision.InsertNewline to ChatAction.None, which "
                + "is precisely the default-arm failure P2 exists to catch. An empty result here "
                + "means the predicate has a hole and P2 above is enforcing nothing — the merged #591 "
                + "shape, where a tool that had halved its own rule reported a plausible zero and a "
                + "real violation read as a clean tree.");

        await Assert.That(Swallowed(plantedRepaired)).IsEmpty()
            .Because(
                "'plantedRepaired' differs from 'plantedSwallow' by one cell, and it is what the real "
                + "adapter must look like. A predicate that fires here is one that will eventually "
                + "be deleted by the first person it annoys, and — worse — will have trained everyone "
                + "to ignore it.");
    }
}