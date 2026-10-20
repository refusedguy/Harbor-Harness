// CellForgeEngineVocabularyParityRules.cs — issue #1621.
//
// THE DEPENDENCY THIS FILE EXISTS TO KEEP HONEST
// ---------------------------------------------
// #436 turned `Harbor.Tui.CellForge.Engine` into a standalone leaf: zero
// ProjectReference entries to Harbor assemblies, zero per-file imports of
// Harbor namespaces. That is a good trade and it is guarded
// (CellForgeEngineAtomicityRules). But the leaf could not keep BORROWING the
// cell/screen/input vocabulary it used to live in
// `Harbor.Ui.Framework.Rendering`, so the vocabulary was PORTED — copied — into
// the engine:
//
//     Cell          Rect          CellDiffOption   UnicodeWidth
//     ScreenBuffer  CellStyle     KeyEvent         KeyCode
//     KeyEventType  KeyModifiers  UiKeyDto         IFocusTarget  RgbColor
//
// Thirteen pairs, two homes each. The copies were made verbatim, and they have
// been kept in sync BY HAND ever since: a fix lands in whichever copy the
// author had open, and nothing in the tree notices. That is the hazard #1621
// names — "синхронизация вручную" — and it is invisible precisely because
// both copies compile.
//
// WHY THE ENGINE IS THE SOURCE OF TRUTH, AND NOT THE OTHER WAY ROUND
// -------------------------------------------------------------------
// The two ways out of a hand-synced pair are MERGE and GUARD.
//
//   * MERGE — one type instead of two. The engine cannot reference the shared
//     layer (that is the whole point of #436), so the merge can only go the
//     other way: `Harbor.Ui.Framework.Rendering` would have to reference
//     `Harbor.Tui.CellForge.Engine`. That inverts the layering the engine's
//     isolation bought — the rendering package is the renderer-agnostic shared
//     layer every backend builds on, and a CellForge-specific engine under it
//     is a backend-specific dependency in the wrong direction. It would also
//     collapse the dual-vocabulary boundary that `ScreenSession` is the
//     documented crossing point of (the chat side paints a Rendering grid, the
//     engine diffs its own), and it would rename a public type in every
//     assembly that consumes it.
//
//   * GUARD — one side is declared the source of truth and the other side is
//     checked against it, so the hand-sync becomes a mechanical fact. This
//     file is that guard, and it is the branch #1621 offers as the
//     alternative: "arch-тест побайтовой идентичности", the byte-identity
//     arch test that grows out of CellForgeEngineEnterPolicyParityRules.
//
// WHAT IS COMPARED, AND WHY IT IS NOT THE RAW FILE
// -------------------------------------------------
// The comparison is over CODE, not over bytes on disk:
//
//   * whole-line comments are dropped — the engine's files carry the "#436:
//     engine-owned port of …" marker and each side documents its own
//     assembly's types (`<see cref="KeyEventMapper"/>` resolves in the shared
//     layer and cannot resolve in the engine, so the doc text legitimately
//     differs and a cref difference is not drift);
//   * blank lines are dropped — they are formatting, and the two sides were
//     formatted by different hands;
//   * the namespace declaration is canonicalised — the two sides declare
//     DIFFERENT namespaces on purpose (`Harbor.Tui.CellForge.Rendering` vs
//     `Harbor.Ui.Framework.Rendering`), and that difference IS the port;
//   * trailing comments are KEPT — they are part of the code line, and they
//     travel with the copy.
//
// What survives that is the executable text, and it must be identical line for
// line. One difference is sanctioned and named in the table: the engine is
// BCL-only, so the `Ensure.NotNull` alias (Harbor.Extensions) is spelled
// `ArgumentNullException.ThrowIfNull` there — the alias's own delegate, so the
// two throw the same exception with the same parameter name.
//
// THE FAILURE THIS PREVENTS
// -------------------------
// A one-sided edit to a vocabulary file is not a compile error (the two copies
// are separate types in separate assemblies), not a test failure (nothing
// asserted that they agreed), and not a warning. The engine would then compute
// cell widths, styles and key codes by one rule and the shared layer by
// another, and the two grids would disagree in a way that only shows up as a
// wrong character at the far edge of a line. That is the silent-drift failure
// the #591-shaped guards in this repo exist for.
//
// NON-VACUITY
// -----------
// `NonVacuityTheParityCheckerFiresOnAPlantedDivergenceOnly` runs the SAME
// comparison function the real rule runs, over a planted pair that differs by
// one line (and one that does not), so the rule cannot pass by never
// comparing anything — the merged #591 shape, where a rule that had halved
// itself reported a plausible zero and a real violation read as a clean tree.

using System.Text.RegularExpressions;
using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Issue #1621: the engine's cell/screen/input vocabulary is copied into the
///     shared rendering layer, and the copies must not drift apart.
/// </summary>
public sealed class CellForgeEngineVocabularyParityRules
{
    /// <summary>
    ///     The engine project — the vocabulary's source of truth. The shared
    ///     twins live in the rendering layer and in the design system.
    /// </summary>
    private const string EngineProject = "Harbor.Tui.CellForge.Engine";

