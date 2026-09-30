// ChildProcessPipeDrainRules.cs — the guard for #884, widened for #908.
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
// fork — that was the fix for #537. So this is a source rule, in the shape
// `TuiReadLineContractRules` uses.
//
// #908: WHY THE SCOPE IS THE TREE, NOT THE PORT
// ---------------------------------------------
// #897 merged this guard scoped to the port — every `*.cs` under `src/`+`apps/`
// that MENTIONS `IGitQuery` — and named the two sites a wider rule would also
// flag, so the narrow scope read as a decision with a reason rather than as a
// claim that the tree was clean. Those two are `TreeTool.cs` and
// `LspServerSession.cs`, and #908 is the issue about them.
//
// The reason given for stopping at the port was that "the invariant is a property
// of `IGitQuery`". It is not, and that is the whole of #908. The invariant is:
//
//     a member that redirects a child pipe must drain that pipe for the whole
//     life of the child
//
// and it is a property of `Process.Start` — of the redirect, and of the child
// behind it. It is not a property of any port, because a port has no say in
// which streams a callee redirects. Scoping to a port therefore grades the sites
// that happen to sit behind that port and silently declines to grade every other
// spawn in the tree, which is precisely how `TreeTool` and `LspServerSession`
// survived #884: both are registered, both are live, and neither mentions
// `IGitQuery`.
//
// Measured, not assumed. The rules below are #897's, unchanged, run over every
// product `*.cs` instead of the port's ten files:
//
//   * 994 product files in scope, 12 spawn sites, versus 1 spawn site in the
//     port-scoped scope. The port was grading ONE of twelve.
//   * 4 rule-1 findings and 1 rule-2 finding. Two of them are the two real
//     defects (#908). One file — `McpRegistry` — is the hand-off shape #897
//     predicted and declared as a limit, now handled by rule 3 below.
//
// So the widening costs one new concept (rule 3, the hand-off) and two real
// fixes, and it does not add a single term to rules 1 and 2: neither of them has
// ever looked at the port. `UnreadPipes` and `ReadAfterWait` take a BLOCK. The
// port was only ever a file filter, and it was the filter — not the rule — that
// was wrong.
//
// WHY NOT A PORT INSTEAD (#555)
// -----------------------------
// The alternative is a new `IChildProcessRunner` that drains both pipes, which
// would make the rule port-scoped honestly. It is not taken, for a reason that is
// technical rather than one of taste, and the reason is that ONE port cannot
// honestly serve the two sites:
//
//   * `TreeTool.CollectGitTrackedFiles` is a bounded, SYNC, one-shot query that
//     has to hand back a `HashSet<string>` and is called from a sync method.
//     Draining it means the event-driven sync reader `ProcessGitQuery` uses, and
//     `BannedSymbols.txt` bans `TaskAwaiter<T>.GetResult` — so an async port would
//     put sync-over-async on this path.
//   * `LspServerSession.StartAsync` is an ASYNC, long-lived, bidirectional
//     process whose lifetime is owned by the session. Its drain is a pump task
//     cancelled on dispose, which is `McpProcessClient`'s shape, and there is no
//     sync spelling of that at all.
//
// Two ports, then, for one class of defect — a new axis twice, under a feature
// freeze. And a port would still not have covered `McpRegistry`, which builds a
// psi and hands it to a third type, or the next spawn somebody writes in a
// directory with no port in it. The rule that finally catches those is the one
// that reads the tree.
//
// WHAT IS RULED
// -------------
//   1. UNREAD PIPE. A member that redirects stdout or stderr must touch that
//      stream: a read, an async read, a data-received event, or a hand-off of the
//      base stream.
//   2. READ-AFTER-WAIT. In a member that redirects a stream AND calls the
//      SYNCHRONOUS `WaitForExit(`, that stream must be touched before the call.
//      Reading afterwards is the same deadlock with one more step in it, and it is
//      what the old `RunGit` did to stdout.
//   3. HAND-OFF. A member that redirects AND passes its `ProcessStartInfo` to
//      another type's constructor is not graded: the receiving type owns the
//      child and the drain. `McpRegistry` builds a psi and hands it to
//      `McpProcessClient`, which starts a stderr pump in its constructor. This is
//      the limit #897 declared, now given a name instead of a caveat.
//   4. DISCOVERY. The scan reaches the whole product tree, the port's own spawn is
//      still inside it, and rule 3 is load-bearing — so no rule can pass because
//      the probe read nothing, and the exemption cannot rot into a blanket
//      suppression while still matching nothing.
//   5. NON-VACUITY: each rule runs against synthetic positive and negative
//      controls, and against real members of both shapes — `WorkspaceInspector`
//      (drained, async), `McpProcessClient` (the hand-off), and the two #884 and
//      #908 spellings. An extractor that silently returned no block would leave
//      every negative assertion green for the wrong reason, and that is the
//      failure mode this file exists to avoid.
//
// LIMITS, STATED RATHER THAN HIDDEN
// ---------------------------------
//   * The unit graded is the innermost brace-balanced block containing the
//     `ProcessStartInfo` TOKEN, found in comment-stripped source so that prose
//     cannot supply a subject. When the token is in a method BODY that block is
//     the body, which also contains the object initializer — so a redirect and
//     the drain that answers it are seen together. That is the case at all
//     twelve spawn sites today.
//   * When the token is in a SIGNATURE, as in `McpProcessClient`'s constructor,
//     the block that opens before it is the enclosing CLASS body, not the
//     constructor, and the class body is what gets graded. `McpProcessClient`
//     comes out clean, and it is clean for the right reason by luck: its stderr
//     pump is a sibling member of the same class, so the class body names
//     `StandardError`. A constructor that drained from a type elsewhere would be
//     graded on a block that cannot see it. The consequence is bounded and stated
//     rather than hidden: a member whose psi is passed in is graded as the hand-off
//     it is (rule 3 exempts the CALLER, and the callee is the one that drains).
//   * Rule 2 is textual about ORDER, not about threading. A member that names a
//     stream early for an unrelated reason and reads it after the wait would pass.
//     No shape in the tree does that, and the alternative — an IL rewriter — is a
//     different tool.
//   * Rule 3 keys on the psi's IDENTIFIER reaching another constructor's argument
//     list. A hand-off through a field, a collection, or a wrapper method is not
//     recognised, and would read as a violation. That is the false-positive
//     direction, which is the safe one: it is loud.
//   * String literals are not stripped, which is the documented behaviour of
//     `SourceScan.StripComments` and matches every other gate here.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     A product member must not redirect a child-process pipe it never reads, and
///     must not read one only after waiting for the child. See the file header for
///     the incident, the scope decision (#908), and the limits.
/// </summary>
public sealed class ChildProcessPipeDrainRules
{
    /// <summary>
    ///     The spawn under inspection. This is also what makes the scope the TREE
    ///     rather than a port: a redirect is a property of <c>Process.Start</c>, so
    ///     the subject of the rule is this token and every member that builds one.
    /// </summary>
    private const string SpawnNeedle = "ProcessStartInfo";

