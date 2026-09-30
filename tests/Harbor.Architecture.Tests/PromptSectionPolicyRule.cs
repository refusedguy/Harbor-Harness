// PromptSectionPolicyRule.cs — GUARD.
//
// THE CONVENTION BEING ENFORCED
// ------------------------------
// A prompt-assembly method (one that appends `## ` Markdown section headers into
// a StringBuilder) must not carry a bare, non-zero numeric bound inside a
// conditional. Such a bound is a POLICY decision about what the model is shown
// — "at most three guidelines", "skip anything over 160 chars" — and a policy
// spelled as `n >= 3` cannot be named by a test, so nothing can hold it in
// place. Raising the cap to 30 or the length ceiling to 1600 changes what the
// model reads, and every test in the repository still passes.
//
// WHY THE BRANCH SELECTORS ARE NOT THE PROBLEM
// ---------------------------------------------
// It is tempting to read this as "the builder has too many branches". The
// selectors are the OPPOSITE of the defect: every one of them is
// `if (<some context component>.Count == 0)` — the same requirement, "render
// this section when it has content", expressed per section. They are stages of
// ONE path, not competing modes, and each already has a test naming it (the six
// sections of SystemPromptBuilder are covered by SystemPromptBuilderTests and
// SystemPromptSupervisionTests). Collapsing them or splitting them into
// separately-named requirements would be churn. The bodies are where the
// untested decisions live.
//
// WHY "## " IS THE SCOPE SIGNAL
// -----------------------------
// A hand-written list of prompt-assembly files would age exactly like the
// tables #578 was opened over, and would miss the next builder. The
// `## `-header test is a property of the CODE: these strings are a contract
// (the prompt tests parse for them, and consumers anchor on them — see the
// "rendered even when the tool list is empty so consumers can rely on the
// anchor" note in SystemPromptBuilder). So the probe finds the methods by what
// they emit, and a new builder is covered the moment it emits its first header.
//
// A prompt assembler that emits no `## ` header is out of scope by
// construction, and that is a real boundary rather than an oversight:
// CompactionService.BuildSummarizationPrompt writes `<conversation>` framing for
// a summariser, not a sectioned document, and carries no section-policy bound.
//
// ZERO IS EXEMPT
// --------------
// `Count > 0` / `Count == 0` are presence gates, and every section needs one.
// Exempting 0 is what keeps this rule from blocking the first new section — the
// thing a "method no longer than N lines" guard would have forbidden outright.
// Only a NON-ZERO bound is a policy, and only a policy needs a name.
//
// PERIMETER
// ---------
// `src/` only. `contrib/` is uncompiled and outside support by owner decision;
// `apps/` composes, it does not assemble prompts. Files under `tests/` are
// excluded because a guard that graded its own fixtures would be grading its
// own positive control.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the canonical file really is in the scan and its
//      assembler really is found, so a probe whose method detection stopped
//      working cannot report "no violations" and go green.
//   2. NonVacuity_Scan_DetectsABareBoundInSyntheticSource — the POSITIVE
//      CONTROL. The probe is handed synthetic source holding a bare `>= 3` under
//      a `## ` header and MUST report it, plus a clean snippet that names its
//      bound and one whose only literals are presence gates, and MUST report
//      neither of the latter two.

using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One bare numeric bound the probe found inside a prompt assembler.</summary>
/// <param name="File">Repo-relative path, forward slashes.</param>
/// <param name="Line">1-based line of the <c>if</c>.</param>
/// <param name="Method">The enclosing method's name, or "(unknown)".</param>
/// <param name="Bound">The literal as spelled, e.g. <c>3</c> or <c>160</c>.</param>
/// <param name="Text">The offending line, trimmed.</param>
internal sealed record PromptPolicySite(
    string File,
    int Line,
    string Method,
    string Bound,
    string Text);

/// <summary>Everything the rule needs from one repository scan.</summary>
/// <param name="Assemblers">Prompt-assembly methods found, as <c>file:method</c>.</param>
/// <param name="BareBounds">Bare non-zero bounds inside them.</param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
internal sealed record PromptPolicyScan(
    IReadOnlyList<string> Assemblers,
    IReadOnlyList<PromptPolicySite> BareBounds,
    int FilesScanned);

/// <summary>Finds prompt-assembly methods and the bare bounds inside them.</summary>
internal static partial class PromptSectionPolicyProbe
{
    /// <summary>
    /// The file whose assembler this rule was opened over. The scan does not
    /// depend on it — the <c>## </c> signal does the work — but the non-vacuity
    /// check names it so a probe that finds nothing fails loudly instead of
    /// reporting an empty violation list.
    /// </summary>
    internal const string CanonicalFile =
        "src/Harbor.Application/Sessions/SystemPromptBuilder.cs";

