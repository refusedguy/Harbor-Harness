// SessionStatusFrameReachabilityRules.cs — GUARD for issue #861.
//
// THE DEFECT IS NOT A BUG IN THE PRODUCT. IT IS A CLAIM IN A TEST.
// ------------------------------------------------------------------
// #687 removed the transcript heuristic from `ChatStreamingPresenter.DeriveStatus`
// and made the reducer the single source of a session's status. The product side
// is correct and this file does not dispute it:
//
//   src/Harbor.Ui.Framework.Projection/Rendering/ChatStreamingPresenter.cs:50
//       public SessionStatus DeriveStatus(UiState state) => state.Chat.SessionStatus;
//
// What #861 reports is that the TEST named after that fix could never have caught
// its regression:
//
//   tests/Harbor.Ui.Framework.Tests/SessionStatusSingleSourceTests.cs:108
//       public async Task WhateverTheReducerDecided_IsWhatTheNextFramePushes()
//
// The name and the comment promise a FRAME guarantee — "ChatViewModel.RenderFrameTick
// calls DeriveStatus on every 16 ms frame and writes the answer into
// SessionStatusTracker, so the presenter is the last writer standing on that
// tracker's value" — and the body delivers a PURE-READ guarantee:
//
//   Presenter.DeriveStatus(decided) == SessionStatus.Error
//
// Both assertions are true. `DeriveStatus` really is a read. And that is exactly
// the problem: the assertions cannot fail if the frame is deleted. The production
// code makes the same unverified claim (ChatViewModel.cs:139-141: "This is the
// last writer standing on the tracker's value, so anything that recomputed a
// verdict here … became a second opinion"), and NOTHING in the suite checks that
// the writer is still there.
//
// THE DEATH THIS FILE MAKES UNREACHABLE
// ------------------------------------
// Rewrite one line, at apps/Harbor.App.Avalonia/ViewModels/ChatViewModel.cs:142:
//
//     _sessionManager.SetStatus(activeSession.Id, SessionStatus.Idle);
//
// and the presenter becomes DEAD CODE: nothing in `src/` or `apps/` calls
// `DeriveStatus` any more. `SessionStatusSingleSourceTests` stays green, because
// it holds its own `new ChatStreamingPresenter()` field and asks the presenter a
// question no product path routes to it any more.
//
// This is #857's shape exactly (see `PanelKeyRouteReachabilityRule`), on a seam
// that is ALIVE today. That is the difference worth stating: #857's routers are
// already dead and the rule ratchets them in a ledger; this seam is the last
// writer on a value the user sees, and the guard's job is to keep it that way —
// so it is a REACHABILITY rule, not a ledger.
//
// WHY A REACHABILITY RULE AND NOT A RENAME
// ----------------------------------------
// A rename would make the test honest and cost nothing. It is done here — the
// test is renamed in this PR — and it is not sufficient on its own, for a reason
// that is not about the test: after the rename the suite still contains NO
// assertion that the frame calls the presenter. A future edit to ChatViewModel
// is then free to inline a status, and nothing goes red. The claim moves from a
// test NAME (cheap to lie in) to a RULE (has to be re-derived to survive).
//
// WHY THIS IS NOT A NEW AXIS UNDER #555
// -------------------------------------
// #555 freezes new EXTENSION SEAMS — new interfaces a plugin or a widget can
// implement. This is a gate over code that already exists, in the project that
// already holds the other 20+ source gates. Two of the three perimeters it needs
// are reachable without adding anything:
//
//   * `tests/Harbor.Architecture.Tests` already text-scans `apps/` — the shared
//     `SourceScan.ProductTrees` is literally `["src", "apps"]`, and 20+ rules use
//     it. No ProjectReference to an app is needed to READ an app's source.
//   * `tests/Harbor.App.Avalonia.Tests` ALREADY EXISTS and ALREADY references
//     `apps/Harbor.App.Avalonia` (see its .csproj), and
//     `AvaloniaWorkspaceCommandsTests.CreateChatViewModel:145` already constructs
//     the real `ChatViewModel` over a real `UiRenderEngine` and a real
//     `ChatStreamingPresenter`. The perimeter this issue was filed as needing is
//     already built; nothing has to be founded to reach it.
//
// PERIMETER, DERIVED NOT TRANSCRIBED
// ----------------------------------
// Nothing here is a file list, and each part had to be derived from a FORM because
// the first reading of the tree refused every shortcut:
//
//   * the READERS are every method whose declared return type is `SessionStatus`,
//     found by shape — six of them, not the two a transcription of "the presenter"
//     would list, including `SessionStatusService.GetStatus` and
//     `SessionManager.GetStatus`, which are legitimate reads of the TRACKER;
//   * the two kinds are separated by the reader's PARAMETER TYPE, not its name: a
//     decision-publish read takes the `UiState` the reducer produced, a tracker
//     read takes a `string sessionId` and asks the store what it already holds;
//   * the FRAME is whoever CALLS a decision-publish reader — so a second frame
//     appearing is covered without an edit, which is the failure mode that made
//     #890 delete its `SeamFiles` transcription;
//   * the CLAIM is "a test drives the SEAM directly while its own project cannot
//     reach the frame", with reachability read from each test's `.csproj`.
//
// A VOCABULARY KEY WAS TRIED FIRST AND REJECTED ON THE MEASUREMENT, and the
// numbers are the argument:
//
//   * matching test NAMES for `Frame`                       → 107 tests, ~all
//     CellForge rendering tests where the word is literal and correct;
//   * narrowing to names where a frame is the ACTOR, i.e.
//     `Frame…Pushes` / `…Write…Frame`                        → 5 tests, 4 false
//     (`Flush_UsesSingleBackendWritePerFrame`, `EmptyFrame_WritesNothing`);
//   * naming the SEAM instead — the test calls a decision-publish read, and its
//     project references no `apps/` assembly                   → 2 tests, both
//     #861's own file, zero false positives.
//
// A perimeter built from vocabulary has to be re-argued at every unrelated render
// test. One built from the seam cannot be, because the seam is what the test is
// actually exercising.
//
// WHAT IS DELIBERATELY NOT RULED
// ------------------------------
//   * `SessionLifecycleService` writes `SessionStatus.Idle`/`.Error` straight into
//     the tracker at `:91`, `:160`, `:243`, bypassing the store. That writer is
//     REAL, it is a known-open debt of #687 (the test file's own header records
//     it), and fixing it is a behaviour change. This file does not rule on it and
//     does not pretend to — `TrackerWriters` names it so the debt is visible
//     instead of assumed away.
//   * A frame-level test would need `DispatcherTimer` handling. These rules need
//     none: they ask who CALLS the read, which is the question the test's name was
//     standing in for.
//
// NON-VACUITY, AND WHY THE CONTROL IS NOT CIRCULAR
// ------------------------------------------------
// A reachability gate has one degenerate failure mode: it reports nothing because
// its matcher stopped working, and an empty report is indistinguishable from a
// healthy one. Three controls close it, and each closes a DIFFERENT way of
// reporting nothing:
//
//   1. `Scan_IsLive` — the scan walked a checkout, found readers, and found FRAMES
//      (a zero here would leave the claim gate with nothing to match and passing).
//   2. `NonVacuity_TheMatcherSeparatesTheTwoKindsOfReader` — the classifier works,
//      so the gate is not passing because every reader looked like a tracker read.
//   3. `Probe_CanAcquireACallerItWasNeverGiven` — the POSITIVE CONTROL. It hands
//      the matcher a reader and a caller that do not exist in this checkout and
//      REQUIRES the reader back, reachable, attributed to the injected caller. A
//      transcription cannot be shown acquiring a file it was not given — that is
//      #890's acceptance criterion, and it is the criterion here.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One <c>SessionStatus</c>-returning method, and who calls it.</summary>
/// <param name="File">Repo-relative path, forward slashes.</param>
/// <param name="Line">1-based line of the declaration.</param>
/// <param name="Method">The method's name.</param>
/// <param name="ParameterType">
///     The declared type of the method's first parameter, or <c>null</c> when it
///     takes none. This is what separates the two kinds of reader — see
///     <see cref="PublishesTheDecision" />.
/// </param>
/// <param name="Callers">Product call sites, <c>file:line</c>.</param>
internal sealed record SessionStatusReader(
    string File,
    int Line,
    string Method,
    string? ParameterType,
    IReadOnlyList<string> Callers)
{
    /// <summary>Whether any product code calls this read.</summary>
    internal bool IsReachable => Callers.Count > 0;

    /// <summary>
    ///     Whether this read CARRIES THE REDUCER'S ANSWER, as opposed to reading
    ///     back what was already written.
    /// </summary>
    /// <remarks>
    ///     Derived from the parameter type, not from a list of names, because the
    ///     two kinds are genuinely different questions and a transcription cannot
    ///     tell them apart. A decision-publish read takes the <c>UiState</c> the
    ///     reducer produced — it is a projection of the decision, and the frame is
    ///     the only thing that can move it into the tracker. A tracker read takes a
    ///     <c>string sessionId</c> and asks the store what it already holds;
    ///     <c>SessionStatusService.GetStatus</c> and <c>SessionManager.GetStatus</c>
    ///     are that shape, they are reached from the session list and the board, and
    ///     requiring them to be called from "the frame" would be a category error.
    /// </remarks>
    internal bool PublishesTheDecision =>
        ParameterType is not null
        && ParameterType.EndsWith("UiState", StringComparison.Ordinal);
}

