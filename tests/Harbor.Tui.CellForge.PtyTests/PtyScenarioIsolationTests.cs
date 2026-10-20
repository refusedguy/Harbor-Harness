using System.Text;
using System.Text.RegularExpressions;
using Harbor.E2E.Framework;
using TUnit.Core.Interfaces;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     The guard for #1789: the CellForge PTY scenarios run in PARALLEL, and
///     that is only safe because nothing in this project is process-wide.
///     <para />
///     The project used to hold one <c>[NotInParallel("pty")]</c> key across
///     40 classes, which serialized the whole assembly: the wall time was the
///     sum of the durations and the parallelism factor was 1.00. The key was
///     removed because there was no shared resource behind it. Every scenario
///     spawns its own pseudo-terminal, its own child process, its own mock
///     server and its own temp <c>$HOME</c>.
///     <para />
///     This file exists so the removal cannot be undone by accident and so the
///     decision has a written shape. It is a guard, not a comment: each rule
///     below fails on the tree as it is, and the messages say what to do
///     instead.
///     <para />
///     The rules, and the reasoning behind each:
///     <list type="number">
///         <item>
///             <description>
///                 <b>No process-environment write.</b> A write to the process
///                 environment is process-wide, so every scenario that made one
///                 would have to hold a mutex against every other scenario that
///                 made one, and with unkeyed readers the repo's rule
///                 (<c>docs/TEST_PATTERNS.md</c> §Parallelism) demands the BARE
///                 <c>[NotInParallel]</c> form, which means "completely alone".
///                 The scenarios do not need one: the child gets its
///                 <c>HOME</c> explicitly through <c>ChildEnv</c>, and the test
///                 process reads no home-scoped state.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>No project-wide mutex.</b> A constraint key held by
///                 every class IS a project-wide mutex, whatever the key is
///                 called. That is the shape #1789 removed. A key that TWO
///                 classes hold is the shape a real shared resource has (the
///                 project's one pair is the quit gesture, whose 2 000 ms
///                 window the product measures on the APP's clock), and it
///                 must name that resource in a comment on the attribute.
///                 Three holders means the key has spread.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>No bare form.</b> The bare <c>[NotInParallel]</c> takes
///                 the global <c>NotInParallelLock</c>: the class runs with no
///                 other test in the assembly, keyed or not. It is the RIGHT
///                 form for a class that mutates a process-wide static whose
///                 readers are unkeyed, and rule 1 is why no class here does
///                 that.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Every key is documented.</b> The repo's rule: "keep the
///                 reason as a comment on the attribute". A key without a
///                 comment is a label nobody can check. The mistake #720 was
///                 opened for.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>A bounded concurrency budget.</b> The budget is what
///                 replaced the mutex: without a limiter, TUnit's unconstrained
///                 bucket runs at ProcessorCount × 4, which on a four-core
///                 runner means sixteen spawned <c>dotnet</c> children at once.
///                 The limit is a CONSTANT (scenarios at a time), not a ratio.
///             </description>
///         </item>
///     </list>
/// </summary>
public sealed class PtyScenarioIsolationTests
{
    /// <summary>
    ///     This file is excluded from its own scan: it NAMES the API it looks
    ///     for, in a constant and in planted controls. The rule that keeps a
    ///     guard from reporting its own prose is the same one the architecture
    ///     guard follows (<c>ProcessEnvIsolationRule</c>).
    /// </summary>
    private const string ThisFile = "PtyScenarioIsolationTests.cs";

    private const string ProjectDirName = "Harbor.Tui.CellForge.PtyTests";

