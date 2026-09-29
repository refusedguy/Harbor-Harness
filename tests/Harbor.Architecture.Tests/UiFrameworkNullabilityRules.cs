// UiFrameworkNullabilityRules.cs — guard for the `null!` wave in src/Harbor.Ui.Framework*.
//
// The TEA state layer is 0/157 files on Result, and that is correct: a reducer
// cannot fail, so a Result in a transition would be a lie about an impossibility.
// What the layer was NOT free of was the other nullable lie — a member declared
// non-nullable that is initialised with the null-forgiving operator, so the
// compiler is asked to accept a value nobody assigned.
//
// Six of those lived in one class, ProjectionCache in
// src/Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs. They are
// now `required`, which makes the compiler state the invariant instead of a
// comment. This file stops the next contributor from putting one back.
//
// SCOPE. Deliberately src/Harbor.Ui.Framework* only. The same pattern exists
// elsewhere in the repo (HarborCompositionContext, IAgent.State) and those are
// owned by their own issues — widening this gate would make it red on a tree this
// PR is not changing.
//
// WHAT IS ALLOWED, and why.
//
//   * `default!` is NOT matched. It is a different idiom with a legitimate use:
//     StoreSubscriberViewModel.Selector<T>._last holds a `default(T)` sentinel
//     behind a `_has` flag, and reads it only when the flag is set. Converting
//     that to Maybe<T> would add an allocation per selector per frame to model an
//     absence that is already impossible. That is a judgement call recorded once
//     here rather than re-litigated at every site.
//
//   * The literal `null!` inside a comment or a doc comment is not matched. A
//     contributor documenting *why* they did not use `null!` must not fail this
//     gate, so comments are stripped before matching. String literals are not
//     stripped — the risk of a false positive there is smaller than the risk of
//     writing a comment-stripper with a string-literal state machine.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Asserts that no file under <c>src/Harbor.Ui.Framework*/</c> suppresses a
///     nullable warning with the <c>null</c> literal plus the null-forgiving
///     operator.
/// </summary>
/// <remarks>
///     <para>
///         The rule is a source scan rather than a reflection test because
///         <c>null!</c> compiles away: by the time an assembly is loaded there is
///         nothing left to inspect, so the only way to see the operator is the
///         text. It therefore reuses <see cref="RepoPaths" />, the same
///         repo-root discovery the README gate uses, and degrades to "nothing to
///         check" when the marker file is absent (a published test host).
///     </para>
///     <para>
///         Runs as part of the build via the HarborArchitectureGate target in
///         Directory.Build.props, so a re-introduced <c>null!</c> fails CI without
///         anyone remembering to run this project.
///     </para>
/// </remarks>
public class UiFrameworkNullabilityRules
{
    /// <summary>
    ///     Matches the <c>null!</c> token pair. The leading group rejects a
    ///     preceding word character so an identifier such as <c>xNull!</c> cannot
    ///     match; the trailing group rejects a following word character so
    ///     documentation like <c>null!x</c> cannot either.
    /// </summary>
    private static readonly Regex NullForgivingOnNull = new(@"(?<!\w)null!(?!\w)", RegexOptions.Compiled);

    /// <summary>Matches one or more consecutive single-line comments.</summary>
    private static readonly Regex LineComment = new(@"//[^\n]*", RegexOptions.Compiled);

    /// <summary>Matches a /* … *&#47; block, including the newlines it spans.</summary>
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Every <c>.cs</c> file under the TEA state layer, sorted for a stable failure message.</summary>
    private static IReadOnlyList<string> EnumerateUiFrameworkSources()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string src = Path.Combine(root, "src");
        if (!Directory.Exists(src))
        {
            return [];
        }

        var found = new List<string>();
        foreach (string dir in Directory.GetDirectories(src, "Harbor.Ui.Framework*"))
        {
            found.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
        }

        // Never descend into build output — a stale obj/ copy would be counted as
        // a second occurrence of every violation.
        return
        [
            .. found.Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                  .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     Blanks out one block-comment match, preserving every newline so line numbers
    ///     computed downstream still point at the right source line.
    /// </summary>
    private static string BlankOutComment(Match match)
    {
        var blank = new char[match.Length];
        for (int i = 0; i < match.Length; i++)
        {
            blank[i] = match.Value[i] == '\n' ? '\n' : ' ';
        }

        return new string(blank);
    }

    /// <summary>
    ///     Strips comments so documentation about the rule cannot trip it.
    ///     <see cref="LineComment" /> runs last because it cannot span a line.
    /// </summary>
    private static string StripComments(string source) =>
        LineComment.Replace(BlockComment.Replace(source, BlankOutComment), " ");

    /// <summary>
    ///     No file under <c>src/Harbor.Ui.Framework*/</c> initialises a field or
    ///     property with <c>null!</c>.
    /// </summary>
    /// <remarks>
    ///     Declared <c>required</c> when the member is genuinely always assigned —
    ///     that is what this file removed. Use <c>Maybe&lt;T&gt;</c> when absence is
    ///     real and the consumer must branch, and a plain <c>T?</c> when there is a
    ///     working default. Only a member that is assigned exactly once, at a
    ///     construction site the compiler can see, belongs in <c>required</c>.
    /// </remarks>
    [Test]
    public async Task Assert_NoNullForgivingNullInUiFramework()
    {
        var violations = new List<string>();
        string? root = RepoPaths.RepoRoot;

        foreach (string file in EnumerateUiFrameworkSources())
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            // Split after stripping, so a line number here indexes the real source.
            string[] lines = StripComments(source).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!NullForgivingOnNull.IsMatch(lines[i]))
                {
                    continue;
                }

                string rel = root is null ? file : Path.GetRelativePath(root, file);
                violations.Add($"{rel}({i + 1}): {lines[i].Trim()}");
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "src/Harbor.Ui.Framework* must not suppress nullable warnings with `null!`. "
                + "Use `required` when the member is always assigned at a visible construction site, "
                + "`Maybe<T>` when absence is real and the consumer must branch, or `T?` when there is "
                + "a working default. See ProjectionCache in DefaultUiProjector.cs for the worked example. "
                + $"Violations: {(violations.Count == 0 ? "(none)" : string.Join(", ", violations))}");
    }

    /// <summary>
    ///     The slice is discoverable at all — i.e. <see cref="RepoPaths.RepoRoot" />
    ///     resolved and at least one <c>src/Harbor.Ui.Framework*</c> directory exists.
    /// </summary>
    /// <remarks>
    ///     Without this, a broken repo-root discovery would make the gate above
    ///     vacuously green, which is the failure mode a ratchet has to guard
    ///     against.
    /// </remarks>
    [Test]
    public async Task Assert_UiFrameworkSliceIsDiscoverable()
    {
        var sources = EnumerateUiFrameworkSources();

        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("Harbor.slnx must sit above the test host, or the null-forgiving gate checks nothing.");
        await Assert.That(sources.Count).IsGreaterThan(100)
            .Because($"expected the full TEA slice (9 projects, >100 files); found {sources.Count}.");
    }
}