    /// <summary>
    ///     A file-scoped namespace declaration. The two sides of a pair declare
    ///     different namespaces on purpose, so the declaration is canonicalised
    ///     before the comparison instead of being treated as a difference.
    /// </summary>
    private static readonly Regex NamespaceDeclaration = new(
        @"^namespace\s+[A-Za-z_][A-Za-z0-9_.]*\s*;$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     The executable lines of a file: comments and blank lines dropped, the
    ///     namespace declaration canonicalised, everything else trimmed.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The code lines, in order.</returns>
    private static List<string> CodeLines(string path)
    {
        var lines = new List<string>();

        foreach (string raw in File.ReadAllLines(path))
        {
            string trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            // Whole-line comments only: a trailing comment stays with its code
            // line (it travels with the copy), while a comment line carries no
            // code and each side is free to document its own assembly.
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(NamespaceDeclaration.IsMatch(trimmed) ? "namespace <canonical>;" : trimmed);
        }

        return lines;
    }

    /// <summary>
    ///     Compares the two sides of a pair, line for line. A difference is
    ///     allowed only when the row declares it as a sanctioned rewrite.
    /// </summary>
    /// <param name="engine">The engine side (the source of truth).</param>
    /// <param name="shared">The shared twin.</param>
    /// <param name="rewrites">The sanctioned spelling differences.</param>
    /// <returns>The differences found; empty when the two sides agree.</returns>
    internal static List<string> Diff(
        IReadOnlyList<string> engine,
        IReadOnlyList<string> shared,
        IReadOnlyList<CellForgeEngineAtomicityRules.VocabularyRewrite> rewrites)
    {
        var differences = new List<string>();

        if (engine.Count != shared.Count)
        {
            differences.Add($"line count: engine {engine.Count}, shared {shared.Count}");
        }

        int limit = Math.Min(engine.Count, shared.Count);
        var used = new bool[rewrites.Count];

        for (int i = 0; i < limit; i++)
        {
            if (engine[i] == shared[i])
            {
                continue;
            }

            int sanctioned = -1;
            for (int r = 0; r < rewrites.Count && sanctioned < 0; r++)
            {
                if (used[r])
                {
                    continue;
                }

                if (shared[i] == rewrites[r].Shared && engine[i] == rewrites[r].Engine)
                {
                    sanctioned = r;
                }
            }

            if (sanctioned < 0)
            {
                differences.Add($"line {i + 1}: engine = {engine[i]} | shared = {shared[i]}");
            }
            else
            {
                used[sanctioned] = true;
            }
        }

        return differences;
    }

    /// <summary>
    ///     Every shared twin of the engine's vocabulary is a verbatim copy of it.
    ///     The engine side is the source of truth; the twin is the copy, and the
    ///     only freedom it has is the sanctioned rewrites the table names.
    /// </summary>
    [Test]
    public async Task EverySharedVocabularyCopyIsVerbatimIdenticalToTheEngineSourceOfTruth()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return;
        }

        var drift = new List<string>();

        foreach (CellForgeEngineAtomicityRules.VocabularyPair pair in CellForgeEngineAtomicityRules.VocabularyPairs)
        {
            string enginePath = Path.Combine(root, "src", EngineProject,
                pair.EngineFile.Replace('/', Path.DirectorySeparatorChar));
            string sharedPath = Path.Combine(root, "src", pair.SharedProject,
                pair.SharedFile.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(enginePath) || !File.Exists(sharedPath))
            {
                // The inventory half of #1621 (CellForgeEngineAtomicityRules)
                // reports a missing file; there is nothing to compare here.
                continue;
            }

            List<string> differences = Diff(CodeLines(enginePath), CodeLines(sharedPath), pair.Rewrites);

            foreach (string difference in differences)
            {
                drift.Add($"src/{pair.SharedProject}/{pair.SharedFile} vs src/{EngineProject}/{pair.EngineFile}: "
                          + difference);
            }
        }

        await Assert.That(drift).IsEmpty()
            .Because(
                "The engine's cell/screen/input vocabulary is copied into the shared rendering layer (#436 made "
                + "the engine a zero-Harbor-reference leaf, and a leaf cannot borrow), and the two copies were "
                + "kept in sync BY HAND. A one-sided edit is not a compile error, not a test failure and not a "
                + "warning — the two grids would just compute widths, styles and key codes by different rules, "
                + "and the disagreement would surface as a wrong character at the edge of a line. The engine "
                + "side is the source of truth; the shared twin must be a verbatim copy of it, modulo the "
                + "sanctioned rewrites the table names (the BCL-only guard retype). Drift: "
                + (drift.Count == 0 ? "(none)" : string.Join("; ", drift)));
    }

    /// <summary>
    ///     The comparison must fire on a planted divergence and stay silent on a
    ///     planted pair that agrees. Both halves run the same function the real
    ///     rule runs, so the rule cannot pass by comparing nothing.
    /// </summary>
    [Test]
    public async Task NonVacuityTheParityCheckerFiresOnAPlantedDivergenceOnly()
    {
        List<string> engine =
        [
            "namespace <canonical>;",
            "public int Width { get; }",
            "public int Height { get; }",
        ];

        List<string> agreeing =
        [
            "namespace <canonical>;",
            "public int Width { get; }",
            "public int Height { get; }",
        ];

        List<string> drifted =
        [
            "namespace <canonical>;",
            "public int Width { get; }",
            "public int Height { get; init; }",
        ];

        await Assert.That(Diff(engine, agreeing, [])).IsEmpty()
            .Because(
                "'agreeing' is the engine side verbatim, so the comparison must find nothing in it. A checker "
                + "that fires here would be deleted by the first person it annoys — and, worse, it would have "
                + "trained everyone to ignore it.");

        await Assert.That(Diff(engine, drifted, [])).IsNotEmpty()
            .Because(
                "'drifted' differs from the engine side by exactly one line — a setter nobody copied. That is "
                + "the drift #1621 is about, and it is invisible to the compiler because the two sides are "
                + "separate types in separate assemblies. An empty result here means the parity rule has a hole "
                + "and enforces nothing — the merged #591 shape.");
    }
}