    /// <summary>A process-environment write, in any spelling of the call.</summary>
    private static readonly Regex EnvWrite = new(
        @"Environment\s*\.\s*SetEnvironmentVariable\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    ///     One <c>[NotInParallel]</c> attribute: the bare form, or a key list.
    ///     Group 1 holds the argument list (empty for the bare form).
    /// </summary>
    private static readonly Regex NotInParallel = new(
        @"\[\s*NotInParallel\s*(?:\(\s*([^)]*?)\s*\))?\s*\]",
        RegexOptions.Compiled);

    /// <summary>A <c>[ParallelLimiter&lt;…&gt;]</c> attribute and the limit type it names.</summary>
    private static readonly Regex ParallelLimiter = new(
        @"\[\s*ParallelLimiter\s*<\s*([A-Za-z_][A-Za-z0-9_]*)\s*>",
        RegexOptions.Compiled);

    /// <summary>A comment line (documentation or not).</summary>
    private static readonly Regex CommentLine = new(
        @"^\s*(///|//)",
        RegexOptions.Compiled);

    /// <summary>
    ///     The project's own source files: the raw text (for the comment
    ///     checks) and the comment-stripped text (for the call checks, so that
    ///     a mention of an API in prose is not a call to it).
    /// </summary>
    private static IReadOnlyList<(string File, string Raw, string Code)> ProjectSources()
    {
        string dir = Path.Combine(HarborAppLocator.FindRepoRoot(), "tests", ProjectDirName);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        List<(string File, string Raw, string Code)> sources = [];
        foreach (string path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (string.Equals(name, ThisFile, StringComparison.Ordinal))
            {
                continue;
            }

            string raw = File.ReadAllText(path);
            sources.Add((name, raw, StripComments(raw)));
        }

        return sources;
    }

    // ── rule 1: no process-wide state ───────────────────────────────────────

    [Test]
    public async Task No_Scenario_Writes_The_Process_Environment()
    {
        IReadOnlyList<(string File, string Raw, string Code)> sources = ProjectSources();
        await Assert.That(sources.Count).IsGreaterThan(0)
            .Because(
                "the scan found no source file, so the rule would report green while enforcing nothing. "
                + "The project directory is resolved from the repository root, which is found by walking up "
                + "for Harbor.slnx.");

        List<string> violations = [];
        foreach ((string file, string _raw, string code) in sources)
        {
            if (EnvWrite.IsMatch(code))
            {
                violations.Add(file);
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "A write to the process environment is process-wide, so every scenario that made one would "
                + "have to hold a mutex against every other scenario that made one, and with unkeyed "
                + "readers the repo's rule (docs/TEST_PATTERNS.md §Parallelism) demands the BARE "
                + "[NotInParallel] form, which means \"completely alone\". That is strictly worse than the "
                + "key this project used to carry. The scenarios do not need a process write: the child "
                + "receives its HOME through ChildEnv, and the per-test TempHome is the scenario's own. "
                + "Files that write the process environment: " + string.Join(", ", violations));
    }

    // ── rule 2: no project-wide mutex ───────────────────────────────────────

    [Test]
    public async Task No_Constraint_Key_Is_Held_By_More_Than_Two_Classes()
    {
        IReadOnlyList<(string File, string Raw, string Code)> sources = ProjectSources();
        await Assert.That(sources.Count).IsGreaterThan(0)
            .Because("the scan found no source file, so the rule would be enforcing nothing.");

        Dictionary<string, List<string>> holders = new(StringComparer.Ordinal);
        foreach ((string file, string _raw, string code) in sources)
        {
            foreach (string key in ConstraintKeys(code))
            {
                if (!holders.TryGetValue(key, out List<string>? files))
                {
                    files = [];
                    holders[key] = files;
                }

                files.Add(file);
            }
        }

        List<string> violations = [];
        foreach (KeyValuePair<string, List<string>> holder in holders)
        {
            if (holder.Value.Count <= 2)
            {
                continue;
            }

            violations.Add(
                $"key \"{holder.Key}\" is held by {holder.Value.Count} classes ({string.Join(", ", holder.Value)})");
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "A constraint key held by more than one class IS a mutex over its named peers, and a "
                + "key that EVERY class holds is a project-wide mutex whatever it is called. That is "
                + "exactly the shape #1789 removed (one \"pty\" key on 40 classes, parallelism factor "
                + "0.99). A key that TWO classes hold is the shape a real shared resource has: the "
                + "project's one such pair is the quit gesture, whose 2 000 ms window the product "
                + "measures on the APP's clock. A third holder means the resource is not what the key "
                + "claims, or the key has spread: name the resource in a comment on the attribute and "
                + "fix the key. Violations: " + string.Join("; ", violations));
    }

    // ── rule 3: no bare form ────────────────────────────────────────────────

    [Test]
    public async Task No_Class_Uses_The_Bare_NotInParallel_Form()
    {
        IReadOnlyList<(string File, string Raw, string Code)> sources = ProjectSources();
        await Assert.That(sources.Count).IsGreaterThan(0)
            .Because("the scan found no source file, so the rule would be enforcing nothing.");

        List<string> violations = [];
        foreach ((string file, string _raw, string code) in sources)
        {
            if (IsBareNotInParallel(code))
            {
                violations.Add(file);
            }
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "The bare [NotInParallel] form means \"completely alone\": it takes the global "
                + "NotInParallelLock, so the class runs with no other test in the assembly, keyed or not. "
                + "In this project that would serialize the scenarios against the fast in-process tests "
                + "too (PtyScrollbackTests, and this file). The bare form is the RIGHT form for a class "
                + "that mutates a process-wide static whose readers are unkeyed, and rule 1 above is why "
                + "no class here does that. Files using the bare form: " + string.Join(", ", violations));
    }

    // ── rule 4: every key is documented ─────────────────────────────────────

    [Test]
    public async Task Every_NotInParallel_Attribute_Carries_A_Comment()
    {
        IReadOnlyList<(string File, string Raw, string Code)> sources = ProjectSources();
        await Assert.That(sources.Count).IsGreaterThan(0)
            .Because("the scan found no source file, so the rule would be enforcing nothing.");

        List<string> undocumented = [];
        foreach ((string file, string raw, string _code) in sources)
        {
            string[] lines = raw.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!NotInParallel.IsMatch(lines[i]))
                {
                    continue;
                }

                if (HasDedicatedCommentAbove(lines, i))
                {
                    continue;
                }

                undocumented.Add($"{file}:{i + 1}");
            }
        }

        await Assert.That(undocumented).IsEmpty()
            .Because(
                "The repo's rule for a parallelism attribute: \"keep the reason as a comment on the "
                + "attribute\" (docs/TEST_PATTERNS.md §Parallelism). A key without a comment is a label "
                + "nobody can check. The mistake #720 was opened for, and a bare form without a comment "
                + "is a whole-assembly mutex nobody can justify. Attributes without a comment above them: "
                + string.Join(", ", undocumented));
    }