    /// <summary>
    ///     The SYNCHRONOUS wait, and only it. <c>WaitForExitAsync</c> does not contain
    ///     this substring (it is <c>WaitForExitAsync(</c>), which is the point: an async
    ///     wait does not leave the readers unstarted, so it is not what rule 2 grades.
    /// </summary>
    private const string SyncWaitNeedle = "WaitForExit(";

    /// <summary>
    ///     The port #884's original spelling was scoped to, kept as an ASSERTION
    ///     rather than as a filter. #908 widened the scan to the whole tree; this
    ///     says the port's own spawn is still inside what is scanned, so the
    ///     widening cannot quietly stop grading the member that started all this.
    /// </summary>
    private const string PortNeedle = "IGitQuery";

    /// <summary>The port implementation whose spawn must remain in scope after the widening.</summary>
    private const string PortImplementationFile = "src/Harbor.Application/Git/ProcessGitQuery.cs";

    /// <summary>
    ///     A file in the port's blast radius mentions the port. Deliberately a mention
    ///     and not an implementation: a DI registration
    ///     (<c>services.AddSingleton&lt;IGitQuery, ProcessGitQuery&gt;()</c>) is how a
    ///     second implementation appears, and that file is exactly the one a
    ///     file-list-based scan would forget.
    /// </summary>
    /// <remarks>
    ///     #908: no longer used to FILTER the scan — the tree is scanned. Retained
    ///     only for <see cref="The_Port_Spawn_Is_Still_In_Scope" />, which is what
    ///     stops the widening from being a silent narrowing.
    /// </remarks>
    private static IReadOnlyList<string> FindPortFiles() =>
    [
        .. SourceScan.EnumerateProductCsFiles()
            .Where(file => SourceScan.TryReadAllText(file) is { } text
                          && text.Contains(PortNeedle, StringComparison.Ordinal))
            .Select(SourceScan.Relative)
            .Order(StringComparer.Ordinal)
    ];

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
    ///     The local a member builds its psi into: <c>var psi = new ProcessStartInfo</c>,
    ///     <c>ProcessStartInfo psi = …</c>, or a field assignment. Rule 3 needs the
    ///     name to recognise a hand-off of that exact object to another type.
    /// </summary>
    private static readonly Regex PsiAssignment = new(
        @"(\w+)\s*=\s*new\s+ProcessStartInfo",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     A constructor call and its argument list. Rule 3 pairs the type with the
    ///     psi identifier: <c>new McpProcessClient(psi)</c> is a hand-off,
    ///     <c>new Process { StartInfo = psi }</c> is not (no argument list at all), and
    ///     <c>new Process(psi)</c> cannot be the hand-off because the type is excluded.
    /// </summary>
    private static readonly Regex ConstructorCall = new(
        @"new\s+(\w+)\s*\(([^;{()]*(?:\([^()]*\)[^;{()]*)*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Types that take a psi and are NOT a hand-off, because they ARE the child.
    ///     <c>new Process(psi)</c> starts the process in this member, so this member
    ///     owns the drain and rule 3 must not excuse it.
    /// </summary>
    private static readonly HashSet<string> NotAHandOff = new(StringComparer.Ordinal)
    {
        "Process",
        "ProcessStartInfo",
    };

    /// <summary>
    ///     A real, already-correct member in the port's own assembly, used as the
    ///     extractor's positive control. <c>WorkspaceInspector.RunGitAsync</c> reads
    ///     both pipes with <c>ReadToEndAsync</c> before it waits, and carries stderr
    ///     alongside stdout in its <c>GitOutput</c> record — the shape #884's fix
    ///     converges on.
    /// </summary>
    private const string DrainedPrecedentFile = "src/Harbor.Application/Sessions/WorkspaceInspector.cs";

    /// <summary>
    ///     The real hand-off #897 predicted: a psi built in <c>McpRegistry</c> and
    ///     passed to <c>McpProcessClient</c>, whose constructor starts the stderr pump.
    ///     Rule 3 exists for this shape and rule 4 asserts rule 3 is still the reason
    ///     it is quiet.
    /// </summary>
    private const string HandOffFile = "src/Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs";

    /// <summary>
    ///     The receiving type of the one real hand-off, asserted by name. If somebody
    ///     renames it the exemption stops matching, <c>McpRegistry</c> goes RED, and
    ///     the decision has to be made again on purpose.
    /// </summary>
    private const string HandOffReceiver = "McpProcessClient";

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
            + "badge and on the jump palette's worktree list. It is also #908, twice: a `git "
            + "ls-files` whose stderr nobody reads, and a language server whose stderr nobody "
            + "pumps — and the second is the worse of the two, because nothing kills that child "
            + "and a server that blocks on write() simply stops answering. Both streams must be "
            + "read: stdout because it grows a line per changed path, stderr because it is where "
            + "the child says WHY, and dropping it made a non-zero exit unexplainable. Found: "
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
            + "starts its readers after the wait still has the #884 bug. `TreeTool` is that member: "
            + "it reads a `git ls-files` line-by-line only after the wait has already given up or "
            + "succeeded, under a comment that says reading asynchronously was the thing to avoid. "
            + "Found: " + (violations.Count == 0 ? "(none)" : string.Join("\n", violations)));
    }

