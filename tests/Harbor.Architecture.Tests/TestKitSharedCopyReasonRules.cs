// TestKitSharedCopyReasonRules.cs — GUARD for issue #1889, second half.
//
// WHAT #1889 FOUND, AND WHY A COPY IS A PROBLEM
// ----------------------------------------------
// `Harbor.TestKit` shipped ONE file that needed the UI stack, and that file
// dragged the whole UI/Desktop stack into every one of its 14 consumers. The
// same arrangement produced a second, quieter problem: FIVE projects kept a
// `TestScalingGate.cs` of their own — a byte-for-byte copy of the shared one,
// modulo the namespace and the accessibility modifiers — because the shared
// file sat behind a TestKit that dragged `Harbor.Application` into the slice
// those projects measure.
//
// Five copies of a ~250-line measurement helper is five places for the
// decision function to drift. #1889 removed the REASON the copies existed
// (the fat TestKit), so all five are gone. This file is what stops them from
// coming back: a copy of a shared kit file is allowed, but only if it says
// WHY it is a copy.
//
// WHAT IS RULED
// -------------
//   R1  A file under `tests/` that is a COPY of a file in the shared kit
//       (same file name, same code modulo the namespace and the `public` /
//       `internal` modifiers) must carry a WRITTEN REASON in its file
//       header: the header must name the master it was copied from, and the
//       prose around that mention must be an actual argument.
//
//   R2  A file that merely SHARES A NAME with a kit file is not a copy and is
//       not judged. `PerfGate.cs` in three projects is a copy; a file that
//       happens to be called `Fakes.cs` and does something else is not.
//
//   The shared kit's OWN files are the masters, and they are not candidates:
//   a master is trivially "a copy of itself", and judging it would report
//   every shared file as an undocumented copy.
//
// WHY THE REASON IS CHECKED THROUGH `ExemptionReason`
// ----------------------------------------------------
// "Write down why" is a question this repository already has ONE answer to:
// `ExemptionReason.RowsWithoutAReason` — not blank, not a bare tracking URL,
// and at least 40 characters, because `later` and `TODO` are not arguments.
// Routing this rule through it means a second, softer definition of "a reason"
// cannot appear next to the first.
//
// The rule deliberately does NOT ask whether the reason is still TRUE. That
// is a different question, and the tables that can answer it answer it by
// liveness. What this rule asks is the narrower one: did somebody write the
// argument down where a tool can read it?
//
// NON-VACUITY
// -----------
//   * `The_Tree_Actually_Contains_A_Documented_Copy` — at least one copy
//     exists and carries a reason, so the rule is judging something rather
//     than passing over an empty set.
//   * `A_Copy_Without_A_Reason_Is_Reported` — the synthetic control: a copy
//     whose header names the master but states no reason must be reported.
//   * `A_Copy_With_A_Reason_Is_Not_Reported` — the negative control: the same
//     copy WITH a reason must be silent, or "reported" would be satisfied by
//     a matcher that reports everything.
//   * `A_Same_Named_File_That_Is_Not_A_Copy_Is_Not_Reported` — R2's control.
//
// The synthetic controls drive the SAME matcher the live rule uses, through
// the same entry point, so a matcher that was emptied or inverted cannot pass
// both of them.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     A documented copy of a shared kit file must carry a written reason
///     (#1889). Without one, the copies of the shared cost gates would drift
///     apart again — five of them existed only because the shared file was
///     unreachable, and nothing stopped a sixth from appearing.
/// </summary>
public sealed class TestKitSharedCopyReasonRules
{
    /// <summary>
    ///     The directory holding the shared kit's master files.
    /// </summary>
    internal const string CoreKitDirectory = "Harbor.TestKit";

    /// <summary>
    ///     The projects whose files are shared kit sources — the core kit and
    ///     its parts (<c>Harbor.TestKit.Tui</c>, and whatever else the family
    ///     grows). A project whose name ends in <c>.Tests</c> is a test suite
    ///     FOR the kit, not a part of it, and is excluded.
    /// </summary>
    internal static bool IsSharedKitProject(string projectDirectory) =>
        (string.Equals(projectDirectory, CoreKitDirectory, StringComparison.Ordinal)
         || projectDirectory.StartsWith(CoreKitDirectory + ".", StringComparison.Ordinal))
        && !projectDirectory.EndsWith(".Tests", StringComparison.Ordinal);