    // ── rule 5: a bounded concurrency budget ────────────────────────────────

    [Test]
    public async Task The_Project_Declares_A_Bounded_Concurrency_Budget()
    {
        string baseFile = Path.Combine(
            HarborAppLocator.FindRepoRoot(), "tests", ProjectDirName, "CellForgePtyScenarioBase.cs");
        await Assert.That(File.Exists(baseFile)).IsTrue()
            .Because("the scenario base class is where the budget is declared; the file is not there.");

        string text = StripComments(File.ReadAllText(baseFile));
        Match match = ParallelLimiter.Match(text);
        await Assert.That(match.Success).IsTrue()
            .Because(
                "The scenarios are spawned .NET children, and TUnit's unconstrained bucket runs at "
                + "ProcessorCount × 4, which is sixteen children at once on a four-core runner. The budget "
                + "replaced the project-wide mutex, so it has to be declared: "
                + "[ParallelLimiter<SomeLimit>] on CellForgePtyScenarioBase.");

        string limitTypeName = match.Groups[1].Value;
        Type? limitType = Type.GetType(
            $"Harbor.Tui.CellForge.PtyTests.{limitTypeName}, Harbor.Tui.CellForge.PtyTests");
        await Assert.That(limitType).IsNotNull()
            .Because(
                $"the limit type {limitTypeName} could not be resolved in this assembly, so the budget it "
                + "declares cannot be read.");

        var limit = (IParallelLimit)Activator.CreateInstance(limitType!)!;
        await Assert.That(limit.Limit).IsGreaterThan(0)
            .Because("a limiter with no permits would hang the project; measured: " + limit.Limit + ".");
        await Assert.That(limit.Limit).IsLessThanOrEqualTo(4)
            .Because(
                "The budget is a CONSTANT, not a ratio: a scenario is a spawned child process, and the "
                + "four-core runner is what carries them. Four permits is the ceiling this guard holds "
                + "the budget to; a larger value is a different budget and has to be argued for with a "
                + "measurement, not discovered by a timeout in somebody else's shard. Measured: "
                + limit.Limit + " permit(s).");
    }

    // ── non-vacuity ─────────────────────────────────────────────────────────

