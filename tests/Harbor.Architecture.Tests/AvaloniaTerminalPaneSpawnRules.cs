// AvaloniaTerminalPaneSpawnRules.cs — source-level guard for issue #672:
// TerminalPaneViewModel forked a real interactive shell (`$SHELL -i`, full
// inherited environment) straight out of a Presentation assembly, with no
// permission gate, no cancellation point and no seam to fake.
//
// THE PRECEDENT THIS FOLLOWS
// --------------------------
// #537 moved `GitService` out of Presentation for exactly this reason and left
// two pieces of machinery behind: the architectural half (Presentation no
// longer forks) and the DECISION half, pinned by `GitServicePermissionGatingTests`
// so "is the UI-chrome git read gated?" cannot quietly become an oversight. This
// file is the architectural half for the terminal pane, and it is a SOURCE-TEXT
// rule for the same reason `AvaloniaFireAndForgetRules` (#569) is: the rule spans
// `apps/Harbor.App.Avalonia`, which this test project does not reference (and must
// not — it is an app, a composition root). A source scan needs no reference edge.
//
// WHY AN EXCEPTION ROW IS NOT THE ANSWER HERE
// -------------------------------------------
// `PresentationCapabilityRules.KnownViolations` grandfathers a real violation
// against an IL probe that reads `System.Diagnostics.Process` in every method
// body. The terminal pane is NOT that shape and must not be given one:
//
//   * `PtyProcess` P/Invokes `posix_openpt`/`posix_spawn` (NativeMethods.cs). It
//     never names `System.Diagnostics.Process`, so the IL probe would not see a
//     hit even if the app assembly were scanned — the capability probe is blind
//     to it, which is precisely why this violation survived #455.
//   * `Harbor.App.Avalonia` is not in `FullLayerMatrixTests.AllSrcAssemblies`
//     (that list is `src/`-only), so it is not a Presentation assembly the
//     capability probe walks at all. A baseline row there would be a row against
//     an assembly the probe never opens — a lie in a table whose whole purpose is
//     to be checkable.
//
// So: no exemption, here or there. The pane is rewritten to go through a seam,
// and this rule keeps it there. `PresentationCapabilityRules` needs no change and
// gains no row that would later have to be deleted.
//
// NON-VACUITY
// -----------
// A source guard that silently matches nothing is worse than no guard. Three
// defences, mirroring AvaloniaFireAndForgetRules:
//
//   * `Scanner_FindsTheGuardedProject` — the walk really finds the shell.
//   * `Detector_FiresOnAKnownFork_AndStaysQuietOnTheSeamCall` — the detector, in
//     isolation, fires on the exact pre-#672 shape and is quiet on the post-#672
//     shape AND on the unrelated `_ =` style discards that legitimately exist.
//   * `Detector_IsNotDefeatedByRenamingTheLocal` — pins that the rule keys on the
//     spawn call, not on a variable name, so it cannot be defeated by a rename.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     §ARCH guard (issue #672): a Presentation assembly may not fork a terminal
///     session. The desktop floating-terminal pane goes through
///     <c>ITerminalPaneLauncher</c>, which is permission-gated and cancellable.
/// </summary>
public class AvaloniaTerminalPaneSpawnRules
{
    /// <summary>
    ///     Projects this rule polices. Each entry must correspond to a perimeter
    ///     that has actually been converted.
    /// </summary>
    private static readonly string[] GuardedProjects = ["apps/Harbor.App.Avalonia"];

    /// <summary>
    ///     The forbidden shapes: a direct <c>PtyProcess</c> spawn, or the
    ///     <c>PtyStartSpec</c> that describes one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Keyed on the spawn CALL rather than on the type name, so binding the
    ///         handle to a differently-named local does not defeat it — the
    ///         <c>detector</c> test below pins that.
    ///     </para>
    ///     <para>
    ///         <c>PtyStartSpec</c> is listed separately because constructing one is
    ///         already the business decision "here is the argv I intend to execute",
    ///         which is exactly what must not happen in a ViewModel.
    ///     </para>
    /// </remarks>
    private static readonly Regex[] ForbiddenPatterns =
    [
        new(@"PtyProcess\s*\.\s*(TryStart|Start)\s*\(", RegexOptions.Compiled),
        new(@"new\s+PtyStartSpec\s*\(", RegexOptions.Compiled),
    ];

