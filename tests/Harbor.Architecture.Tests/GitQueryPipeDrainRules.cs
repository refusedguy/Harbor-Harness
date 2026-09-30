// GitQueryPipeDrainRules.cs — the guard for #884.
//
// THE INCIDENT
// ------------
// `ProcessGitQuery.RunGit` built a `ProcessStartInfo` with
// `RedirectStandardOutput = true` AND `RedirectStandardError = true`, then called
// `process.WaitForExit(3s)` and only afterwards
// `process.StandardOutput.ReadToEnd()`. Nothing ever read stderr, and stdout was
// read after the wait rather than during it.
//
// A redirected pipe is a bounded buffer. A child that writes past its capacity
// blocks in `write()`; nobody is reading, so it never drains; the exit event
// never fires; `WaitForExit` returns false when the ceiling expires; the child is
// killed; and the caller receives an empty string, which is byte-for-byte the same
// answer as "this directory is not a git repository". A healthy repo with a large
// working tree, and a directory that is not a repo at all, were one thing: no
// branch badge.
//
// #884 asks whether git actually writes to stderr on the four commands that are
// called, and records that it does not know. The question is worth answering, and
// the answer does not change the fix:
//
//   * Outside a repository `git rev-parse --abbrev-ref HEAD` writes one
//     `fatal: not a git repository` line to stderr and exits non-zero — far below a
//     pipe buffer, so on THAT path the unread redirect was harmless. NOT VERIFIED
//     by running git: `git` is off-limits on this host, and the claim is read off
//     documented behaviour rather than measured.
//   * It does not matter, because the defect is a property of the CALLER. The
//     caller cannot bound how much a child writes, and `git status --porcelain`
//     grows one line per changed path. stdout had the identical exposure and is
//     the likelier stream to overrun, which is why the fix drains both instead of
//     deleting `RedirectStandardError = true` the way #708 did to the notification
//     runner. `ProcessGitQuery`'s class remarks give the second reason (a
//     fullscreen cell renderer must not have a child write to the user's stderr).
//
// WHY A SOURCE SCAN AND NOT A BASELINE ROW
// ----------------------------------------
// `BannedSymbols.txt` names framework symbols, and "this method must not leave a
// redirected pipe unread" is not a symbol — the same reason #589 needed
// `TuiReadLineContractRules` rather than a banned API. The shape is invisible to a
// reference matrix (the pipe is a BCL type) and to a capability probe over
// Presentation assemblies: `ProcessGitQuery` is Application, and it is ALLOWED to
// fork — that was the fix for #537. So this is a source rule over the port's
// implementation, in the shape `TuiReadLineContractRules` uses.
//
// WHY THE SCOPE IS THE PORT, NOT THE TREE
// ----------------------------------------
// The invariant is a property of `IGitQuery`, so the scan is: every `*.cs` under
// `src/`+`apps/` that MENTIONS `IGitQuery`, minus build output. A second
// implementation in a new folder is covered the day it is written. A
// hand-maintained file list would not be, and a hand-maintained list is precisely
// the shape #578 says ages silently.
//
// This is also the honest scope, and the two sites it excludes are named here
// rather than left for a reader to assume they are clean. The same rule run over
// the whole tree flags two more spawns today:
//   * `src/Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs:261` — `git ls-files`
//     redirects stderr, never reads it, and additionally reads stdout only after
//     `WaitForExit`. The mirror image of this bug, on the other git spawn.
//   * `src/Harbor.Lsp/LspServerSession.cs:60` — a language server, which is chatty
//     on stderr by design, whose stderr nobody pumps.
//
// Neither is an `IGitQuery` implementation and neither is this issue. Widening
// this file to grade them also needs the extractor fixed first — see LIMITS.
//
// WHAT IS RULED
// -------------
//   1. UNREAD PIPE. A member that redirects stdout or stderr must touch that
//      stream: a read, an async read, a data-received event, or a hand-off of the
//      base stream. Red without the fix.
//   2. READ-AFTER-WAIT. In a member that redirects a stream AND calls the
//      SYNCHRONOUS `WaitForExit(`, that stream must be touched before the call.
//      Reading afterwards is the same deadlock with one more step in it, and it is
//      what the old `RunGit` did to stdout.
//   3. DISCOVERY. The scan reaches the port's files and at least one spawn site in
//      them, so neither rule can pass because the probe read nothing.
//   4. NON-VACUITY, five ways: each rule is run against synthetic positive and
//      negative controls (the #884 spelling, the drained spelling, wait-then-read,
//      read-then-wait, and a member that never waits synchronously), and both are
//      run against `WorkspaceInspector.RunGitAsync` — a REAL member in the same
//      assembly that already drains both pipes correctly. An extractor that
//      silently returned no block would leave every negative assertion green for
//      the wrong reason, and that is the failure mode this file exists to avoid.
//
// LIMITS, STATED RATHER THAN HIDDEN
// ---------------------------------
//   * The unit graded is the innermost brace-balanced block containing the
//     `ProcessStartInfo` TOKEN, found in comment-stripped source so that prose
//     cannot supply a subject. For every spawn shape in the port that block is the
//     method body, which also contains the object initializer — so a redirect and
//     the drain that answers it are seen together. A psi built inside a nested
//     `if`/`foreach` would be graded on that inner block, and a hand-off to
//     another type (`McpProcessClient`, which pumps stderr for its caller) would
//     read as a violation. `McpRegistry` is that shape today; it is one more
//     reason the scope stops at the port.
//   * Rule 2 is textual about ORDER, not about threading. A member that names a
//     stream early for an unrelated reason and reads it after the wait would pass.
//     No shape in the port does that, and the alternative — an IL rewriter — is a
//     different tool.
//   * String literals are not stripped, which is the documented behaviour of
//     `SourceScan.StripComments` and matches every other gate here.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #884: an <c>IGitQuery</c> implementation must not redirect a child-process pipe
///     it never reads, and must not read one only after waiting for the child. See
///     the file header for the incident, the scope decision, and the limits.
/// </summary>
public sealed class GitQueryPipeDrainRules
{
    /// <summary>
    ///     A file in the port's blast radius mentions the port. Deliberately a mention
    ///     and not an implementation: a DI registration
    ///     (<c>services.AddSingleton&lt;IGitQuery, ProcessGitQuery&gt;()</c>) is how a
    ///     second implementation appears, and that file is exactly the one a
    ///     file-list-based scan would forget.
    /// </summary>
    private const string PortNeedle = "IGitQuery";