    // ── Rule 3: a psi handed to another type is that type's to drain ────────

    [Test]
    public async Task A_Handed_Off_Psi_Is_Not_Graded_As_An_Unread_Pipe()
    {
        const string HandOff = """
            public McpProcessClient? GetProcess()
            {
                if (_startInfo is null) return null;
                var psi = new ProcessStartInfo
                {
                    FileName = _startInfo.Command,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                _process = new McpProcessClient(psi);
                return _process;
            }
            """;

        await Assert.That(UnreadPipes(HandOff)).IsEmpty().Because(
            "Negative control for rule 1 in the hand-off shape, and it is #897's declared limit "
            + "being given a name. This member redirects BOTH pipes and reads neither, which is "
            + "textually identical to the #884 bug — except that it never starts the child. It "
            + "hands the psi to McpProcessClient, whose constructor calls Process.Start and "
            + "immediately starts a stderr pump. Grading the caller for a drain the callee owns is "
            + "a false positive, and a permanently red false positive is how a guard gets deleted.");
    }

    [Test]
    public async Task A_Hand_Off_Exemption_Does_Not_Cover_A_Member_That_Starts_The_Process_Itself()
    {
        // `TreeTool`'s PRE-#908 spelling, kept as a fixture: the same redirects, but
        // `new Process` IS the child, so this member owns the drain and rule 3 must not
        // apply. It is the control the exemption must not be able to swallow, and it has
        // to keep the old signature — the point is the shape, not a member that exists.
        const string StartsItself = """
            private static HashSet<string> CollectGitTrackedFiles(string root)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = new Process { StartInfo = psi };
                p.Start();
                if (!p.WaitForExit(GitTimeoutMs)) { p.Kill(); throw new TimeoutException(); }
                while (p.StandardOutput.ReadLine() is { } line) { }
                return set;
            }
            """;

        IReadOnlyList<string> unread = UnreadPipes(StartsItself);
        IReadOnlyList<string> late = ReadAfterWait(StartsItself);

        await Assert.That(HandsOff(StartsItself)).IsFalse().Because(
            "Non-vacuity for rule 3 in the direction that matters. `new Process { StartInfo = psi }` "
            + "is the member CONSTRUCTING the child, so nothing downstream will drain it for it — "
            + "and `new Process` carries no argument list, so the hand-off pattern does not match it "
            + "either. If rule 3 ever widened to cover this, the exemption would be suppressing the "
            + "exact defect #908 is filed against, and the guard would go green on a deadlock.");

        await Assert.That(unread).IsNotEmpty().Because(
            "The member that starts the process itself is graded normally: stderr is redirected and "
            + "never named.");

        await Assert.That(late).IsNotEmpty().Because(
            "And rule 2 still reaches it — stdout is first touched after the synchronous wait.");
    }

