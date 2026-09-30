// AnalyzerSeverityScopeRules.cs — #838: the DI severity model has a per-PATH
// layer, and nothing owned the gap between that layer and the document which
// claims to summarise it. RED BY CONSTRUCTION in the commit that adds it.
//
// WHAT #838 ACTUALLY WAS
// ----------------------
// The issue asked a two-way question — "is `apps/` outside the analyzer's
// perimeter, or does DI006 have a hole in the shape of a static auto-property?"
// — and the answer is NEITHER, which is why the question had to be asked by
// reading configuration rather than by running a build.
//
//   * `apps/` is NOT outside the perimeter. `/Directory.Build.props` references
//     DependencyInjection.Lifetime.Analyzers solution-wide and
//     `apps/Directory.Build.props` imports it, so the package and the root
//     `.editorconfig` both reach the app projects.
//   * DI006 has NO hole in that shape. The README shipped in the pinned package
//     (2.18.24, read out of the restored nupkg) puts the exact form in its
//     "Problem" block:
//
//         public static class Locator
//         {
//             public static IServiceProvider Provider { get; set; } = null!;
//
//     so a static auto-property typed `IServiceProvider` is in scope, and #837
//     saw the rule fire as `error DI006` on that same shape in the CLI.
//   * The third thing is the one nobody had looked for: `.editorconfig` has a
//     PATH-SCOPED block that demotes DI003/DI006/DI008/DI014 to `suggestion`
//     for the desktop composition roots. `apps/Harbor.App.Avalonia/App.axaml.cs`
//     is inside that block, `suggestion` is not promoted by
//     `TreatWarningsAsErrors`, and `dev` is green. That is the whole
//     explanation, and it is a deliberate, written policy — not a hole.
//
// So the defect is a DOCUMENTATION defect wearing the costume of an enforcement
// one, and it is the same defect twice over: `docs/ANALYZERS.md` said
// "All other DI rules → warning" while seven of them are `suggestion`, and it
// summarised the rule half of the severity model while omitting the path half.
// A reader who trusted it would correctly conclude that DI006 "did not stop"
// `App.axaml.cs` and go looking for a hole in the rule.
//
// WHAT THIS FILE RULES
// --------------------
//   1. Every path-scoped section in `.editorconfig` must resolve to something
//      in the checkout. Measured on `dev` at #838: FOUR blocks scoped DI rules
//      to `apps/Harbor.App.{Avalonia,Wpf,Maui,Blazor}`, and three of those
//      directories DO NOT EXIST — the Wpf/Maui/Blazor roots live under
//      `contrib/`, which is not in `Harbor.slnx` and is not built by CI. Those
//      three blocks were inert configuration that read like live policy: a
//      future author could have re-added a `Harbor.App.Blazor` and inherited a
//      four-rule DI relaxation nobody chose for it, with no diagnostic anywhere.
//   2. Every path that `.editorconfig` scopes a DI severity for must be NAMED in
//      `docs/ANALYZERS.md`. This is the guard that closes the #838 misdiagnosis
//      itself: the demotion is a real, deliberate exception, so the honest place
//      for it is the document that says what the severities are — and that is
//      what makes "DI006 did not fire here" legible as policy rather than as a
//      gap.
//   3. The set of DI-scoped paths is pinned to the declared list, so widening
//      the relaxation is a reviewable edit rather than a silent append. Same
//      argument as `StoredLocatorInventory_IsExactlyTheDeclaredBaseline`: a
//      number in an assertion, not an assumption.
//   4. Non-vacuity, below.
//
// NON-VACUITY
// -----------
// A rule that reports nothing is indistinguishable from a rule that is broken,
// and a broken guard is worse than none because it is believed. Two controls:
//   * `NonVacuity_SeparatesPathSectionsFromGlobSections` hands the scanner the
//     two real path sections that DO exist and the four whole-tree / glob
//     sections (`[*]`, `*.{cs,csx}`, `*.{md,markdown}`, `Makefile`), and
//     requires the first pair to be found and the second group to be rejected.
//     A scanner that returned nothing, or that mistook a glob for a path, fails
//     here rather than passing the sweep forever.
//   * `DiSeverityOverrides_AreScopedToExactlyTheDeclaredRoot` requires a
//     non-empty discovered set, so the sweep cannot be satisfied by an empty
//     parse on the day the `[apps/…]` block is renamed or moved.
//
// SCOPE, AND ITS LIMITS — STATED, NOT HIDDEN
// ------------------------------------------
// The section scanner is textual: it reads `[…]` headers and tracks which body
// lines belong to each. It does not evaluate editorconfig glob semantics, and
// it does not need to — the anchor it keeps is the literal directory or file
// prefix in front of the first glob character, which is the part a human reads
// to decide "does this path exist?". Three consequences, all deliberate:
//   * `[*]`, `[*.{cs,csx}]`, `[{a,b}]` and `[Makefile]` are NOT path sections.
//     A section with a comma is a multi-pattern section and is skipped; a
//     section whose anchor would be empty (the pattern starts with a glob) is
//     skipped; a section with no `/` is not a repo path and is skipped, which is
//     why `[Makefile]` is excluded even though that file does exist.
//   * A path section whose anchor exists but matches nothing is INVISIBLE to
//     this rule. `[apps/Harbor.App.Avalonia/**.cs]` resolves to a real directory
//     and says nothing about whether a `.cs` file is behind the glob. This rule
//     guards the configuration addressing a real path, not the glob's reach.
//   * It reads the working tree, so — like every other `RepoPaths` consumer —
//     it degrades to "nothing to check" outside a checkout rather than failing
//     the Release arch gate. `RequireRoot` makes that degradation LOUD: a
//     missing repo root throws with the reason instead of returning an empty
//     set that would read as a clean bill of health.
//
// WHAT WAS AND WAS NOT VERIFIED
// -----------------------------
// Verified by reading: the `.editorconfig` blocks, the shipped 2.18.24 README,
// the absence of `apps/Harbor.App.{Wpf,Maui,Blazor}`, the presence of the three
// roots under `contrib/`, and that `Harbor.slnx` lists only
// `apps/Harbor.App.Cli`.
//
// NOT verified: no build was run for #838, so the size of the DI006 diagnostic
// at `App.axaml.cs:33` was never measured. The demotion is an inference from
// configuration plus a green `dev`, and it is a complete one — the shape is in
// the rule's own "Problem" block, and `suggestion` is not promoted — but it is
// an inference. `TreatWarningsAsErrors` promotes warnings, not suggestions, and
// nothing else in that project's `NoWarn` list (`NU1903`, `CS0169`, `CS0414`,
// `CA1823`, `MA0046`, `S3267`, `S125`) mentions a DI rule.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     One <c>[…]</c> section of <c>.editorconfig</c>: the literal header, the
///     repo path it anchors to (null when the header is a glob or a bare file
///     name), and the body lines that follow it.
/// </summary>
internal sealed record EditorConfigSection(string Pattern, string? Anchor, IReadOnlyList<string> Body);