    /// <summary>
    ///     Strips whole-line <c>//</c> comments so a file that explains the rule
    ///     (including this guard's own failure text) is not itself a violation.
    /// </summary>
    private static string StripLineComments(string source) =>
        string.Join('\n', source.Split('\n')
            .Select(line =>
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx < 0 ? line : line[..idx];
            }));

    /// <summary>Repo-relative <c>path:line</c> of every forbidden shape in a file.</summary>
    private static List<string> DetectIn(string relativePath, string source)
    {
        var hits = new List<string>();
        string[] lines = StripLineComments(source).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // One report per line, even when both patterns match it: a single
            // `PtyProcess.Start(new PtyStartSpec(...))` is one violation, and a
            // failure message that prints it twice reads as two bugs.
            foreach (Regex pattern in ForbiddenPatterns)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    hits.Add($"{relativePath}:{i + 1}: {lines[i].Trim()}");
                    break;
                }
            }
        }

        return hits;
    }

    [Test]
    public async Task AvaloniaShell_DoesNot_ForkATerminalSession()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because(
                "This guard walks the working tree. With no Harbor.slnx above AppContext.BaseDirectory "
                + "the scan yields nothing and the rule reports green while enforcing nothing.");

        if (root is null)
        {
            return;
        }

        var violations = new List<string>();
        foreach (string file in EnumerateGuardedFiles(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            violations.AddRange(DetectIn(relative, File.ReadAllText(file)));
        }

        await Assert.That(violations).IsEmpty()
            .Because(
                "§ARCH (#672). A Presentation assembly may not fork a process: docs/ARCHITECTURE_LAYERS.md "
                + "§3 assigns subprocess to Infrastructure, and a UI that shells out on its own bypasses the "
                + "ITool/PermissionRuleset seam, so the launch is never gated, never cancellable and never "
                + "appears in a transcript. This is worse than the git case #537 fixed — the pane ran "
                + "`$SHELL -i` with the FULL inherited environment, not three read-only git arguments. "
                + "Go through ITerminalPaneLauncher (Harbor.Abstractions.Terminal), which the container "
                + "wires to a permission-gated, cancellable Infrastructure implementation. "
                + string.Join("\n", violations));
    }

    // ── non-vacuity ───────────────────────────────────────────────────────

    [Test]
    public async Task Scanner_FindsTheGuardedProject()
    {
        string? root = RepoPaths.RepoRoot;
        await Assert.That(root).IsNotNull()
            .Because("The walk needs a repository root; without one this file guards nothing.");

        if (root is null)
        {
            return;
        }

        int files = EnumerateGuardedFiles(root).Count;
        await Assert.That(files).IsGreaterThan(50)
            .Because(
                $"The guarded Avalonia shell should hold well over 50 source files; found {files}. "
                + "A near-zero count means the path is stale and the rule enforces nothing.");
    }

    [Test]
    public async Task Detector_FiresOnAKnownFork_AndStaysQuietOnTheSeamCall()
    {
        // The exact pre-#672 shape, verbatim from the old constructor.
        const string knownFork = """
            public sealed partial class TerminalPaneViewModel
            {
                private void Open(string cwd)
                {
                    _pty = PtyProcess.Start(new PtyStartSpec(Title, Args: ["-i"], WorkingDirectory: cwd));
                }
            }
            """;

        // The post-#672 shape: the ViewModel only ever names the abstraction.
        const string seamCall = """
            public sealed partial class TerminalPaneViewModel
            {
                private void Open(string cwd)
                {
                    Result<ITerminalPane> started = _launcher.Launch(TerminalPaneRequest.ForDirectory(cwd));
                    if (started.IsFailure) { OutputText = started.Error; return; }
                    _pane = started.Value;
                }
            }
            """;

        // A non-spawn discard, of which this repo has hundreds: the rule must not
        // fire on `_ =` alone, or it cries wolf and gets deleted.
        const string unrelatedDiscard = """
            public void Flush() => _ = _buffer.Append(text);
            public void Dispatch() => _ = _store.Dispatch(msg);
            """;

        await Assert.That(DetectIn("Known.cs", knownFork)).IsNotEmpty()
            .Because("the pre-#672 spawn shape must be detected, or the rule guards nothing");
        await Assert.That(DetectIn("Seam.cs", seamCall)).IsEmpty()
            .Because(
                "the seam call is the shape this issue converts TO. If it is flagged the rule forbids the "
                + "fix as well as the bug, and the only way to make CI green would be to widen or delete it.");
        await Assert.That(DetectIn("Unrelated.cs", unrelatedDiscard)).IsEmpty()
            .Because("a plain `_ =` discard is not a process spawn; the rule must stay quiet on it");
    }

    [Test]
    public async Task Detector_IsNotDefeatedByRenamingTheLocal()
    {
        // The old code could have been written with any local name. Keying the rule
        // on a variable name would have been the easy way to write this guard, and it
        // would have been worthless.
        const string renamed = """
            public void Open(string cwd)
            {
                var session = PtyProcess.TryStart(new PtyStartSpec(Shell, Args: ["-i"], WorkingDirectory: cwd));
                _ = session;
            }
            """;

        await Assert.That(DetectIn("Renamed.cs", renamed)).IsNotEmpty()
            .Because(
                "the rule keys on the spawn call, not on a variable name — otherwise a rename silently "
                + "un-guards the very line it was written for");
    }

    [Test]
    public async Task Detector_IgnoresTheExplanationInProse()
    {
        // The refactor's own comments name the forbidden call. A guard that fails on
        // its own documentation is a guard nobody keeps.
        const string prose = """
            // #672: this used to call PtyProcess.Start(new PtyStartSpec(...)) directly.
            /// <summary>Launches through the seam; never PtyProcess.Start here.</summary>
            public void Open() => _ = _launcher.Launch(request);
            """;

        await Assert.That(DetectIn("Prose.cs", prose)).IsEmpty()
            .Because(
                "line comments are stripped before matching, so documenting the old shape does not "
                + "reintroduce it");
    }

    private static List<string> EnumerateGuardedFiles(string root)
    {
        List<string> found = [];
        foreach (string project in GuardedProjects)
        {
            string dir = Path.Combine(root, project);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}.worktrees{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                found.Add(file);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }
}