    // ── Rule 4: the scan can see its subject, and the exemption is load-bearing

    [Test]
    public async Task The_Scan_Reaches_The_Whole_Product_Tree()
    {
        IReadOnlyList<string> files = ScannedFiles();
        int spawns = files.Sum(file => FindSpawnSites(file).Count);

        await Assert.That(spawns).IsGreaterThanOrEqualTo(12).Because(
            "Non-vacuity for the discovery, and the floor is a measurement rather than a round "
            + "number. #897's port-scoped scan found exactly ONE spawn site. The same extractor "
            + "over the same filter rules, with the port filter removed, finds 12: ProcessGitQuery, "
            + "WorkspaceInspector, ProcessNotificationRunner, DaemonCommand, SkillUpdater, "
            + "BashTool, RipGrepTool, McpProcessClient, McpRegistry, TreeTool and "
            + "LspServerSession. The port was grading one of twelve, which is the whole of #908. A "
            + "number below 12 means the tree walk broke and the quiet result means 'no subject'. "
            + "Found " + spawns + " spawn site(s): " + Flatten(files));
    }

    [Test]
    public async Task The_Port_Spawn_Is_Still_In_Scope()
    {
        IReadOnlyList<string> portFiles = FindPortFiles();

        await Assert.That(portFiles).IsNotEmpty().Because(
            "The port that started this is still mentioned in the product tree, so the #908 widening "
            + "has not quietly stopped looking at it.");

        await Assert.That(portFiles.Contains(PortImplementationFile)).IsTrue().Because(
            "And its implementation is still scanned, spawn and all. #908 widened the scope from the "
            + "port to the tree; a widening that dropped the port on the way would be a narrowing "
            + "dressed as one, and `ProcessGitQuery.RunGit` is the member #884 was filed about. Port "
            + "files: " + Describe(portFiles));

        await Assert.That(FindSpawnSites(PortImplementationFile)).IsNotEmpty().Because(
            "Non-vacuity for the extractor, on the one file the whole guard exists for: if the "
            + "ProcessStartInfo token stopped being found there, every other assertion in this file "
            + "would still be green and the guard would be grading nothing.");
    }