/// <summary>
///     Owns the per-path layer of the analyzer severity model: the
///     <c>.editorconfig</c> sections that scope a DI rule's severity to a
///     directory rather than to the tree, and the documentation that has to
///     account for them. See the file header for #838 and for the limits.
/// </summary>
public sealed class AnalyzerSeverityScopeRules
{
    private const string EditorConfigRelativePath = ".editorconfig";
    private const string AnalyzerDocRelativePath = "docs/ANALYZERS.md";

    /// <summary>
    ///     The paths `.editorconfig` is allowed to scope a DI severity to.
    ///     Measured on `dev` at #838, after the three dead blocks were removed:
    ///     one — the Avalonia desktop composition root, which is the only
    ///     desktop root in the checkout. Adding a second is an argument to make
    ///     in review, not a line to append.
    /// </summary>
    private static readonly string[] DeclaredDiScopedRoots = ["apps/Harbor.App.Avalonia"];

    /// <summary>Glob characters that end the literal path prefix of a section header.</summary>
    private const string GlobCharacters = "*?{";

    /// <summary>
    ///     A <c>dotnet_diagnostic.DI###.severity = …</c> assignment — i.e. a
    ///     member of the DI-lifetime family this file is about. The Excubo EDI
    ///     rules and the TUnit reservations are scoped to paths too, and they are
    ///     out of scope here: the doc-coverage rule is about the DI severity
    ///     table, not about every reservation in the file.
    /// </summary>
    private static readonly Regex DiSeverityAssignment =
        new(@"^dotnet_diagnostic\.(?<id>DI\d{3})\.severity\s*=\s*(?<severity>\S+)\s*$");

    // =====================================================================
    // 1. The configuration half: no section may address a path that is not there.
    // =====================================================================