/// <summary>A product method that pushes the reducer's decision — i.e. the frame.</summary>
/// <param name="File">Repo-relative path of the frame method.</param>
/// <param name="Line">1-based line of its declaration.</param>
/// <param name="Method">The frame method's name.</param>
internal sealed record StatusFrameMethod(string File, int Line, string Method);

/// <summary>A test that DRIVES the seam directly while its project cannot reach the frame.</summary>
/// <param name="Test">Repo-relative path of the test file.</param>
/// <param name="Project">The test project that owns it.</param>
/// <param name="TestMethod">The test method's name.</param>
/// <param name="Reader">The decision-publish read it calls directly.</param>
internal sealed record UnreachableFrameClaim(
    string Test,
    string Project,
    string TestMethod,
    string Reader);

/// <summary>Everything the rules need from one repository scan.</summary>
/// <param name="Readers">Every <c>SessionStatus</c>-returning method found, with its callers.</param>
/// <param name="Frames">
///     Product methods that push the reducer's decision into the tracker — the
///     frame, discovered by finding who calls a decision-publish reader.
/// </param>
/// <param name="Claims">Tests that name a frame their own project cannot reach.</param>
/// <param name="PerimeterDirectories">Perimeter directories derived, repo-relative.</param>
/// <param name="MissingPerimeterDirectories">Derived perimeter directories absent from the checkout.</param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
/// <param name="TestFilesScanned">How many test-tree <c>.cs</c> files were read for the claim scan.</param>
/// <param name="TrackerWriters">Product sites that write a <c>SessionStatus</c> into the tracker.</param>
internal sealed record SessionStatusFrameReport(
    IReadOnlyList<SessionStatusReader> Readers,
    IReadOnlyList<StatusFrameMethod> Frames,
    IReadOnlyList<UnreachableFrameClaim> Claims,
    IReadOnlyList<string> PerimeterDirectories,
    IReadOnlyList<string> MissingPerimeterDirectories,
    int FilesScanned,
    int TestFilesScanned,
    IReadOnlyList<string> TrackerWriters);