    /// <summary>Repository roots the scan walks. <c>contrib/</c> and <c>apps/</c> are excluded.</summary>
    private static readonly string[] ScanRoots = ["src"];

    /// <summary>Directory names never descended into during the scan.</summary>
    private static readonly FrozenSet<string> SkippedDirectories =
        new[] { "bin", "obj", ".worktrees", "node_modules" }
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Walks the repository. A missing checkout scans nothing, which the rule reports as a failure.</summary>
    internal static PromptPolicyScan Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new PromptPolicyScan([], [], 0);
        }

        var assemblers = new List<string>();
        var bounds = new List<PromptPolicySite>();
        int scanned = 0;

        foreach (string file in EnumerateSources(repoRoot))
        {
            string relative = MakeRelative(repoRoot, file);
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            scanned++;
            ScanSource(relative, lines, assemblers, bounds);
        }

        return new PromptPolicyScan(assemblers, bounds, scanned);
    }

    /// <summary>
    /// Scans already-read lines. Exposed so the positive control drives the REAL
    /// matcher (comment stripping and brace-depth method detection included)
    /// instead of a second implementation of it, which is the only way "it can
    /// fail" means anything.
    /// </summary>
    internal static void ScanSource(
        string relativeFile,
        string[] lines,
        List<string> assemblers,
        List<PromptPolicySite> bounds)
    {
        string[] clean = SourceCommentStripper.StripAll(lines);
        int n = clean.Length;
        if (n == 0)
        {
            return;
        }

        // Brace depth BEFORE each line. A method body is the run of lines whose
        // depth is one below the depth at its signature, so this is what lets a
        // header found at line i be attributed to the method that owns it
        // instead of to whatever method happens to precede it in the file.
        var depthBefore = new int[n];
        int depth = 0;
        for (int i = 0; i < n; i++)
        {
            depthBefore[i] = depth;
            depth += clean[i].Count(c => c == '{') - clean[i].Count(c => c == '}');
        }

        // Pass 1: which methods are prompt assemblers? A method qualifies when
        // any line inside it appends a `## ` header. The RANGE is what is
        // collected, not the header line, because a section is commonly emitted
        // once per arm of a conditional — `## Available Tools` is written twice
        // in SystemPromptBuilder — and grading per header line would report the
        // same bound two or three times over.
        var scanned = new HashSet<(int Start, int End)>();

        for (int i = 0; i < n; i++)
        {
            if (!clean[i].Contains(SectionHeaderSignal, StringComparison.Ordinal))
            {
                continue;
            }

            int headerDepth = depthBefore[i];

            // The signature is the nearest line ABOVE the header's nesting level
            // that looks like a method declaration. It has to be found this way
            // rather than by scanning forward from the method's opening brace,
            // because a section header is often emitted from inside an if/else
            // arm, and a forward scan from the brace lands in the arm and
            // reports "(unknown)" for the very file this rule was opened over.
            int signature = -1;
            for (int j = i; j >= 0; j--)
            {
                if (depthBefore[j] >= headerDepth)
                {
                    continue;
                }

                if (MethodSignature().IsMatch(clean[j]))
                {
                    signature = j;
                }
            }

            // The method runs to the first line back at (or above) the
            // signature's own depth — the method's closing brace sits AT that
            // depth, since the brace that opened the body is what raised it.
            // With no signature found the scan covers the rest of the file,
            // which can only over-report: the safe direction for a guard.
            int bodyDepth = signature < 0 ? int.MaxValue : depthBefore[signature];
            int end = MethodEnd(depthBefore, i, bodyDepth);
            int start = signature < 0 ? 0 : signature;

            string method = signature < 0 ? "(unknown)" : MethodNameOf(clean[signature]);
            string key = $"{relativeFile}:{method}";
            if (!assemblers.Contains(key, StringComparer.Ordinal))
            {
                assemblers.Add(key);
            }

            if (!scanned.Add((start, end)))
            {
                continue;
            }

            // Pass 2: grade this method's body exactly once.
            for (int k = start; k <= end && k < n; k++)
            {
                foreach (string literal in BareBoundsOn(clean[k]))
                {
                    bounds.Add(new PromptPolicySite(
                        relativeFile,
                        k + 1,
                        method,
                        literal,
                        clean[k].Trim()));
                }
            }
        }
    }

    /// <summary>
    /// A section header is a <c>## </c> inside a string literal. Prose in a
    /// comment cannot reach here (comments are stripped first), and the file's
    /// own XML docs quote these headers as <c>&lt;c>## Available Tools</c></c>,
    /// which the stripper also removes — so this is a statement about emitted
    /// text, not about documentation.
    /// </summary>
    private const string SectionHeaderSignal = "\"## ";

    /// <summary>
    /// Last line of the method that owns <paramref name="headerLine" />: the
    /// first line at or back above the signature's depth. The method's own
    /// closing brace sits AT the signature's depth (the brace that opened the
    /// body is what raised it), so <c>&lt;=</c> is what stops the walk there
    /// rather than one line past it.
    /// </summary>
    private static int MethodEnd(int[] depthBefore, int headerLine, int bodyDepth)
    {
        for (int k = headerLine; k < depthBefore.Length; k++)
        {
            if (depthBefore[k] <= bodyDepth)
            {
                return k - 1;
            }
        }

        return depthBefore.Length - 1;
    }

    /// <summary>The declared name off a matched signature line.</summary>
    private static string MethodNameOf(string signatureLine) =>
        MethodSignature().Match(signatureLine).Groups["name"].Value;

    /// <summary>
    /// The non-zero integer literals this line compares against. Returns none
    /// for a line that is not a conditional, for a presence gate (the literal
    /// <c>0</c>), and for a loop header — a <c>for</c>/<c>while</c> bound is
    /// traversal, not presentation policy.
    /// </summary>
    private static IEnumerable<string> BareBoundsOn(string line)
    {
        string trimmed = line.TrimStart();

        // A loop bound is traversal, not presentation policy, and the `if (`
        // requirement already excludes `for`/`while`/`foreach` headers — none of
        // which open with it.
        if (!IfCondition().IsMatch(trimmed))
        {
            yield break;
        }

        // Two matchers, one per operand order, rather than one alternation
        // reusing the group name: .NET does accept a duplicate group name, but
        // relying on that buys nothing here and a [GeneratedRegex] failure is a
        // hard build error rather than a runtime surprise.
        foreach (Match match in RightHandBound().Matches(trimmed))
        {
            string? literal = UsableBound(match);
            if (literal is not null)
            {
                yield return literal;
            }
        }

        foreach (Match match in LeftHandBound().Matches(trimmed))
        {
            string? literal = UsableBound(match);
            if (literal is not null)
            {
                yield return literal;
            }
        }
    }

    /// <summary>
    /// The literal of <paramref name="match" /> when it is a bound this rule
    /// grades, and <c>null</c> when it is not. <c>null</c> rather than a bool so
    /// the two callers cannot forget the filter and let a <c>0</c> through.
    /// </summary>
    private static string? UsableBound(Match match)
    {
        string literal = match.Groups["num"].Value;

        // A leading zero is not a bound — it is padding in a literal, and a
        // slice bound is not what this rule grades.
        if (literal.Length > 1 && literal[0] == '0')
        {
            return null;
        }

        // The presence gate. A section needs `Count > 0`, and exempting zero is
        // what keeps this rule from blocking the next section added.
        return literal == "0" ? null : literal;
    }

    private static IEnumerable<string> EnumerateSources(string repoRoot)
    {
        foreach (string root in ScanRoots)
        {
            string absolute = Path.Combine(repoRoot, root);
            if (!Directory.Exists(absolute))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(absolute, "*.cs", SearchOption.AllDirectories))
            {
                if (SkippedDirectories.Any(dir =>
                        path.Contains(Path.DirectorySeparatorChar + dir + Path.DirectorySeparatorChar,
                            StringComparison.Ordinal)))
                {
                    continue;
                }

                yield return path;
            }
        }
    }

    /// <summary>
    /// A conditional. The trailing <c>(</c> is REQUIRED. Without it a string
    /// literal containing the word "if" would be graded as a branch, and the
    /// rendered tool guideline this rule is about is exactly the kind of prose
    /// that contains such a word.
    /// </summary>
    [GeneratedRegex(@"\bif\s*\(")]
    private static partial Regex IfCondition();

    /// <summary>
    /// An integer literal on the RIGHT of a comparison: <c>g.Length &gt; 160</c>.
    /// A leading <c>==</c>/<c>!=</c>/<c>&lt;=</c>/<c>&gt;=</c> is included, and so
    /// is a bare <c>&lt;</c>/<c>&gt;</c>, because both spell the same mistake.
    /// </summary>
    [GeneratedRegex(@"[<>!=]=?\s*(?<num>\d+)")]
    private static partial Regex RightHandBound();

    /// <summary>
    /// An integer literal on the LEFT of a comparison: <c>3 &lt;= n</c>. Same
    /// defect as <see cref="RightHandBound" />, written the other way round.
    /// </summary>
    [GeneratedRegex(@"(?<num>\d+)\s*[<>!=]=?")]
    private static partial Regex LeftHandBound();

    /// <summary>A method signature: an access modifier, then the name, then <c>(</c>.</summary>
    [GeneratedRegex(@"\b(?:public|private|protected|internal)\s+(?:static\s+|async\s+|override\s+|sealed\s+)*[\w<>\[\],.?]+\s+(?<name>\w+)\s*\(")]
    private static partial Regex MethodSignature();

    private static string MakeRelative(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>
///     Guard: a conditional inside a prompt-assembly method carries no bare
///     non-zero bound. Every such bound is a policy about what the model reads,
///     and a policy is only holdable if something can name it.
/// </summary>
public sealed class PromptSectionPolicyRule
{
    private static readonly Lazy<PromptPolicyScan> Report = new(
        () => PromptSectionPolicyProbe.Scan(RepoPaths.RepoRoot));

    // =====================================================================
    // 1. The rule.
    // =====================================================================

    /// <summary>
    ///     No prompt-assembly method compares against a bare non-zero integer.
    ///     A bound written as <c>n &gt;= 3</c> is invisible to a test, so
    ///     raising it to 30 changes what the model is shown and nothing goes
    ///     red. Name the bound and a test can hold it.
    /// </summary>
    [Test]
    public async Task PromptAssembler_HasNoBareNumericBoundInAConditional()
    {
        var offenders = Report.Value.BareBounds
            .Select(b => $"{b.File}:{b.Line} ({b.Method}) bound {b.Bound} — {b.Text}")
            .ToList();

        await Assert.That(offenders).IsEmpty()
            .Because(
                "a non-zero bound inside a prompt-assembly method is a PRESENTATION POLICY, not an "
                + "incidental number: it decides how much of each tool's guidance reaches the model. "
                + "Spelled inline it cannot be named, so no test can hold it — raising the cap from 3 "
                + "to 30, or the per-guideline length ceiling from 160 to 1600, changes what the model "
                + "reads and every test in the repository still passes. Declare it as a named constant "
                + "and assert it by name. Presence gates (Count > 0) are exempt: a section needs one, "
                + "and exempting 0 is what keeps this rule from blocking the next section added. "
                + "Offending sites: " + (offenders.Count == 0 ? "(none)" : string.Join("\n  ", offenders)));
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan really walked a checkout and really found the canonical
    ///     assembler. Without this a probe whose method detection broke would
    ///     report zero violations and the rule above would be satisfied by
    ///     having found nothing at all.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it reports zero violations "
                   + "and the rule is satisfied by having nothing to look at");

        PromptPolicyScan report = Report.Value;

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no .cs file under src/; the root or the file filter is wrong and "
                   + "every rule here is vacuously green");

        await Assert.That(string.Join(" | ", report.Assemblers))
            .Contains(PromptSectionPolicyProbe.CanonicalFile)
            .Because(
                "the scan found no prompt-assembly method in " + PromptSectionPolicyProbe.CanonicalFile
                + ", which is the file this rule was opened over. Detection is by the `## ` header the "
                + "assembler emits, so a miss means that signal stopped matching — not that the file is "
                + "clean. Found: "
                + (report.Assemblers.Count == 0 ? "(nothing)" : string.Join(" | ", report.Assemblers)));
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The probe is handed three synthetic snippets,
    ///     each a prompt assembler: one with a bare bound, one that names its
    ///     bound, one whose only literals are presence gates. It MUST report
    ///     the first and neither of the other two. A probe whose matchers
    ///     stopped matching reports nothing, and the rule goes green while
    ///     enforcing nothing.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scan_DetectsABareBoundInSyntheticSource()
    {
        const string bare = """
            private static void Assemble(StringBuilder builder, IReadOnlyList<string> guidelines)
            {
                builder.AppendLine("## Available Tools");
                int n = 0;
                foreach (string g in guidelines)
                {
                    if (n >= 3) break;
                    if (g.Length > 160) continue;
                    builder.Append("  - ").AppendLine(g);
                    n++;
                }
            }
            """;

        const string named = """
            private const int MaxGuidelinesPerTool = 3;
            private const int MaxGuidelineChars = 160;

            private static void Assemble(StringBuilder builder, IReadOnlyList<string> guidelines)
            {
                builder.AppendLine("## Available Tools");
                int n = 0;
                foreach (string g in guidelines)
                {
                    if (n >= MaxGuidelinesPerTool) break;
                    if (g.Length > MaxGuidelineChars) continue;
                    builder.Append("  - ").AppendLine(g);
                    n++;
                }
            }
            """;

        const string presenceOnly = """
            private static void Assemble(StringBuilder builder, IReadOnlyList<Tool> tools)
            {
                builder.AppendLine("## Available Tools");
                if (tools.Count == 0)
                {
                    builder.AppendLine("No tools available this turn.");
                }
            }
            """;

        // A prompt assembler is found BY its header. A method with a bare bound
        // but no `## ` section is out of scope by construction, and grading it
        // would mean the rule is really about arbitrary methods.
        const string noHeader = """
            private static string Truncate(string s)
            {
                if (s.Length > 160) return s[..160];
                return s;
            }
            """;

        var bareAssemblers = new List<string>();
        var bareSites = new List<PromptPolicySite>();
        PromptSectionPolicyProbe.ScanSource("src/Synthetic/Bare.cs", bare.Split('\n'),
            bareAssemblers, bareSites);

        var namedAssemblers = new List<string>();
        var namedSites = new List<PromptPolicySite>();
        PromptSectionPolicyProbe.ScanSource("src/Synthetic/Named.cs", named.Split('\n'),
            namedAssemblers, namedSites);

        var presenceAssemblers = new List<string>();
        var presenceSites = new List<PromptPolicySite>();
        PromptSectionPolicyProbe.ScanSource("src/Synthetic/Presence.cs", presenceOnly.Split('\n'),
            presenceAssemblers, presenceSites);

        var headerlessAssemblers = new List<string>();
        var headerlessSites = new List<PromptPolicySite>();
        PromptSectionPolicyProbe.ScanSource("src/Synthetic/Truncate.cs", noHeader.Split('\n'),
            headerlessAssemblers, headerlessSites);

        await Assert.That(bareAssemblers.Count).IsEqualTo(1)
            .Because("the first snippet emits a `## ` header, so it IS a prompt assembler and must be "
                   + "found. A miss means the `## ` signal stopped matching and rule 1 is passing "
                   + "because the probe finds nothing. Scanned: "
                   + (bareAssemblers.Count == 0 ? "(nothing)" : string.Join(" | ", bareAssemblers)));

        await Assert.That(string.Join(" | ", bareSites.Select(s => s.Bound))).IsEqualTo("3 | 160")
            .Because("the first snippet holds the exact defect this rule exists for: a cap of 3 and a "
                   + "160-char ceiling, both deciding what the model reads, both unnameable. Scanned: "
                   + (bareSites.Count == 0 ? "(nothing)" : string.Join(" | ", bareSites.Select(s => s.Bound))));

        await Assert.That(bareSites[0].Method).IsEqualTo("Assemble")
            .Because("the violation must be attributed to the method that owns the header, or a failure "
                   + "message names a location the reader cannot act on");

        await Assert.That(namedAssemblers.Count).IsEqualTo(1)
            .Because("the second snippet is a prompt assembler too, so the probe must still see it — "
                   + "otherwise rule 1 is green because the probe finds nothing rather than because the "
                   + "code is clean");

        await Assert.That(namedSites).IsEmpty()
            .Because("the second snippet is the shape the rule REQUIRES: the same two policy decisions "
                   + "declared as named constants. Rule 1 must not fire on it, or the rule is "
                   + "unsatisfiable. Scanned: "
                   + (namedSites.Count == 0 ? "(nothing)" : string.Join(" | ", namedSites.Select(s => s.Text))));

        await Assert.That(presenceAssemblers.Count).IsEqualTo(1)
            .Because("the third snippet emits a header and must be found as an assembler; it is the "
                   + "shape that proves the zero exemption is about the LITERAL and not about skipping "
                   + "assemblers");

        await Assert.That(presenceSites).IsEmpty()
            .Because("the third snippet's only literal is a presence gate (Count == 0). A section needs "
                   + "one, and this is what stops the rule from blocking the next section added. Scanned: "
                   + (presenceSites.Count == 0 ? "(nothing)" : string.Join(" | ", presenceSites.Select(s => s.Bound))));

        await Assert.That(headerlessAssemblers.Count).IsEqualTo(0)
            .Because("the fourth snippet holds a bare 160 but emits no `## ` section, so it is not a "
                   + "prompt assembler and the rule does not claim it. A probe that graded it would be "
                   + "grading arbitrary methods — and would be red on the moment this file is written");
    }
}