    /// <summary>
    ///     Every path-scoped <c>.editorconfig</c> section resolves to a real file
    ///     or directory in the checkout. This is the test that fails on `dev` at
    ///     #838: the <c>apps/Harbor.App.{Wpf,Maui,Blazor}</c> blocks address
    ///     composition roots that live under <c>contrib/</c> and are not built by
    ///     CI, so nothing they contain has any effect and nothing reports it.
    /// </summary>
    [Test]
    public async Task PathScopedSections_ResolveToRealPaths()
    {
        string root = RequireRoot();

        var dead = new List<string>();
        foreach (EditorConfigSection section in PathScopedSections())
        {
            if (Exists(root, section.Anchor!))
            {
                continue;
            }

            var relaxed = new List<string>();
            foreach (string line in section.Body)
            {
                Match match = DiSeverityAssignment.Match(line);
                if (match.Success)
                {
                    relaxed.Add($"{match.Groups["id"].Value}={match.Groups["severity"].Value}");
                }
            }

            dead.Add($"[{section.Pattern}] anchors on '{section.Anchor}', which is not in the "
                   + $"checkout, so {(relaxed.Count == 0 ? "the whole section" : string.Join(", ", relaxed))} "
                   + "is inert");
        }

        await Assert.That(dead.ToArray()).IsEmpty()
            .Because(".editorconfig sections that scope severities to a path that does not exist "
                   + "cannot fail anything and read like live policy — delete the section, or point it "
                   + "at the real path: " + string.Join(" | ", dead));
    }

    // =====================================================================
    // 2. The documentation half: the doc must account for the path layer.
    // =====================================================================

    /// <summary>
    ///     Every path <c>.editorconfig</c> scopes a DI severity for is named in
    ///     <c>docs/ANALYZERS.md</c>. This is the test that fails on `dev` at
    ///     #838, and it is the one that closes the misdiagnosis rather than the
    ///     symptom: the Avalonia root's four-rule relaxation is real and
    ///     deliberate, so its only correct home is the document that states what
    ///     the severities are. While it is missing, a reader of that document has
    ///     no way to tell "DI006 is a build error" from "DI006 is a suggestion in
    ///     the one place you are looking".
    /// </summary>
    [Test]
    public async Task AnalyzerDoc_NamesEveryPathScopedDiOverride()
    {
        string doc = ReadRepoFile(AnalyzerDocRelativePath);

        var unnamed = new List<string>();
        foreach (EditorConfigSection section in DiScopedSections())
        {
            if (doc.Contains(section.Anchor!, StringComparison.Ordinal))
            {
                continue;
            }

            var relaxed = new List<string>();
            foreach (string line in section.Body)
            {
                Match match = DiSeverityAssignment.Match(line);
                if (match.Success)
                {
                    relaxed.Add(match.Groups["id"].Value);
                }
            }

            unnamed.Add($"'{section.Anchor}' ({string.Join(", ", relaxed)})");
        }

        await Assert.That(unnamed.ToArray()).IsEmpty()
            .Because("a path-scoped DI relaxation the analyzer document never mentions is invisible "
                   + "to every reader of the severity table, and its absence reads as an enforcement "
                   + "gap rather than as policy — add a section naming it in "
                   + AnalyzerDocRelativePath + ": " + string.Join(" | ", unnamed));
    }

    // =====================================================================
    // 3. The inventory: the relaxation surface is a number, not an assumption.
    // =====================================================================