    [Test]
    public async Task The_Hand_Off_Exemption_Is_Load_Bearing()
    {
        var exempted = new List<string>();
        foreach (string file in ScannedFiles())
        {
            string? code = SourceScan.TryReadAllText(Absolute(file));
            if (code is null)
            {
                continue;
            }

            foreach ((int line, string block) in MembersOf(code))
            {
                if (HandsOff(block))
                {
                    exempted.Add($"{file}:{line}");
                }
            }
        }

        await Assert.That(exempted).IsNotEmpty().Because(
            "Non-vacuity for rule 3. An exemption that matches nothing is a rule that has quietly "
            + "stopped existing, and nothing else in this file would notice: the four sites rule 3 "
            + "protects would simply be absent from the violation list, which is what being green "
            + "looks like. Today exactly one site is a hand-off — McpRegistry — and it is why this "
            + "assertion exists.");

        await Assert.That(exempted.Any(site => site.StartsWith(HandOffFile, StringComparison.Ordinal)))
            .IsTrue().Because(
            "And the one real hand-off is the one #897 named, in the file it named. If the shape "
            + "moved or was fixed, this goes red and the exemption has to be re-decided rather than "
            + "left matching something nobody looked at. Found: " + Describe(exempted));

        // The exemption is keyed on the RECEIVER, so assert the receiver too: a member
        // that keeps its psi and stops handing it over must stop being exempt, and a
        // hand-off to some other type must not inherit this one's exemption by living
        // in the same file.
        string? handOffBlock = SourceScan.TryReadAllText(Absolute(HandOffFile)) is { } text
            ? InnermostBlockContaining(SourceScan.StripComments(text), SpawnNeedle)
            : null;

        await Assert.That(handOffBlock is not null
                          && handOffBlock.Contains($"new {HandOffReceiver}", StringComparison.Ordinal))
            .IsTrue().Because(
            "Rule 3 is keyed on the psi's identifier reaching another type's ARGUMENT LIST, and this "
            + "is the assertion that says which type. `McpRegistry` is exempt because it builds a "
            + "psi and passes it to " + HandOffReceiver + ", whose constructor starts the stderr "
            + "pump — not because it lives in this file, and not because it redirects something. If "
            + "the receiver is renamed, the exemption stops matching and the site goes RED, which is "
            + "the intended failure: a decision to stop handing off has to be written down.");
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

    // ── Non-vacuity: the extractor, against REAL members of both shapes ────

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

    [Test]
    public async Task The_Real_Hand_Off_Passes_The_Same_Scanner()
    {
        string? code = SourceScan.TryReadAllText(Absolute(HandOffFile)) is { } text
            ? SourceScan.StripComments(text)
            : null;

        await Assert.That(code).IsNotNull().Because(
            "The second real control, " + HandOffFile + ", has to be readable. Rule 3 was written "
            + "from its shape, so it is the one member where a wrong extractor and a wrong rule are "
            + "indistinguishable from the outside.");

        string block = InnermostBlockContaining(code!, SpawnNeedle);

        await Assert.That(HandsOff(block)).IsTrue().Because(
            "The real hand-off must be recognised as one. This is the only assertion that ties rule "
            + "3 to actual source rather than to a synthetic snippet — a synthetic control passes "
            + "just as well against a rule that has stopped matching anything. The receiver is "
            + HandOffReceiver + ", whose constructor is where the drain this exemption defers to "
            + "actually lives.");

        await Assert.That(UnreadPipes(block)).IsEmpty().Because(
            "And so it is not reported. McpProcessClient starts its stderr pump in the constructor, "
            + "so the psi's owner drains it.");
    }

    // ── Probes ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Rule 1 against one member. A member that handed its psi to the type which owns
    ///     the child (rule 3) reports nothing, so the exemption is applied HERE, at the
    ///     rule, rather than by the caller — otherwise the scan and the synthetic
    ///     controls would each have to remember it, and a guard whose correctness depends
    ///     on every call site remembering is a guard with the same defect it guards.
    /// </summary>
    internal static IReadOnlyList<string> UnreadPipes(string source)
    {
        if (HandsOff(source))
        {
            return [];
        }

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

    /// <summary>
    ///     Rule 3: does this member hand its psi to a type that owns the child?
    /// </summary>
    /// <remarks>
    ///     The psi's identifier is taken from its own assignment, so a DIFFERENT
    ///     <c>Process</c> constructed in the same member cannot be mistaken for the
    ///     hand-off — that is what keeps <c>new Process { StartInfo = psi }</c> graded,
    ///     since <c>Process</c> is excluded by name as well as by having no argument
    ///     list. Recognising a hand-off through a field, a collection or a wrapper
    ///     method is not attempted: the failure would be a false POSITIVE, which is
    ///     loud and cheap, where a false negative would be the deadlock this file
    ///     exists to catch.
    /// </remarks>
    internal static bool HandsOff(string source)
    {
        string code = SourceScan.StripComments(source);
        Match psi = PsiAssignment.Match(code);
        if (!psi.Success)
        {
            return false;
        }

        string identifier = psi.Groups[1].Value;
        foreach (Match call in ConstructorCall.Matches(code))
        {
            if (!NotAHandOff.Contains(call.Groups[1].Value)
                && call.Groups[2].Value.Contains(identifier, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Grade every member that spawns a process in the product tree.</summary>
    private static IReadOnlyList<string> Scan(Func<string, IReadOnlyList<string>> rule)
    {
        var violations = new List<string>();
        foreach (string file in ScannedFiles())
        {
            string? code = SourceScan.TryReadAllText(Absolute(file));
            if (code is null)
            {
                // A file this scan cannot read is a file it cannot grade. The discovery
                // assertion above is what stops that from being quiet.
                continue;
            }

            foreach ((int line, string block) in MembersOf(code))
            {
                violations.AddRange(rule(block).Select(v => $"{file}:{line}  {v}"));
            }
        }

        return violations;
    }

    /// <summary>
    ///     Every product file the rules are graded over. #908: this is the whole tree.
    ///     #897 had it filtered to the files mentioning <see cref="PortNeedle" />, which
    ///     found 1 spawn site; the unfiltered set finds 12, and the 11 that were not
    ///     being looked at are where both #908 defects live.
    /// </summary>
    private static IReadOnlyList<string> ScannedFiles() =>
    [
        .. SourceScan.EnumerateProductCsFiles()
            .Select(SourceScan.Relative)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>Every spawn site in a file, as <c>file:line</c>.</summary>
    private static IReadOnlyList<string> FindSpawnSites(string relative)
    {
        string? code = SourceScan.TryReadAllText(Absolute(relative));
        if (code is null)
        {
            return [];
        }

        return [.. MembersOf(code).Select(m => $"{relative}:{m.Line}")];
    }

    /// <summary>
    ///     Every member in <paramref name="code" /> that spawns a process, as the LINE of
    ///     its <c>ProcessStartInfo</c> token and the block that encloses it.
    /// </summary>
    /// <remarks>
    ///     The line is counted in the COMMENT-STRIPPED text, because the offsets are
    ///     offsets into that text — <c>SourceScan.StripComments</c> collapses a
    ///     <c>//</c> comment to a single space, so the stripped string is SHORTER than
    ///     the file and an offset measured against one cannot be looked up in the
    ///     other. Newlines are preserved by both, so counting in the stripped text
    ///     still names the real source line. Getting this wrong is not cosmetic: the
    ///     red run of this guard reported the violation at line 60 of a file whose
    ///     spawn is at line 106, and a failure message pointing at the wrong line is
    ///     half a guard.
    /// </remarks>
    private static IReadOnlyList<(int Line, string Block)> MembersOf(string code)
    {
        string stripped = SourceScan.StripComments(code);
        var members = new List<(int, string)>();
        int at = stripped.IndexOf(SpawnNeedle, StringComparison.Ordinal);
        while (at >= 0)
        {
            string block = InnermostBlockContaining(stripped, SpawnNeedle, at);
            if (block.Length > 0)
            {
                members.Add((LineOf(stripped, at), block));
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
            if (at >= 0 && (best < 0 or at < best))
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
    /// <remarks>
    ///     "Containing" is a span test, and that has one consequence worth naming: a
    ///     block whose OPENING BRACE sits after the token does not contain it. For a
    ///     <c>ProcessStartInfo</c> in a method body that never comes up, and the graded
    ///     block is the body. For one in a SIGNATURE — <c>McpProcessClient</c>'s
    ///     constructor parameter — the opening brace comes after it, so the smallest
    ///     covering span is the enclosing class body and the class body is what is
    ///     graded. See LIMITS.
    /// </remarks>
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

    /// <summary>
    ///     1-based line number of a character offset, counted in the text it is an
    ///     offset INTO.
    /// </summary>
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