/// <summary>
///     Finds every method that RETURNS a <c>SessionStatus</c> and counts the
///     product call sites that reach it.
/// </summary>
internal static partial class SessionStatusFrameProbe
{
    /// <summary>
    ///     The one file that DECIDES a status: the reducer that folds the core's
    ///     <c>AgentEvent</c>s into <see cref="ChatDomainState" />. Its
    ///     <c>SessionStatus</c>-returning helpers are the decision, not a read of
    ///     it, so they are excluded — otherwise the rule would demand that the
    ///     decision site be reachable from outside itself.
    /// </summary>
    internal const string DecisionFile =
        "src/Harbor.Ui.Framework.State/State/ChatAppReducer.cs";

    /// <summary>Directory names never descended into during the scan.</summary>
    private static readonly string[] SkippedDirectories =
        ["bin", "obj", "external", ".worktrees", "node_modules", "contrib"];

    /// <summary>
    ///     The perimeter: the same product trees every other source gate reads
    ///     (<c>src</c> + <c>apps</c>). Taken from the shared constant rather than
    ///     re-typed, so a tree added there is covered here without an edit.
    /// </summary>
    internal static IReadOnlyList<string> PerimeterDirectories() =>
        [.. SourceScan.ProductTrees];

    /// <summary>Walks the perimeter and pairs each reader with its callers.</summary>
    internal static SessionStatusFrameReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new SessionStatusFrameReport([], [], [], [], [], 0, 0, []);
        }

        IReadOnlyList<string> perimeter = PerimeterDirectories();
        var missing = new List<string>();
        var sources = new List<(string Relative, string[] Lines)>();
        int scanned = 0;

        foreach (string relativeDir in perimeter)
        {
            string absoluteDir = Path.Combine(
                repoRoot, relativeDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(absoluteDir))
            {
                missing.Add(relativeDir);
                continue;
            }

            foreach (string file in EnumerateSources(absoluteDir))
            {
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
                sources.Add((MakeRelative(repoRoot, file), lines));
            }
        }

        var readers = new List<SessionStatusReader>();
        foreach ((string relative, string[] lines) in sources)
        {
            if (relative == DecisionFile)
            {
                continue;
            }

            CollectReaders(relative, lines, sources, readers);
        }

        var frames = CollectFrames(readers, sources);
        var claims = CollectClaims(repoRoot, readers, frames);
        var writers = new List<string>();
        foreach ((string relative, string[] lines) in sources)
        {
            CollectWriters(relative, lines, writers);
        }

        return new SessionStatusFrameReport(
            readers, frames, claims, perimeter, missing, scanned, claims.Count, writers);
    }

    /// <summary>
    ///     The FRAME, discovered rather than named: the product methods that call a
    ///     decision-publish reader. Those are the code that can carry the reducer's
    ///     answer into the tracker, so they are the methods a test has to be able to
    ///     name honestly — and the ones this file's own gate keeps alive.
    /// </summary>
    /// <remarks>
    ///     Derived by pairing each decision-publish reader with its callers and
    ///     keeping the callers. A transcription would be the single name
    ///     <c>RenderFrameTick</c>, and that is precisely the form #890 deleted: it
    ///     can only notice the method being renamed, never a second frame appearing
    ///     where nobody edited the list.
    /// </remarks>
    internal static IReadOnlyList<StatusFrameMethod> CollectFrames(
        IReadOnlyList<SessionStatusReader> readers,
        IReadOnlyList<(string Relative, string[] Lines)> sources)
    {
        var frames = new List<StatusFrameMethod>();
        var seen = new HashSet<(string, string)>();

        foreach (SessionStatusReader reader in readers.Where(r => r.PublishesTheDecision))
        {
            foreach (string caller in reader.Callers)
            {
                string file = caller[..caller.LastIndexOf(':')];
                int line = int.Parse(caller[(caller.LastIndexOf(':') + 1)..],
                    System.Globalization.CultureInfo.InvariantCulture);

                string[] lines = sources.First(s => s.Relative == file).Lines;
                string text = string.Join("\n", SourceCommentStripper.StripAll(lines));

                // The frame is the METHOD that contains the call, not the call line.
                int declaration = EnclosingDeclaration(text, line);
                if (declaration < 0 || !seen.Add((file, text[declaration..])))
                {
                    continue;
                }

                Match name = MethodDeclaration().Match(text, declaration);
                if (name.Success)
                {
                    frames.Add(new StatusFrameMethod(file, LineOf(text, declaration), name.Groups["name"].Value));
                }
            }
        }

        return frames;
    }

    /// <summary>
    ///     Tests that DRIVE the seam directly while their own project cannot reach
    ///     the frame. This is #861's shape, stated as a rule.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The discriminator is the SEAM, not the word "frame", and getting that
    ///         wrong is the whole difficulty. Measured on this tree:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             Matching the test's NAME for <c>Frame</c> finds <b>107</b> tests,
    ///             nearly all CellForge rendering tests where the word is literal and
    ///             correct.
    ///         </item>
    ///         <item>
    ///             Narrowing to names where a frame is the ACTOR (<c>Frame…Pushes</c>)
    ///             still finds 5, four of them false: <c>Flush_UsesSingleBackendWritePerFrame</c>,
    ///             <c>EmptyFrame_WritesNothing</c>.
    ///         </item>
    ///         <item>
    ///             Naming the seam instead — the test calls a decision-publish read
    ///             at all, and its project references no <c>apps/</c> assembly —
    ///             finds <b>3</b>, of which one is this file's own control.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         A rule built on vocabulary has to be re-argued at every unrelated
    ///         render test; a rule built on the seam cannot. The reachability half is
    ///         read from the test's own <c>.csproj</c>, so a project that later takes
    ///         an <c>apps/</c> reference stops being a violator on its own.
    ///     </para>
    /// </remarks>
    internal static IReadOnlyList<UnreachableFrameClaim> CollectClaims(
        string repoRoot,
        IReadOnlyList<SessionStatusReader> readers,
        IReadOnlyList<StatusFrameMethod> frames)
    {
        var claims = new List<UnreachableFrameClaim>();
        if (frames.Count == 0)
        {
            return claims;
        }

        // Only the readers that publish the decision are the seam under test.
        HashSet<string> seam = readers
            .Where(r => r.PublishesTheDecision)
            .Select(r => r.Method)
            .ToHashSet(StringComparer.Ordinal);

        if (seam.Count == 0)
        {
            return claims;
        }

        string testsRoot = Path.Combine(repoRoot, "tests");
        if (!Directory.Exists(testsRoot))
        {
            return claims;
        }

        foreach (string file in EnumerateSources(testsRoot))
        {
            // A rule must not police its own fixtures, or its positive control is
            // a permanent offender — the reason SourceScan.IsBuildOutput rejects
            // `/tests/` at all.
            if (Path.GetFileName(file) is "SessionStatusFrameReachabilityRules.cs"
                or "SessionStatusFrameReachabilityTests.cs")
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            string? project = OwningProject(repoRoot, file);
            if (project is null || ReachesApps(repoRoot, project))
            {
                continue;
            }

            foreach ((string testMethod, IReadOnlyList<string> called) in TestMethods(lines))
            {
                foreach (string reader in seam)
                {
                    if (called.Contains(reader, StringComparer.Ordinal))
                    {
                        claims.Add(new UnreachableFrameClaim(
                            MakeRelative(repoRoot, file), project, testMethod, reader));
                    }
                }
            }
        }

        return claims;
    }

    /// <summary>
    ///     Each <c>[Test]</c> method in a file, with the members it calls — the
    ///     body up to the next <c>[Test]</c>, which is the shape these files use.
    /// </summary>
    internal static IEnumerable<(string Method, IReadOnlyList<string> Called)> TestMethods(
        string[] lines)
    {
        var starts = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("[Test]", StringComparison.Ordinal))
            {
                starts.Add(i);
            }
        }

        for (int index = 0; index < starts.Count; index++)
        {
            int from = starts[index];
            int to = index + 1 < starts.Count ? starts[index + 1] : lines.Length;
            string body = string.Join("\n", lines[from..to]);

            Match name = TestMethodName().Match(body);
            if (!name.Success)
            {
                continue;
            }

            var called = new List<string>();
            foreach (Match call in MethodCall().Matches(body))
            {
                called.Add(call.Groups["name"].Value);
            }

            yield return (name.Groups["name"].Value, called);
        }
    }

    /// <summary>A test method's declared name.</summary>
    [GeneratedRegex(
        @"public\s+(?:async\s+)?(?:Task|void)\s+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex TestMethodName();

    /// <summary>The test project directory a test file belongs to, or null if none.</summary>
    private static string? OwningProject(string repoRoot, string file)
    {
        string? dir = Path.GetDirectoryName(file);
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            return null;
        }

        string testsRoot = Path.Combine(repoRoot, "tests");
        string relative = Path.GetRelativePath(testsRoot, dir);
        return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative;
    }

    /// <summary>
    ///     Whether a test project references any <c>apps/</c> assembly — i.e.
    ///     whether it could name a frame at all.
    /// </summary>
    private static bool ReachesApps(string repoRoot, string project)
    {
        string dir = Path.Combine(repoRoot, "tests", project);
        if (!Directory.Exists(dir))
        {
            return false;
        }

        foreach (string csproj in Directory.GetFiles(dir, "*.csproj"))
        {
            string text;
            try
            {
                text = File.ReadAllText(csproj);
            }
            catch (IOException)
            {
                continue;
            }

            if (AppsProjectReference().IsMatch(text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Byte offset of the method declaration that encloses a 1-based line, or
    ///     <c>-1</c> when the line is not inside a method this matcher recognises.
    /// </summary>
    /// <remarks>
    ///     Found by taking the LAST declaration that starts at or above the line,
    ///     which is the enclosing one for the straight-line shape these files use.
    ///     The first implementation walked backwards over "blank or a declaration"
    ///     and found nothing on the real tree — the very call it was written for —
    ///     which is why this one enumerates forwards instead of guessing.
    /// </remarks>
    private static int EnclosingDeclaration(string text, int line)
    {
        int lineOffset = OffsetOfLine(text, line);
        if (lineOffset < 0)
        {
            return -1;
        }

        int best = -1;
        foreach (Match declaration in MethodDeclaration().Matches(text))
        {
            if (declaration.Index > lineOffset)
            {
                break;
            }

            best = declaration.Index;
        }

        return best;
    }

    /// <summary>Byte offset at which a 1-based line starts, or -1 if out of range.</summary>
    private static int OffsetOfLine(string text, int line)
    {
        if (line < 1)
        {
            return -1;
        }

        int offset = 0;
        for (int current = 1; current < line; current++)
        {
            int next = text.IndexOf('\n', offset);
            if (next < 0)
            {
                return -1;
            }

            offset = next + 1;
        }

        return offset;
    }

    /// <summary>A <c>ProjectReference</c> into <c>apps/</c>.</summary>
    [GeneratedRegex(@"ProjectReference[^>]*apps[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex AppsProjectReference();

    /// <summary>A method declaration, capturing its name.</summary>
    [GeneratedRegex(
        @"\b(?:public|private|protected|internal)\s+(?:static\s+|virtual\s+|override\s+|sealed\s+|async\s+|new\s+|partial\s+|extern\s+)*[\w<>\.\[\],\s]+?\s(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex MethodDeclaration();

    /// <summary>
    ///     Finds the <c>SessionStatus</c>-returning methods in one file and gives
    ///     each the product call sites that reach it. Exposed so the control below
    ///     drives the REAL matcher rather than a second implementation of it.
    /// </summary>
    internal static void CollectReaders(
        string relativeFile,
        string[] lines,
        IReadOnlyList<(string Relative, string[] Lines)> sources,
        List<SessionStatusReader> readers)
    {
        string text = string.Join("\n", SourceCommentStripper.StripAll(lines));

        foreach (Match match in SessionStatusReturningMethod().Matches(text))
        {
            string name = match.Groups["name"].Value;
            int line = LineOf(text, match.Index);
            string? parameterType = FirstParameterType(text, match.Index + match.Length - 1);
            var callers = new List<string>();

            foreach ((string otherRelative, string[] otherLines) in sources)
            {
                string otherText = string.Join("\n", SourceCommentStripper.StripAll(otherLines));
                foreach (Match call in MethodCall().Matches(otherText))
                {
                    if (call.Groups["name"].Value != name)
                    {
                        continue;
                    }

                    // The declaration itself is not a caller.
                    int callLine = LineOf(otherText, call.Index);
                    if (otherRelative == relativeFile && callLine == line)
                    {
                        continue;
                    }

                    callers.Add($"{otherRelative}:{callLine}");
                }
            }

            readers.Add(new SessionStatusReader(relativeFile, line, name, parameterType, callers));
        }
    }

    /// <summary>
    ///     The declared type of a method's FIRST parameter, read out of the source.
    /// </summary>
    /// <remarks>
    ///     This is what lets the rules tell a decision-publish read (takes
    ///     <c>UiState</c>) from a tracker read (takes <c>string sessionId</c>)
    ///     without naming either. A matcher that cannot read it returns <c>null</c>,
    ///     which classifies every reader as a tracker read — the gate then passes
    ///     vacuously, so <see cref="NonVacuity_TheMatcherReadsAParameterType" /> in
    ///     the tests exists to close exactly that.
    /// </remarks>
    internal static string? FirstParameterType(string text, int openParen)
    {
        int depth = 0;
        for (int i = openParen; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return null;
                }

                continue;
            }

            if (depth != 1 || char.IsWhiteSpace(c) || c == ',')
            {
                continue;
            }

            // Walk the qualified type name.
            int start = i;
            while (i < text.Length
                   && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '_' or '<' or '>'))
            {
                i++;
            }

            string token = text[start..i].TrimEnd('<', '>');
            if (token.Length == 0)
            {
                continue;
            }

            // A parameter is (type, name) or a single bare token — `UiState state`
            // or `CancellationToken`. Either way the token just walked IS the type;
            // scanning on to read the name would return the name instead, and every
            // reader would then classify as a tracker read.
            return token;
        }

        return null;
    }

    /// <summary>
    ///     Records every product site that writes a <c>SessionStatus</c> into a
    ///     tracker or manager. Used to name the writers, so the one the frame owns
    ///     and the ones #687 left open are both visible.
    /// </summary>
    internal static void CollectWriters(string relativeFile, string[] lines, List<string> writers)
    {
        string text = string.Join("\n", SourceCommentStripper.StripAll(lines));

        foreach (Match match in StatusWrite().Matches(text))
        {
            writers.Add($"{relativeFile}:{LineOf(text, match.Index)}");
        }
    }

    /// <summary>
    ///     A method declaration whose RETURN TYPE is <c>SessionStatus</c>. The
    ///     return type is the shape: a renderer that READS roles is fine and a
    ///     reducer that ASSIGNS one is fine — handing a status OUT is what the
    ///     frame has to keep doing.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:public|private|protected|internal)\s+(?:static\s+|virtual\s+|override\s+|sealed\s+|async\s+|new\s+|partial\s+|extern\s+)*SessionStatus\s+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex SessionStatusReturningMethod();

    /// <summary>A call of the form <c>receiver.Name(</c> or <c>Type.Name(</c>.</summary>
    [GeneratedRegex(@"\b[A-Za-z_][\w\.]*\.(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex MethodCall();

    /// <summary>A write of a status into a tracker or session manager.</summary>
    [GeneratedRegex(@"\bSetStatus\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex StatusWrite();

    /// <summary>Walks a directory for sources, skipping build output and nested checkouts.</summary>
    private static IEnumerable<string> EnumerateSources(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(dir, "*.cs");
                subdirs = Directory.GetDirectories(dir);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                yield return file;
            }

            foreach (string subdir in subdirs)
            {
                string name = Path.GetFileName(subdir);
                if (!SkippedDirectories.Contains(name, StringComparer.Ordinal))
                {
                    pending.Push(subdir);
                }
            }
        }
    }

    /// <summary>Repo-relative, forward-slashed, for a stable failure message.</summary>
    private static string MakeRelative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>1-based line number of a match within already-joined text.</summary>
    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}