    /// <summary>
    ///     The set of paths <c>.editorconfig</c> scopes a DI severity to is
    ///     exactly the declared list. Non-vacuous in its own right: the
    ///     discovered set must be non-empty, so this cannot be satisfied by a
    ///     parse that finds nothing on the day the block is renamed or moved.
    /// </summary>
    [Test]
    public async Task DiSeverityOverrides_AreScopedToExactlyTheDeclaredRoot()
    {
        string[] discovered =
        [
            .. DiScopedSections()
                .Select(section => section.Anchor!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        await Assert.That(discovered.Length).IsGreaterThan(0)
            .Because("an empty discovered set means the section scanner is not reading the "
                   + "DI-scoped block at all, which would make the equality below vacuous");

        await Assert.That(discovered).IsEquivalentTo(DeclaredDiScopedRoots)
            .Because("the paths that relax a DI rule are an inventory, not a default: adding one is a "
                   + "reviewable decision with a written reason, so the declared list is the place it "
                   + "belongs");
    }

    // =====================================================================
    // 4. Non-vacuity: the scanner separates the two classes of section.
    // =====================================================================

    /// <summary>
    ///     The scanner finds the path sections that really exist and rejects the
    ///     whole-tree and file-glob ones. Without this, a scanner that returned
    ///     an empty set would pass tests 1–3 forever, and a scanner that treated
    ///     <c>[*.{cs,csx}]</c> as a path would fail test 1 on a section nobody
    ///     can delete.
    /// </summary>
    [Test]
    public async Task NonVacuity_SeparatesPathSectionsFromGlobSections()
    {
        HashSet<string> anchors =
        [
            .. PathScopedSections().Select(section => section.Anchor!)
        ];

        foreach (string real in
                 [
                     "apps/Harbor.App.Avalonia",
                     "tests/Harbor.Tui.RendererTests/NickConsoleExGoldenFrameTests.cs"
                 ])
        {
            await Assert.That(anchors.Contains(real, StringComparer.Ordinal)).IsTrue()
                .Because($"'{real}' is a real path section in {EditorConfigRelativePath}; if the scanner "
                       + "cannot find it, it is not reading the file and every other test here is vacuous");
        }

        foreach (string notAPath in ["*", "*.{cs,csx}", "*.{md,markdown}", "Makefile"])
        {
            await Assert.That(anchors.Contains(notAPath, StringComparer.Ordinal)).IsFalse()
                .Because($"'{notAPath}' is a whole-tree glob or a bare file name, not a repo path; "
                       + "classifying it as one would make PathScopedSections_ResolveToRealPaths fail "
                       + "on a section that cannot be deleted");
        }
    }

    // =====================================================================
    // Discovery
    // =====================================================================

    /// <summary>Every section whose header anchors to a repo-relative path.</summary>
    private static IReadOnlyList<EditorConfigSection> PathScopedSections() =>
        [.. ParseSections(ReadRepoFile(EditorConfigRelativePath)).Where(s => s.Anchor is not null)];

    /// <summary>Every path section that carries at least one DI severity assignment.</summary>
    private static IReadOnlyList<EditorConfigSection> DiScopedSections() =>
    [
        .. PathScopedSections().Where(s => s.Body.Any(line => DiSeverityAssignment.IsMatch(line)))
    ];

    /// <summary>
    ///     Splits <c>.editorconfig</c> into sections. A header is a trimmed line
    ///     that both starts and ends with a bracket; everything up to the next
    ///     header is that section's body.
    /// </summary>
    private static IReadOnlyList<EditorConfigSection> ParseSections(IReadOnlyList<string> lines)
    {
        var sections = new List<EditorConfigSection>();
        var body = new List<string>();
        string? pattern = null;

        foreach (string raw in lines)
        {
            string line = raw.Trim();

            if (line.Length > 1 && line[0] == '[' && line[^1] == ']')
            {
                if (pattern is not null)
                {
                    sections.Add(new EditorConfigSection(pattern, AnchorOf(pattern), [.. body]));
                }

                pattern = line[1..^1].Trim();
                body = [];
                continue;
            }

            if (pattern is not null)
            {
                body.Add(line);
            }
        }

        if (pattern is not null)
        {
            sections.Add(new EditorConfigSection(pattern, AnchorOf(pattern), [.. body]));
        }

        return sections;
    }

    /// <summary>
    ///     The literal repo path a section header addresses, or <c>null</c> when
    ///     it addresses no single path. See the header for the three exclusions:
    ///     multi-pattern sections, patterns that begin with a glob, and headers
    ///     with no directory separator.
    /// </summary>
    private static string? AnchorOf(string pattern)
    {
        if (pattern.Contains(',') || !pattern.Contains('/'))
        {
            return null;
        }

        int glob = pattern.IndexOfAny(GlobCharacters.ToCharArray());
        string anchor = (glob < 0 ? pattern : pattern[..glob]).TrimEnd('/');

        return anchor.Length == 0 ? null : anchor;
    }

    // =====================================================================
    // Filesystem
    // =====================================================================

    private static bool Exists(string root, string relativePath)
    {
        string absolute = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(absolute) || File.Exists(absolute);
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RequireRoot(), relativePath));

    /// <summary>
    ///     The repository root, or a loud failure. These tests inspect files in
    ///     the checkout; degrading to an empty result outside one would report
    ///     "no violations" on the strength of having read nothing.
    /// </summary>
    private static string RequireRoot() =>
        RepoPaths.RepoRoot ?? throw new InvalidOperationException(
            "[analyzer-scope] repository root not found. This guard walks the working tree; with no "
            + "Harbor.slnx above the test host it has nothing to read, and reporting 'no violations' "
            + "would make it vacuous.");
}
