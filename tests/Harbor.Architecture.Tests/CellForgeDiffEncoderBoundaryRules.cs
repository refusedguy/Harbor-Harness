// CellForgeDiffEncoderBoundaryRules.cs: the boundary between the portable
// cell-diff protocol and the CellForge engine's frame (issue #1519).
//
// WHAT THIS FILE EXISTS FOR
// -------------------------
// #1519 found the bridge between the two cell vocabularies in ONE method:
// `CopyEngineToRendering` copied the engine's front buffer cell by cell into
// the Rendering vocabulary and transferred the per-cell diff directive by
// NUMERIC CAST, `(UIR.CellDiffOption)(byte)`. A numeric cast is a claim that
// two enums declare the same members in the same order, and nothing checked
// the claim: the two `CellDiffOption` declarations are separate types in
// separate assemblies (the engine is a zero-Harbor-reference leaf since
// #436, so the shared layer holds a copy of its vocabulary), and a member
// added to one of them crossed as a DIFFERENT member of the other.
//
// The fix is a Bridge: a narrow port (`IFrameSource`) that the read side
// speaks, an implementor (`EngineFrameSource`) that answers in the Rendering
// vocabulary, and a directive table spelled out member by member. This file
// is the gate that keeps that boundary honest. It grades three things:
//
//   1. THE ABSTRACTION DOES NOT NAME THE IMPLEMENTATION. Nothing under
//      `src/Harbor.Ui.Framework.Rendering/` may name the engine in its CODE.
//      The layer matrix forbids the project reference as well (the rendering
//      package's row does not list the engine), and this is the same
//      boundary stated at the level of the source, where it is readable: the
//      protocol has to be usable by a backend that has no engine at all.
//
//   2. THE IMPLEMENTATION DOES NOT NAME THE ABSTRACTION. Nothing under
//      `src/Harbor.Tui.CellForge.Engine/` may name the shared rendering
//      layer in its CODE. The broader form of this rule is
//      `CellForgeEngineAtomicityRules` (the engine declares zero Harbor
//      references and zero Harbor imports); this one states the same
//      boundary for the two packages the bridge joins.
//
//   3. THE DIRECTIVE TABLE IS NOT CARRIED BY A NUMERIC CAST. The table
//      lives in one file, one member per line. The numeric cast is the shape
//      that hid the drift, so it is banned outright: a cast of a diff
//      directive anywhere in the product is a red row.
//
// WHY COMMENTS ARE STRIPPED BEFORE THE SCAN
// -----------------------------------------
// Both packages NAME the other one in prose: the rendering package's README
// and doc comments explain that the engine holds a copy of the cell
// vocabulary, and the engine's files carry the marker that says which file
// they were ported from. A scan that read the comments would grade the
// documentation of the boundary as a violation of it. The scan therefore
// strips comments first (the shared `SourceCommentStripper`), and what it
// grades is CODE: the `using` directives and the qualified names that would
// actually bind a type.
//
// NON-VACUITY
// -----------
// `NonVacuity_The_Scanner_Finds_A_Planted_Violation_And_Ignores_A_Clean_Line`
// runs the SAME matchers the rules run over a planted line that carries the
// banned cast and a line that does not, so a rule cannot pass by never
// matching anything.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>The matcher and the two source walks the boundary rules use.</summary>
internal static class DiffEncoderBoundaryProbe
{
    /// <summary>
    ///     A numeric transfer of a diff directive: <c>(CellDiffOption)(byte)</c>
    ///     or <c>(byte)someCellDiffOption</c>, with or without a namespace
    ///     qualifier.
    /// </summary>
    internal static readonly Regex NumericDiffOptionCast = new(
        @"\(\s*(?:[A-Za-z_][\w]*\.)*CellDiffOption\s*\)\s*\(\s*byte\s*\)"
        + @"|\(\s*byte\s*\)\s*(?:[A-Za-z_][\w]*\.)*CellDiffOption",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Whether the line transfers a diff directive by numeric cast.
    /// </summary>
    /// <param name="line">One line of source.</param>
    /// <returns>True when the line carries the banned cast.</returns>
    internal static bool IsNumericDiffOptionCast(string line) => NumericDiffOptionCast.IsMatch(line);

    /// <summary>
    ///     The project-relative files whose CODE (comments stripped) names
    ///     <paramref name="needle" />.
    /// </summary>
    /// <param name="projectDir">The project directory under <c>src/</c>.</param>
    /// <param name="needle">The namespace to look for.</param>
    /// <returns>The offending files, repo-relative and sorted.</returns>
    internal static List<string> FilesNaming(string projectDir, string needle)
    {
        var offenders = new List<string>();

        foreach (string file in RepoPaths.EnumerateCsFiles(projectDir))
        {
            if (CodeNames(file, needle))
            {
                offenders.Add(Relative(file));
            }
        }

        offenders.Sort(StringComparer.Ordinal);
        return offenders;
    }

    /// <summary>
    ///     Every product file whose CODE carries the banned numeric cast, with
    ///     the line it sits on.
    /// </summary>
    /// <returns>The offending sites, repo-relative and sorted.</returns>
    internal static List<string> NumericCastSites()
    {
        var offenders = new List<string>();

        if (RepoPaths.RepoRoot is not { } root)
        {
            return offenders;
        }

        foreach (string tree in new[] { "src", "apps" })
        {
            string dir = Path.Combine(root, tree);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] code = SourceCommentStripper.StripAll(File.ReadAllLines(file));
                for (int i = 0; i < code.Length; i++)
                {
                    if (IsNumericDiffOptionCast(code[i]))
                    {
                        offenders.Add($"{Relative(file)}:{i + 1}");
                        break;
                    }
                }
            }
        }