    /// <summary>
    ///     The detectors must fire on the shapes they guard and stay quiet on
    ///     the shapes they must not touch. A guard whose scan matches nothing
    ///     is indistinguishable from a guard whose scan is broken, and a broken
    ///     guard is worse than none because it is believed.
    /// </summary>
    [Test]
    public async Task The_Detectors_Fire_On_Their_Shapes_And_Stay_Quiet_On_The_Other_ones()
    {
        // The keyed form names its peers; the bare form names none.
        await Assert.That(ConstraintKeys("[NotInParallel(\"pty\")]").Count).IsEqualTo(1)
            .Because("a keyed attribute must yield exactly its key, or the key census is blind.");
        await Assert.That(ConstraintKeys("[NotInParallel(new[] { \"a\", \"b\" })]").Count).IsEqualTo(2)
            .Because("the array form names two peers, and both must be counted.");
        await Assert.That(ConstraintKeys("[NotInParallel]").Count).IsEqualTo(0)
            .Because("the bare form names no peer. It is \"completely alone\", not a key.");

        await Assert.That(IsBareNotInParallel("[NotInParallel]")).IsTrue()
            .Because("the bare form must be recognised as bare.");
        await Assert.That(IsBareNotInParallel("[NotInParallel(\"pty\")]")).IsFalse()
            .Because("a keyed attribute is not the bare form.");

        // A call in code is a call; the same text in a comment is prose.
        await Assert.That(EnvWrite.IsMatch(StripComments("Environment.SetEnvironmentVariable(\"HOME\", x);")))
            .IsTrue()
            .Because("the detector must fire on the call it exists to catch.");
        await Assert.That(EnvWrite.IsMatch(StripComments("// Environment.SetEnvironmentVariable(\"HOME\", x);")))
            .IsFalse()
            .Because("a mention of the API in a comment is not a call, and a guard that reports its own "
                + "prose is unusable the moment anyone edits a comment.");
        await Assert.That(EnvWrite.IsMatch(StripComments("var v = Environment.GetEnvironmentVariable(\"HOME\");")))
            .IsFalse()
            .Because("reading the environment is not a write.");

        // The stripper keeps code and drops prose.
        const string quote3 = "\"\"\"";
        string source = "// a comment mentioning Environment.SetEnvironmentVariable(\n"
            + "var s = \"a string with // a slash and a \\\" quote\";\n"
            + "var t = " + quote3 + "raw string with \"quotes\" and // slashes" + quote3 + ";\n";
        string stripped = StripComments(source);
        await Assert.That(stripped).Contains("var s =")
            .Because("a comment must not take the code after it with it.");
        await Assert.That(stripped).Contains("var t =")
            .Because("a raw string literal must survive the stripper, because the scenario files use them.");
        await Assert.That(stripped).Contains("raw string with")
            .Because("the CONTENT of a raw string is code, not prose, and must survive the stripper.");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     The constraint keys a file declares. The bare form declares none:
    ///     it is "completely alone", not a named peer set.
    /// </summary>
    private static IReadOnlyList<string> ConstraintKeys(string code)
    {
        List<string> keys = [];
        foreach (Match match in NotInParallel.Matches(code))
        {
            if (!match.Groups[1].Success)
            {
                continue;
            }

            foreach (Match quoted in Regex.Matches(match.Groups[1].Value, "\"([^\"]*)\""))
            {
                keys.Add(quoted.Groups[1].Value);
            }
        }

        return keys;
    }

    /// <summary>Whether the file uses the bare form at least once.</summary>
    private static bool IsBareNotInParallel(string code) =>
        NotInParallel.Matches(code).Cast<Match>().Any(m => !m.Groups[1].Success);

    /// <summary>
    ///     Whether the attribute carries a comment of its OWN. The class's
    ///     documentation block ends with <c>/// &lt;/summary&gt;</c>, and that
    ///     line is not a reason for the attribute. A key that is only
    ///     "documented" by the class doc is exactly the undocumented key the
    ///     rule is about.
    /// </summary>
    private static bool HasDedicatedCommentAbove(string[] lines, int index)
    {
        for (int i = index - 1; i >= 0 && i >= index - 8; i--)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!CommentLine.IsMatch(lines[i]))
            {
                // A non-comment, non-blank line ends the look-back.
                return false;
            }

            return !trimmed.EndsWith("</summary>", StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    ///     Strip comments so that a mention of an API in prose is not a call
    ///     to it. String, raw-string and char literals are preserved.
    /// </summary>
    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        bool inLineComment = false;
        bool inBlockComment = false;
        bool inString = false;
        bool inRawString = false;
        bool inChar = false;
        bool escaped = false;

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                    sb.Append(c);
                }

                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                else if (c == '\n')
                {
                    sb.Append(c);
                }

                continue;
            }

            if (inRawString)
            {
                sb.Append(c);
                if (c == '"' && next == '"' && i + 2 < source.Length && source[i + 2] == '"')
                {
                    sb.Append(next);
                    sb.Append(source[i + 2]);
                    i += 2;
                    inRawString = false;
                }

                continue;
            }

            if (inString)
            {
                sb.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inChar)
            {
                sb.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '\'')
                {
                    inChar = false;
                }

                continue;
            }

            if (c == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (c == '"' && next == '"' && i + 2 < source.Length && source[i + 2] == '"')
            {
                inRawString = true;
                sb.Append(c).Append(next).Append(source[i + 2]);
                i += 2;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }

            if (c == '\'')
            {
                inChar = true;
                sb.Append(c);
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