    /// <summary>The spawn under inspection.</summary>
    private const string SpawnNeedle = "ProcessStartInfo";

    /// <summary>
    ///     The SYNCHRONOUS wait, and only it. <c>WaitForExitAsync</c> does not contain
    ///     this substring (it is <c>WaitForExitAsync(</c>), which is the point: an async
    ///     wait does not leave the readers unstarted, so it is not what rule 2 grades.
    /// </summary>
    private const string SyncWaitNeedle = "WaitForExit(";

    /// <summary>
    ///     Ways a member can drain stdout. A bare <c>StandardOutput</c> covers a read,
    ///     an async read, and a hand-off of <c>.BaseStream</c>; the rest name the
    ///     event-driven drain the BCL offers, so a member is not forced to spell it one
    ///     particular way.
    /// </summary>
    private static readonly string[] StdoutDrains =
    [
        "StandardOutput",
        "BeginOutputReadLine",
        "OutputDataReceived",
        "OpenStandardOutput",
    ];

    /// <summary>Ways a member can drain stderr. The same vocabulary, per stream.</summary>
    private static readonly string[] StderrDrains =
    [
        "StandardError",
        "BeginErrorReadLine",
        "ErrorDataReceived",
        "OpenStandardError",
    ];

    /// <summary>
    ///     A redirect assignment, blanked out before rule 2 measures any position.
    ///     <c>RedirectStandardOutput = true</c> CONTAINS the substring
    ///     <c>StandardOutput</c>, so leaving it in place would let the redirect itself
    ///     satisfy "this stream is touched before the wait" and rule 2 would pass on
    ///     every member that has ever redirected anything. The replacement has the
    ///     match's length, so every index rule 2 computes still points at real source.
    /// </summary>
    private static readonly Regex RedirectAssignment = new(
        @"Redirect(?:StandardOutput|StandardError)\s*=\s*(?:true|false)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     A real, already-correct member in the same assembly, used as the
    ///     extractor's positive control. <c>WorkspaceInspector.RunGitAsync</c> reads
    ///     both pipes with <c>ReadToEndAsync</c> before it waits, and carries stderr
    ///     alongside stdout in its <c>GitOutput</c> record — the shape #884's fix
    ///     converges on.
    /// </summary>
    private const string DrainedPrecedentFile = "src/Harbor.Application/Sessions/WorkspaceInspector.cs";

    // ── Rule 1: no redirected pipe is left unread ───────────────────────────

    [Test]
    public async Task No_Redirected_Stream_Is_Left_Unread()
    {
        IReadOnlyList<string> violations = Scan(UnreadPipes);

        await Assert.That(violations).IsEmpty().Because(
            "A redirected pipe with no reader is a bounded buffer the child can overrun, and an "
            + "overrun child blocks in write() forever: the exit event never fires, the wait returns "
            + "false at the ceiling, the child is killed, and the caller gets an empty string — "
            + "byte-for-byte the answer for 'not a git repository'. That is #884, on the branch "
            + "badge and on the jump palette's worktree list. Both streams must be read: stdout "
            + "because `git status --porcelain` grows a line per changed path, stderr because it "
            + "is where git says WHY, and dropping it made a non-zero exit unexplainable. Found: "
            + (violations.Count == 0 ? "(none)" : string.Join("\n", violations)));
    }

    // ── Rule 2: no redirected pipe is read only after the wait ──────────────

    [Test]
    public async Task No_Redirected_Stream_Is_Read_Only_After_The_Wait()
    {
        IReadOnlyList<string> violations = Scan(ReadAfterWait);

        await Assert.That(violations).IsEmpty().Because(
            "The synchronous WaitForExit overload is documented NOT to wait for asynchronous "
            + "readers, and a child blocked in write() to a full pipe is not going to exit for any "
            + "timeout to catch. The old RunGit shape — wait, then StandardOutput.ReadToEnd() — "
            + "carries the identical exposure to rule 1 with one extra step in it, so a member that "
            + "starts its readers after the wait still has the #884 bug. Found: "
            + (violations.Count == 0 ? "(none)" : string.Join("\n", violations)));
    }

    // ── Rule 3: the scan can see its subject ───────────────────────────────

    [Test]
    public async Task The_Scan_Reaches_The_Port_And_Its_Spawn()
    {
        IReadOnlyList<string> files = FindPortFiles();
        int spawns = files.Sum(file => FindSpawnSites(file).Count);

        await Assert.That(files.Count).IsGreaterThanOrEqualTo(2).Because(
            "Non-vacuity for the discovery. Both rules grade members found in files that mention "
            + "IGitQuery, so a scan that matched nothing would report zero violations and both "
            + "would be green for the wrong reason. The port's definition, its Application "
            + "implementation and the DI registrations that wire it are all in the blast radius "
            + "today; finding fewer than 2 means the needle or the tree walk broke. Found "
            + files.Count + " files: " + Describe(files));

        await Assert.That(spawns).IsGreaterThanOrEqualTo(1).Because(
            "Non-vacuity for the member extractor, which is the part that can quietly do nothing: "
            + "a scan that finds port files but no ProcessStartInfo has nothing to grade, and 'no "
            + "violations' then means 'no subject'. ProcessGitQuery.RunGit is the port's one spawn "
            + "and it is in this checkout, so zero here is a broken extractor rather than a clean "
            + "tree. Found " + spawns + " spawn site(s): " + Flatten(files));
    }

    // ── Non-vacuity: the rules, against synthetic controls ──────────────────

    [Test]
    public async Task Unread_Pipe_Rule_Flags_The_884_Spelling()
    {
        // The code as it stood when #884 was filed: stderr redirected, never read.
        const string Before = """
            private static string? RunGit(string workingDir, params string[] args)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(psi);
                if (!process.WaitForExit(Timeout)) { process.Kill(); return null; }
                return process.StandardOutput.ReadToEnd();
            }
            """;

        await Assert.That(UnreadPipes(Before)).IsNotEmpty().Because(
            "Positive control for rule 1. This is #884 verbatim, and the rule exists to fail on it: "
            + "stderr is redirected and the member never names it. A miss here means the rule is "
            + "inert.");
    }

    [Test]
    public async Task Unread_Pipe_Rule_Accepts_The_Drained_Spelling()
    {
        const string After = """
            private string? RunGit(string workingDir, CancellationToken ct, params string[] args)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(psi);
                var stdout = new StringBuilder();
                process.OutputDataReceived += (_, e) => { if (e.Data is { } l) stdout.Append(l); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is { } l) Log(l); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(Timeout)) { process.Kill(); return null; }
                process.WaitForExit();
                return stdout.ToString();
            }
            """;

        await Assert.That(UnreadPipes(After)).IsEmpty().Because(
            "Negative control for rule 1: the shape ProcessGitQuery actually has after the fix must "
            + "pass, or the guard cannot be adopted.");
    }

    [Test]
    public async Task Read_After_Wait_Rule_Flags_Wait_Then_Read()
    {
        const string Bad = """
            private static string? RunGit(string dir, params string[] args)
            {
                var psi = new ProcessStartInfo { RedirectStandardOutput = true, RedirectStandardError = false };
                using var process = Process.Start(psi);
                if (!process.WaitForExit(Timeout)) { process.Kill(); return null; }
                return process.StandardOutput.ReadToEnd();
            }
            """;

        await Assert.That(ReadAfterWait(Bad)).IsNotEmpty().Because(
            "Positive control for rule 2, and it is a DIFFERENT defect from rule 1: this member does "
            + "not redirect stderr at all, so rule 1 stays silent, yet stdout is still read only "
            + "after the synchronous wait — the mirror-image deadlock on the stream #884 itself "
            + "names as the likelier way in. A guard that cannot fail here is grading presence, not "
            + "order.");
    }

    [Test]
    public async Task Read_After_Wait_Rule_Accepts_Read_Then_Wait()
    {
        const string Good = """
            private static async Task<string?> RunGitAsync(string dir, CancellationToken ct, params string[] args)
            {
                var psi = new ProcessStartInfo { RedirectStandardOutput = true, RedirectStandardError = true };
                using var process = Process.Start(psi);
                Task<string> outTask = process.StandardOutput.ReadToEndAsync(ct);
                Task<string> errTask = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                return await outTask;
            }
            """;

        await Assert.That(ReadAfterWait(Good)).IsEmpty().Because(
            "Negative control for rule 2 in its async form. The repository already contains this "
            + "shape — WorkspaceInspector.RunGitAsync is it verbatim — so a rule that failed it would "
            + "be demanding something this codebase has never written.");
    }

    [Test]
    public async Task Read_After_Wait_Rule_Ignores_A_Member_That_Never_Waits_Synchronously()
    {
        const string NoSyncWait = """
            private static async Task<string?> RunGitAsync(string dir, CancellationToken ct, params string[] args)
            {
                var psi = new ProcessStartInfo { RedirectStandardOutput = true, RedirectStandardError = true };
                using var process = Process.Start(psi);
                Task<string> outTask = process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                return await outTask;
            }
            """;

        await Assert.That(ReadAfterWait(NoSyncWait)).IsEmpty().Because(
            "Rule 2 is about the SYNCHRONOUS wait, because that is the only one that can leave the "
            + "readers unstarted. WaitForExitAsync does not contain the needle, so this must not be "
            + "reported — otherwise the rule would be flagging the correct async shape that four "
            + "other spawn sites in this repository already use.");
    }

    // ── Non-vacuity: the extractor, against a REAL correct member ───────────

    [Test]
    public async Task The_Real_Drained_Precedent_Passes_The_Same_Scanner()
    {
        string? code = PrecedentSource();

        await Assert.That(code).IsNotNull().Because(
            "The control file " + DrainedPrecedentFile + " has to be readable. A control that "
            + "cannot be read is not a control.");

        string block = InnermostBlockContaining(code!, SpawnNeedle);

        await Assert.That(block.Length).IsGreaterThan(0).Because(
            "The member extractor must find the brace-balanced block containing "
            + DrainedPrecedentFile + "'s ProcessStartInfo. This is the assertion that catches an "
            + "extractor regression: one that silently returns nothing leaves every negative "
            + "assertion in this file green for the wrong reason. If this goes red, the fix is the "
            + "EXTRACTOR, not WorkspaceInspector.");

        await Assert.That(UnreadPipes(block)).IsEmpty().Because(
            "WorkspaceInspector.RunGitAsync already reads both pipes with ReadToEndAsync before it "
            + "waits, and carries stderr in its GitOutput record. It is the shape ProcessGitQuery "
            + "converges on, so the scanner that fails the #884 spelling must pass it.");

        await Assert.That(ReadAfterWait(block)).IsEmpty().Because(
            "The same real member, second rule: it waits with WaitForExitAsync, which rule 2 does "
            + "not grade, and its readers start before even that. A member can satisfy rule 1 and "
            + "still break rule 2, so both are asserted against real source.");
    }

    // ── Probes ──────────────────────────────────────────────────────────────

    /// <summary>Rule 1 against one member: a redirected stream nobody in it touches.</summary>
    internal static IReadOnlyList<string> UnreadPipes(string source)
    {
        List<string> hits = [];
        string code = SourceScan.StripComments(source);

        // The redirect assignments are blanked out for the PRESENCE check, and that
        // is not cosmetic: `RedirectStandardError = true` CONTAINS `StandardError`,
        // so a naive substring search finds the redirect and concludes the stream is
        // read. The rule would then pass on the exact code it exists to fail. The
        // redirect itself is still detected, on the text before blanking.
        string positioned = StripRedirectAssignments(code);
        foreach ((string stream, string[] drains) in Streams())
        {
            if (!Redirects(code, stream) || FirstIndexOf(positioned, drains) >= 0)
            {
                continue;
            }

            hits.Add($"Redirect{stream} = true and nothing in this member reads {stream}.");
        }

        return hits;
    }

    /// <summary>
    ///     Rule 2 against one member: a redirected stream whose first touch comes after
    ///     the synchronous wait.
    /// </summary>
    internal static IReadOnlyList<string> ReadAfterWait(string source)
    {
        List<string> hits = [];
        string code = SourceScan.StripComments(source);
        string positioned = StripRedirectAssignments(code);

        int wait = positioned.IndexOf(SyncWaitNeedle, StringComparison.Ordinal);
        if (wait < 0)
        {
            return hits;
        }

        foreach ((string stream, string[] drains) in Streams())
        {
            if (!Redirects(code, stream))
            {
                continue;
            }

            int first = FirstIndexOf(positioned, drains);
            if (first < 0 || first <= wait)
            {
                continue; // absent is rule 1's finding, reported there and not twice
            }

            hits.Add($"{stream} is first touched after {SyncWaitNeedle} — a child that overruns the "
                     + "pipe blocks in write() before the wait is even entered.");
        }

        return hits;
    }

    /// <summary>Grade every member that spawns a process inside the port's blast radius.</summary>
    private static IReadOnlyList<string> Scan(Func<string, IReadOnlyList<string>> rule)
    {
        var violations = new List<string>();
        foreach (string file in FindPortFiles())
        {
            string? code = SourceScan.TryReadAllText(Absolute(file));
            if (code is null)
            {
                // A file this scan cannot read is a file it cannot grade. The discovery
                // assertion above is what stops that from being quiet.
                continue;
            }

            foreach ((int offset, string block) in MembersOf(code))
            {
                violations.AddRange(rule(block).Select(v => $"{file}:{LineOf(code, offset)}  {v}"));
            }
        }

        return violations;
    }

    /// <summary>Repo-relative paths of every product file that mentions the port.</summary>
    private static IReadOnlyList<string> FindPortFiles() =>
    [
        .. SourceScan.EnumerateProductCsFiles()
            .Where(file => SourceScan.TryReadAllText(file) is { } text
                          && text.Contains(PortNeedle, StringComparison.Ordinal))
            .Select(SourceScan.Relative)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>Every spawn site in the blast radius, as <c>file:line</c>.</summary>
    private static IReadOnlyList<string> FindSpawnSites(string relative)
    {
        string? code = SourceScan.TryReadAllText(Absolute(relative));
        if (code is null)
        {
            return [];
        }

        return [.. MembersOf(code).Select(m => $"{relative}:{LineOf(code, m.Offset)}")];
    }

    /// <summary>
    ///     Every member in <paramref name="code" /> that spawns a process, as the offset
    ///     of its <c>ProcessStartInfo</c> token and the block that encloses it.
    ///     Offsets are into the COMMENT-STRIPPED text, so line numbers reported from
    ///     them still point at the real source line.
    /// </summary>
    private static IReadOnlyList<(int Offset, string Block)> MembersOf(string code)
    {
        string stripped = SourceScan.StripComments(code);
        var members = new List<(int, string)>();
        int at = stripped.IndexOf(SpawnNeedle, StringComparison.Ordinal);
        while (at >= 0)
        {
            string block = InnermostBlockContaining(stripped, SpawnNeedle, at);
            if (block.Length > 0)
            {
                members.Add((at, block));
            }

            at = stripped.IndexOf(SpawnNeedle, at + SpawnNeedle.Length, StringComparison.Ordinal);
        }

        return [.. members];
    }

    private static IEnumerable<(string Stream, string[] Drains)> Streams()
    {
        yield return ("StandardOutput", StdoutDrains);
        yield return ("StandardError", StderrDrains);
    }

    /// <summary>Whether a member sets <c>Redirect&lt;stream&gt;</c> to <c>true</c>.</summary>
    private static bool Redirects(string code, string stream) =>
        Regex.IsMatch(code, $@"Redirect{stream}\s*=\s*true", RegexOptions.CultureInvariant);

    private static int FirstIndexOf(string code, IEnumerable<string> needles)
    {
        int best = -1;
        foreach (string needle in needles)
        {
            int at = code.IndexOf(needle, StringComparison.Ordinal);
            if (at >= 0 && (best < 0 || at < best))
            {
                best = at;
            }
        }

        return best;
    }

    /// <summary>
    ///     Blanks the redirect assignments, preserving length so every index measured
    ///     afterwards still refers to the real source.
    /// </summary>
    private static string StripRedirectAssignments(string code) =>
        RedirectAssignment.Replace(code, static m => new string(' ', m.Length));

    /// <summary>
    ///     The innermost brace-balanced block containing the <paramref name="needle" />
    ///     token. <paramref name="at" /> overrides the search when the caller already
    ///     knows the offset. Text handed in is expected to be comment-stripped already.
    /// </summary>
    internal static string InnermostBlockContaining(string text, string needle, int at = -1)
    {
        if (at < 0)
        {
            at = text.IndexOf(needle, StringComparison.Ordinal);
        }

        if (at < 0 || at >= text.Length)
        {
            return string.Empty;
        }

        // One pass for every block span, then the SMALLEST span covering `at`.
        // Nesting alone would not answer it: the answer needs the span, not the depth.
        var open = new Stack<int>();
        int bestOpen = -1;
        int bestLength = int.MaxValue;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                open.Push(i);
                continue;
            }

            if (c != '}' || open.Count == 0)
            {
                continue;
            }

            int start = open.Pop();
            if (start > at || i < at)
            {
                continue; // this block does not cover `at`
            }

            if (i - start < bestLength)
            {
                bestOpen = start;
                bestLength = i - start;
            }
        }

        return bestOpen < 0 ? string.Empty : text[bestOpen..(bestOpen + bestLength + 1)];
    }

    /// <summary>1-based line number of a character offset.</summary>
    private static int LineOf(string text, int at)
    {
        int line = 1;
        for (int i = 0; i < at && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>The control file's text, comment-stripped, or null outside a checkout.</summary>
    private static string? PrecedentSource() =>
        SourceScan.TryReadAllText(Absolute(DrainedPrecedentFile)) is { } text
            ? SourceScan.StripComments(text)
            : null;

    private static string Absolute(string repoRelative) =>
        Path.Combine(RepoPaths.RepoRoot ?? ".", repoRelative.Replace('/', Path.DirectorySeparatorChar));

    private static string Flatten(IReadOnlyList<string> files) =>
        Describe([.. files.SelectMany(FindSpawnSites)]);

    private static string Describe(IReadOnlyList<string> items) =>
        items.Count == 0 ? "(none)" : string.Join(", ", items);
}