    /// <summary>
    ///     Matches a namespace declaration, so the copy's own namespace can be
    ///     normalised away.
    /// </summary>
    private static readonly Regex NamespacePattern = new(@"namespace\s+[\w\.]+\s*;", RegexOptions.Compiled);

    /// <summary>
    ///     Matches the two access modifiers a copy is allowed to differ in.
    /// </summary>
    private static readonly Regex AccessibilityPattern = new(@"\b(public|internal)\b", RegexOptions.Compiled);

    /// <summary>
    ///     Normalises source so that two copies of the same file compare
    ///     equal: comments go, blank lines go, the namespace becomes a
    ///     placeholder, and <c>public</c> / <c>internal</c> become one token.
    /// </summary>
    /// <remarks>
    ///     The accessibility collapse is the point: the copies in the tree are
    ///     the shared file with its types narrowed to <c>internal</c> for the
    ///     local project. That IS the copy shape, and a comparison that did
    ///     not collapse it would see two different files and rule on nothing.
    /// </remarks>
    internal static string Normalize(string source)
    {
        string stripped = SourceScan.StripComments(source);

        var lines = new List<string>();
        foreach (string line in stripped.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                lines.Add(trimmed);
            }
        }

        string text = string.Join("\n", lines);
        text = NamespacePattern.Replace(text, "namespace NS;");
        return AccessibilityPattern.Replace(text, "ACCESS");
    }

    /// <summary>
    ///     The file's leading comment block — the maximal run of
    ///     <c>//</c>-comment lines at the top of the file, ignoring blank lines
    ///     before it.
    /// </summary>
    /// <remarks>
    ///     The header is where a copy's reason belongs: it is the first thing
    ///     a reader of the copy sees, and it is the only place a tool can look
    ///     without parsing the file. A reason buried in the middle of the
    ///     body is a reason nobody reads.
    /// </remarks>
    internal static string HeaderComment(string source)
    {
        var header = new List<string>();
        bool started = false;

        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.Trim();

            if (!started)
            {
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (!trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    break;
                }
            }
            else if (trimmed.Length == 0 || !trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            started = true;
            header.Add(trimmed);
        }

        return string.Join("\n", header);
    }

    /// <summary>
    ///     Whether the candidate is a copy of the master: the same code once
    ///     comments, the namespace and the accessibility modifiers are
    ///     normalised away.
    /// </summary>
    internal static bool IsCopyOf(string candidateSource, string masterSource) =>
        string.Equals(Normalize(candidateSource), Normalize(masterSource), StringComparison.Ordinal);

    /// <summary>
    ///     The failure messages for the candidates that are copies without a
    ///     written reason. Empty means every copy says why it is a copy.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The candidate's own path and the master's repo-relative path
    ///         are the two things a reader needs. The master path is checked
    ///         FIRST, because a copy that does not even name its master is a
    ///         copy nobody can trace, and a reason that does not name the
    ///         master is prose about something else.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyList<string> CopiesWithoutAReason(
        IReadOnlyList<(string Path, string Source)> candidates,
        IReadOnlyList<(string Path, string Source)> masters)
    {
        var failures = new List<string>();

        // The masters themselves are not candidates: a master is trivially a
        // copy of itself, and judging it would report every shared file as an
        // undocumented copy.
        var masterPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var master in masters)
        {
            masterPaths.Add(master.Path);
        }

        foreach ((string path, string source) in candidates)
        {
            string fileName = Path.GetFileName(path);

            if (masterPaths.Contains(path))
            {
                continue;
            }

            foreach ((string masterPath, string masterSource) in masters)
            {
                if (!string.Equals(Path.GetFileName(masterPath), fileName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!IsCopyOf(source, masterSource))
                {
                    // R2: a same-named file that is NOT a copy is not judged.
                    continue;
                }

                string header = HeaderComment(source).Replace('\\', '/');

                if (!header.Contains(masterPath, StringComparison.Ordinal))
                {
                    failures.Add(
                        $"{path} is a copy of {masterPath} but its header does not name the file it "
                        + "was copied from. A copy nobody can trace is a copy nobody keeps in sync.");
                    break;
                }

                // The reason is the header with the master's own path removed —
                // what is left has to be an argument, not an echo of the filename.
                string reason = header.Replace(masterPath, " ", StringComparison.Ordinal);
                reason = string.Join(' ', reason.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Trim();

                IReadOnlyList<string> reasonFailures = ExemptionReason.RowsWithoutAReason(
                    "TestKitSharedCopyReasonRules.DocumentedCopies",
                    new (string Key, ExemptionReason.Row Row)[]
                    {
                        (path, new ExemptionReason.Row(reason, null)),
                    });

                if (reasonFailures.Count > 0)
                {
                    failures.Add(
                        $"{path} is a copy of {masterPath} and must carry a written reason in its "
                        + "header. " + reasonFailures[0]);
                }

                break;
            }
        }

        return failures;
    }

    // ---- R1: the live rule --------------------------------------------------

    /// <summary>
    ///     Every copy of a shared kit file says why it is a copy.
    /// </summary>
    [Test]
    public async Task Every_Copy_Of_A_Shared_Kit_File_Carries_A_Written_Reason()
    {
        IReadOnlyList<(string Path, string Source)> masters = SharedKitMasters();
        IReadOnlyList<(string Path, string Source)> candidates = TestTreeSources();

        IReadOnlyList<string> failures = CopiesWithoutAReason(candidates, masters);

        await Assert.That(failures).IsEmpty()
            .Because(
                "A copy of a shared kit file is allowed — but only if it states WHY. The five "
                + "copies of TestScalingGate.cs that #1889 removed were five places for the "
                + "decision function to drift; a sixth must not appear silently. Failures: "
                + string.Join("; ", failures));
    }

    // ---- non-vacuity: the tree really holds a documented copy ----------------

    /// <summary>
    ///     The tree holds at least one copy of a shared kit file. Without this
    ///     the rule would be deciding nothing, which is the greenest and least
    ///     informative state a rule can be in.
    /// </summary>
    [Test]
    public async Task The_Tree_Actually_Contains_A_Documented_Copy()
    {
        IReadOnlyList<(string Path, string Source)> masters = SharedKitMasters();
        IReadOnlyList<(string Path, string Source)> candidates = TestTreeSources();

        var copies = new List<string>();
        foreach ((string path, string source) in candidates)
        {
            foreach ((string masterPath, string masterSource) in masters)
            {
                if (string.Equals(Path.GetFileName(masterPath), Path.GetFileName(path), StringComparison.Ordinal)
                    && IsCopyOf(source, masterSource))
                {
                    copies.Add(path);
                    break;
                }
            }
        }

        await Assert.That(copies).IsNotEmpty()
            .Because(
                "The rule is about copies that EXIST — if the tree holds none, the rule is "
                + "deciding nothing and its green means nothing. Master files known: "
                + string.Join(", ", masters.Select(m => m.Path)));
    }

    // ---- non-vacuity: the synthetic controls --------------------------------

    /// <summary>
    ///     A copy whose header names the master but states no reason must be
    ///     reported. This is the shape the five deleted copies had before
    ///     #1889 gave them their reason.
    /// </summary>
    [Test]
    public async Task A_Copy_Without_A_Reason_Is_Reported()
    {
        var masters = new List<(string Path, string Source)>
        {
            ("tests/Harbor.TestKit/SampleHelper.cs", SampleMasterSource),
        };

        var candidates = new List<(string Path, string Source)>
        {
            ("tests/Harbor.Sample.Tests/SampleHelper.cs", SampleCopySource),
        };

        IReadOnlyList<string> failures = CopiesWithoutAReason(candidates, masters);

        await Assert.That(failures).IsNotEmpty()
            .Because(
                "A copy that names its master and says nothing else is exactly what the guard is "
                + "for: it must be reported. Failures: " + string.Join("; ", failures));
    }

    /// <summary>
    ///     The same copy WITH a reason must be silent. Without this control,
    ///     "reported" would be satisfied by a matcher that reports everything.
    /// </summary>
    [Test]
    public async Task A_Copy_With_A_Reason_Is_Not_Reported()
    {
        var masters = new List<(string Path, string Source)>
        {
            ("tests/Harbor.TestKit/SampleHelper.cs", SampleMasterSource),
        };

        var candidates = new List<(string Path, string Source)>
        {
            ("tests/Harbor.Sample.Tests/SampleHelper.cs", SampleDocumentedCopySource),
        };

        IReadOnlyList<string> failures = CopiesWithoutAReason(candidates, masters);

        await Assert.That(failures).IsEmpty()
            .Because(
                "A copy that names its master and states the reason is the shape the rule permits. "
                + "Failures: " + string.Join("; ", failures));
    }

    /// <summary>
    ///     A file that shares a NAME with a kit file but is not a copy is not
    ///     judged. Without this control, the rule would police every file
    ///     that happens to share a name, which is a different rule and a
    ///     worse one.
    /// </summary>
    [Test]
    public async Task A_Same_Named_File_That_Is_Not_A_Copy_Is_Not_Reported()
    {
        var masters = new List<(string Path, string Source)>
        {
            ("tests/Harbor.TestKit/SampleHelper.cs", SampleMasterSource),
        };

        var candidates = new List<(string Path, string Source)>
        {
            ("tests/Harbor.Sample.Tests/SampleHelper.cs",
                "namespace Harbor.Sample.Tests;\n\ninternal static class SomethingElse { }\n"),
        };

        IReadOnlyList<string> failures = CopiesWithoutAReason(candidates, masters);

        await Assert.That(failures).IsEmpty()
            .Because(
                "A file that only shares a NAME with a kit file is not a copy of it. Failures: "
                + string.Join("; ", failures));
    }

    // ---- the fixtures -------------------------------------------------------

    /// <summary>
    ///     A minimal stand-in for a shared kit file: a class in a namespace,
    ///     public, with a member.
    /// </summary>
    internal const string SampleMasterSource = """
        namespace Harbor.TestKit;

        /// <summary>A shared helper.</summary>
        public static class SampleHelper
        {
            /// <summary>Does the thing.</summary>
            public static bool DoesIt() => true;
        }
        """;

    /// <summary>
    ///     The same file copied into another project: internal, own namespace,
    ///     header naming the master and stating nothing else.
    /// </summary>
    internal const string SampleCopySource = """
        // tests/Harbor.TestKit/SampleHelper.cs
        namespace Harbor.Sample.Tests;

        /// <summary>A shared helper.</summary>
        internal static class SampleHelper
        {
            /// <summary>Does the thing.</summary>
            public static bool DoesIt() => true;
        }
        """;

    /// <summary>
    ///     The same copy, with the reason the guard demands.
    /// </summary>
    internal const string SampleDocumentedCopySource = """
        // tests/Harbor.TestKit/SampleHelper.cs — DOCUMENTED COPY.
        // This project measures its own slice in isolation and cannot take the
        // shared helper's dependency, so the same API ships locally as
        // INTERNAL. Keep this copy in sync with the shared master.
        namespace Harbor.Sample.Tests;

        /// <summary>A shared helper.</summary>
        internal static class SampleHelper
        {
            /// <summary>Does the thing.</summary>
            public static bool DoesIt() => true;
        }
        """;

    // ---- the walk -----------------------------------------------------------

    /// <summary>
    ///     Every shared kit file, as (repo-relative path, source) pairs.
    /// </summary>
    internal static IReadOnlyList<(string Path, string Source)> SharedKitMasters()
    {
        var masters = new List<(string Path, string Source)>();

        if (RepoPaths.RepoRoot is not { } root)
        {
            return masters;
        }

        string testsRoot = Path.Combine(root, "tests");
        if (!Directory.Exists(testsRoot))
        {
            return masters;
        }

        foreach (string directory in Directory.EnumerateDirectories(testsRoot))
        {
            string name = Path.GetFileName(directory);
            if (!IsSharedKitProject(name))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                string normalised = file.Replace('\\', '/');
                if (normalised.Contains("/obj/", StringComparison.Ordinal)
                    || normalised.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }

                string? source = SourceScan.TryReadAllText(file);
                if (source is null)
                {
                    continue;
                }

                masters.Add((SourceScan.Relative(file), source));
            }
        }

        return masters;
    }

    /// <summary>
    ///     Every <c>*.cs</c> file under <c>tests/</c>, as (path, source) pairs.
    /// </summary>
    /// <remarks>
    ///     <see cref="SourceScan.EnumerateCsFiles" /> deliberately EXCLUDES
    ///     <c>tests/</c> — those gates police product code, and a rule must
    ///     not police its own fixtures. This rule is about the test tree
    ///     itself, so it walks the tree itself.
    /// </remarks>
    internal static IReadOnlyList<(string Path, string Source)> TestTreeSources()
    {
        var sources = new List<(string Path, string Source)>();

        if (RepoPaths.RepoRoot is not { } root)
        {
            return sources;
        }

        string testsRoot = Path.Combine(root, "tests");
        if (!Directory.Exists(testsRoot))
        {
            return sources;
        }

        foreach (string file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            string normalised = file.Replace('\\', '/');
            if (normalised.Contains("/obj/", StringComparison.Ordinal)
                || normalised.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            string? source = SourceScan.TryReadAllText(file);
            if (source is null)
            {
                continue;
            }

            sources.Add((SourceScan.Relative(file), source));
        }

        return sources;
    }
}