        offenders.Sort(StringComparer.Ordinal);
        return offenders;
    }

    /// <summary>
    ///     Whether the file's CODE (comments stripped) names the needle.
    /// </summary>
    private static bool CodeNames(string file, string needle)
    {
        foreach (string line in SourceCommentStripper.StripAll(File.ReadAllLines(file)))
        {
            if (line.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The repo-relative form of an absolute path, for the failure text.
    /// </summary>
    private static string Relative(string path)
    {
        if (RepoPaths.RepoRoot is { } root && path.StartsWith(root, StringComparison.Ordinal))
        {
            return path[(root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
        }

        return path.Replace(Path.DirectorySeparatorChar, '/');
    }
}

/// <summary>
///     Issue #1519: the cell-diff protocol and the CellForge engine's frame
///     meet at a narrow port, and neither side names the other.
/// </summary>
public sealed class CellForgeDiffEncoderBoundaryRules
{
    /// <summary>The shared rendering package: the abstraction side.</summary>
    private const string AbstractionProject = "Harbor.Ui.Framework.Rendering";

    /// <summary>The engine package: the implementation side.</summary>
    private const string ImplementationProject = "Harbor.Tui.CellForge.Engine";

    /// <summary>
    ///     Rule 1: the abstraction does not name the implementation. The
    ///     protocol must be readable by a backend that has no engine.
    /// </summary>
    [Test]
    public async Task The_Abstraction_Does_Not_Name_The_Implementation()
    {
        List<string> offenders = DiffEncoderBoundaryProbe.FilesNaming(
            AbstractionProject,
            "Harbor.Tui.CellForge");

        await Assert.That(offenders).IsEmpty()
            .Because(
                "the cell-diff protocol is the renderer-agnostic surface: a backend that has no "
                + "CellForge engine must be able to implement and consume it. A file in the "
                + "shared rendering package that names the engine in its CODE ties the abstraction "
                + "to one implementation, which is the coupling #1519 removed. Name it in a "
                + "comment if the prose needs it. Offenders: " + string.Join(" | ", offenders));
    }

    /// <summary>
    ///     Rule 2: the implementation does not name the abstraction. The
    ///     engine is a standalone leaf (#436), and the engine's copy of the
    ///     cell vocabulary is the source of truth (#1621).
    /// </summary>
    [Test]
    public async Task The_Implementation_Does_Not_Name_The_Abstraction()
    {
        List<string> offenders = DiffEncoderBoundaryProbe.FilesNaming(
            ImplementationProject,
            "Harbor.Ui.Framework");

        await Assert.That(offenders).IsEmpty()
            .Because(
                "the engine is a standalone leaf: it declares zero Harbor references and zero "
                + "Harbor imports, which `CellForgeEngineAtomicityRules` guards for the whole "
                + "tree. This rule states the same boundary for the two packages the bridge "
                + "joins: the engine's copy of the cell vocabulary is the source of truth, and a "
                + "file that reached back into the shared layer would make the two copies depend "
                + "on each other. Offenders: " + string.Join(" | ", offenders));
    }

    /// <summary>
    ///     Rule 3: the diff-directive table is not carried by a numeric cast.
    ///     The cast is the shape that hid the drift, so it is banned.
    /// </summary>
    [Test]
    public async Task No_Numeric_Cast_Carries_The_Diff_Directive_Table()
    {
        List<string> offenders = DiffEncoderBoundaryProbe.NumericCastSites();

        await Assert.That(offenders).IsEmpty()
            .Because(
                "the two diff-directive enums are separate types in separate assemblies, so "
                + "`(CellDiffOption)(byte)` is a claim that they declare the same members in the "
                + "same order, and nothing in the compiler checks the claim. The table in "
                + "`EngineFrameSource.MapDiffOption` spells the mapping out member by member, and "
                + "a numeric cast anywhere in the product is a second, unchecked way to say the "
                + "same thing. Offenders: " + string.Join(" | ", offenders));
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The matcher is handed three lines: one that
    ///     carries the banned cast in each of its two shapes, and one that
    ///     does not. It must report the first two and ignore the third.
    /// </summary>
    [Test]
    public async Task NonVacuity_The_Scanner_Finds_A_Planted_Violation_And_Ignores_A_Clean_Line()
    {
        const string qualifiedCast = "dst.SetDiffOption(x, y, (UIR.CellDiffOption)(byte)src.GetDiffOption(x, y), 0);";
        const string unqualifiedCast = "var option = (CellDiffOption)(byte)raw;";
        const string explicitMapping = "dst.SetDiffOption(x, y, MapDiffOption(src.GetDiffOption(x, y)), 0);";

        await Assert.That(DiffEncoderBoundaryProbe.IsNumericDiffOptionCast(qualifiedCast)).IsTrue()
            .Because("the qualified cast is the shape #1519 removed, and the matcher must still find it");
        await Assert.That(DiffEncoderBoundaryProbe.IsNumericDiffOptionCast(unqualifiedCast)).IsTrue()
            .Because("the unqualified cast is the same hazard, and the matcher must find it too");
        await Assert.That(DiffEncoderBoundaryProbe.IsNumericDiffOptionCast(explicitMapping)).IsFalse()
            .Because("the explicit mapping is what the bridge uses now; a matcher that flagged it "
                   + "would make the rule unsatisfiable");
    }
}
