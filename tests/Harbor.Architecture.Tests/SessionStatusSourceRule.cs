// SessionStatusSourceRule.cs — GUARD for issue #687.
//
// THE CONVENTION BEING ENFORCED
// -----------------------------
// `SessionStatus` (idle / working / done / error / aborted) is a DOMAIN FACT
// decided once, by the core, around the run that establishes it. It is never
// re-decided downstream by looking at the transcript.
//
// The core really does write it meaningfully, and #687 was filed because the
// presentation layer was answering the same question a second time:
//
//   src/Harbor.Ui.Framework.Projection/Rendering/ChatStreamingPresenter.cs
//     public SessionStatus DeriveStatus(UiState state)
//     {
//         if (state.Chat.IsAgentRunning) return SessionStatus.Working;
//         if (string.Equals(state.Chat.Status, "error", ...)) return SessionStatus.Error;
//         if (state.Chat.Lines.Length > 0 && state.Chat.Lines[^1].Role == ChatRole.Assistant)
//             return SessionStatus.Done;
//         return SessionStatus.Idle;
//     }
//
// "The last transcript line is an assistant line, therefore the session is
// done" is a second opinion about a run the core already knows the end of. It
// does not have to agree with the core's own answer, and when it disagrees it
// is the transcript that is wrong: a run that ended in `AgentErrorEvent` can
// still be sitting on an assistant line, and the heuristic repaints a failed
// run green. It also competes with the writers —
// `SessionLifecycleService` sets `SessionStatus.Error` when a branch cannot be
// opened, and `SubAgentRunner` stamps `Working`/`Done`/`Error` around a real
// sub-agent run; `ChatViewModel.RenderFrameTick` overwrites both on the next
// 16 ms frame with whatever the heuristic returned.
//
// The two halves of the rule:
//
//   A. A method whose declared return type is `SessionStatus` may not read the
//      transcript's shape — a `ChatRole` value, or any `.Role` read. The
//      status is handed OVER, not recomputed. This is the exact shape #687
//      found, and it is graded per METHOD rather than per file on purpose:
//      `UiRenderEngine` legitimately reads roles all over (it PAINTS the
//      transcript) and also forwards one `SessionStatus` through a one-line
//      `DeriveStatus`, so a file-level rule would have flagged the renderer for
//      doing its job.
//
//   B. The decision site must stay a decision site: the reducer that turns the
//      core's events into UI state has to assign every lifecycle outcome
//      (`Working`, `Done`, `Error`, `Aborted`). Rule A alone is satisfied by a
//      presenter that always answers `Idle` — the same bug wearing a different
//      hat, this time a session that never turns green at all. B is what stops
//      the fix from being "delete the heuristic" instead of "read the fact".
//
// WHY A TEXT SCAN AND NOT A COMPILED CHECK
// ---------------------------------------
// The rule is "this method must not consult the transcript", which is a
// statement about a method BODY and survives into metadata only as IL that
// would need Cecil to walk (and Cecil method-granular keys break whenever
// Roslyn renumbers a state machine — the trade `PresentationCapabilityRules`
// documents). It is also a rule about a file the fix DELETES lines from, and
// a compiled check could not be landed first: with the heuristic still in the
// tree the rule is supposed to be red, which is the point of writing it here.
// Comments are stripped first (SourceCommentStripper, shared with #563/#663),
// because the presenter's XML doc quotes the very rule it violates and a
// scanner that read prose would grade the documentation as code.
//
// PERIMETER
// ---------
// Every `Layer.Presentation` src project as classified by
// `FullLayerMatrixTests.Matrix` (so a new Presentation project is covered the
// moment it gets a matrix row — no second list to forget) plus `apps/`.
// `Harbor.Ui.Framework.State` is Presentation, which is why the reducer is
// scanned: it is graded, and it passes because it ASSIGNS a status instead of
// returning one. `contrib/` is outside CI and outside support by owner
// decision, so it is neither scanned nor expected clean.
//
// WHAT THIS RULE DOES NOT CATCH
// -----------------------------
//   * A helper that returns a NAMED type wrapping a `SessionStatus`.
//     `SessionCardViewModel.DotState` maps a status onto `SessionDotState` and
//     is deliberately out of scope for the same reason #663 left it out: a
//     control's own presentation vocabulary is not a second domain answer, and
//     #663 moved it next to the enum it produces on purpose.
//   * A heuristic expressed over something other than the transcript — e.g.
//     "the last tool call failed" — inside a `SessionStatus`-returning method.
//     That is a judgement, not a shape. The brace/string walk below skips
//     string and char literals so a literal containing a brace cannot truncate
//     a body, but a re-decision with no role read in it is invisible here.
//   * The reducer re-acquiring a heuristic. It is scanned, and rule A would
//     catch a `SessionStatus`-RETURNING helper added to it — but a heuristic
//     written inline as an assignment inside `OnAgentEnd` would not, because
//     the rule grades returns.
//
// NON-VACUITY
// -----------
//   1. Scan_IsLive — the scan really walked a checkout: every perimeter
//      directory it derived still exists, files were read, the decision file
//      was located, and methods were graded. A renamed project or a moved
//      decision site turns this red rather than emptying the scan.
//   2. NonVacuity_Scan_DetectsATranscriptHeuristicInSyntheticSource — the
//      POSITIVE CONTROL. The probe is handed the #687 heuristic verbatim and
//      MUST report it, plus three snippets it must NOT: the post-fix pure read,
//      a `string`-returning renderer that reads roles, and a `string`-returning
//      status LABELLER (the #663 canonical table, which reads no roles). A
//      probe whose matchers stopped working reports nothing and the rule goes
//      green while enforcing nothing.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>One method that decides a <c>SessionStatus</c> from the transcript.</summary>
/// <param name="File">Repo-relative path, forward slashes.</param>
/// <param name="Line">1-based line of the method's declaration.</param>
/// <param name="Method">The method's name.</param>
/// <param name="Evidence">The transcript read that made it a violation.</param>
internal sealed record SessionStatusDerivationSite(
    string File,
    int Line,
    string Method,
    string Evidence);

/// <summary>Everything the rule needs from one repository scan.</summary>
/// <param name="DecisionFileFound">Whether the reducer that decides the status was located.</param>
/// <param name="DecisionSiteMembers">
///     <c>SessionStatus</c> members the decision site assigns, read out of the
///     decision file rather than re-typed here.
/// </param>
/// <param name="DerivedFromTranscript">Methods that returned a status derived from the transcript.</param>
/// <param name="PerimeterDirectories">Perimeter directories the scan derived, repo-relative.</param>
/// <param name="MissingPerimeterDirectories">Derived perimeter directories absent from the checkout.</param>
/// <param name="FilesScanned">How many <c>.cs</c> files were read.</param>
/// <param name="MethodsGraded">How many <c>SessionStatus</c>-returning methods had their body read.</param>
internal sealed record SessionStatusSourceReport(
    bool DecisionFileFound,
    IReadOnlyList<string> DecisionSiteMembers,
    IReadOnlyList<SessionStatusDerivationSite> DerivedFromTranscript,
    IReadOnlyList<string> PerimeterDirectories,
    IReadOnlyList<string> MissingPerimeterDirectories,
    int FilesScanned,
    int MethodsGraded);

/// <summary>
///     Finds <c>SessionStatus</c>-returning methods that read the transcript, and
///     reads the lifecycle outcomes the decision site assigns.
/// </summary>
internal static partial class SessionStatusSourceProbe
{
    /// <summary>
    ///     The one file allowed to DECIDE a status: the reducer that folds the
    ///     core's <c>AgentEvent</c>s into <see cref="ChatDomainState" />. It is
    ///     scanned, not trusted — see rule A.
    /// </summary>
    internal const string DecisionFile =
        "src/Harbor.Ui.Framework.State/State/ChatAppReducer.cs";

    /// <summary>
    ///     Every lifecycle outcome the decision site must be able to say. A run
    ///     has four terminal shapes, and "one place decides" means one place
    ///     decides all four — not that the other three were dropped.
    /// </summary>
    internal static readonly string[] RequiredOutcomes =
        ["Working", "Done", "Error", "Aborted"];

    /// <summary>Directory names never descended into during the scan.</summary>
    private static readonly string[] SkippedDirectories =
        ["bin", "obj", "external", ".worktrees", "node_modules"];

    /// <summary>
    ///     The perimeter: every Presentation src project per the layer matrix,
    ///     plus <c>apps/</c>. Derived, never re-typed — a project that joins
    ///     <c>Layer.Presentation</c> is covered without editing this file.
    /// </summary>
    internal static IReadOnlyList<string> PerimeterDirectories()
    {
        var dirs = new List<string> { "apps" };
        var projectDirs = RepoPaths.EnumerateRepoAssemblyNames();
        foreach (string assembly in FullLayerMatrixTests.PresentationLayerAssemblies())
        {
            dirs.Add("src/" + (projectDirs.TryGetValue(assembly, out string? dir) ? dir : assembly));
        }

        return [.. dirs.Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal)];
    }

    /// <summary>Walks the perimeter. A missing checkout scans nothing, which the rule reports as a failure.</summary>
    internal static SessionStatusSourceReport Scan(string? repoRoot)
    {
        if (repoRoot is null || !Directory.Exists(repoRoot))
        {
            return new SessionStatusSourceReport(false, [], [], [], [], 0, 0);
        }

        IReadOnlyList<string> perimeter = PerimeterDirectories();
        var missing = new List<string>();
        var derived = new List<SessionStatusDerivationSite>();
        int scanned = 0;
        int graded = 0;

        foreach (string relativeDir in perimeter)
        {
            string absoluteDir = Path.Combine(repoRoot, relativeDir.Replace('/', Path.DirectorySeparatorChar));
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
                graded += ScanSource(MakeRelative(repoRoot, file), lines, derived);
            }
        }

        (bool found, IReadOnlyList<string> members) = ReadDecisionSiteMembers(repoRoot);
        return new SessionStatusSourceReport(found, members, derived, perimeter, missing, scanned, graded);
    }

    /// <summary>
    ///     Scans already-read lines. Exposed so the positive control drives the
    ///     REAL matcher (comment stripping, signature match, body walk) instead of
    ///     a second implementation of it, which is the only way "it can fail"
    ///     means anything.
    /// </summary>
    /// <returns>How many <c>SessionStatus</c>-returning methods were graded.</returns>
    internal static int ScanSource(
        string relativeFile,
        string[] lines,
        List<SessionStatusDerivationSite> derived)
    {
        string text = string.Join("\n", SourceCommentStripper.StripAll(lines));
        int graded = 0;

        foreach (Match match in SessionStatusReturningMethod().Matches(text))
        {
            string? body = ExtractBody(text, match.Index + match.Length - 1);
            if (body is null)
            {
                continue;
            }

            graded++;
            string? evidence = TranscriptRead(body);
            if (evidence is not null)
            {
                derived.Add(new SessionStatusDerivationSite(
                    relativeFile,
                    LineOf(text, match.Index),
                    match.Groups["name"].Value,
                    evidence));
            }
        }

        return graded;
    }

    /// <summary>
    ///     A method declaration whose RETURN TYPE is <c>SessionStatus</c>. The
    ///     return type is what makes this the defect's shape and not merely
    ///     "a file that mentions statuses": a renderer reading roles is fine, and
    ///     a reducer ASSIGNING one is fine — handing one out derived from the
    ///     transcript is not.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:public|private|protected|internal)\s+(?:static\s+|virtual\s+|override\s+|sealed\s+|async\s+|new\s+|partial\s+|extern\s+)*SessionStatus\s+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex SessionStatusReturningMethod();

    /// <summary>A read of a transcript role: a <c>ChatRole</c> value, or any <c>.Role</c> read.</summary>
    [GeneratedRegex(@"\bChatRole\s*\.", RegexOptions.CultureInvariant)]
    private static partial Regex ChatRoleValue();

    /// <summary>Any <c>.Role</c> member read — the transcript line's own field.</summary>
    [GeneratedRegex(@"\.\s*Role\b", RegexOptions.CultureInvariant)]
    private static partial Regex RoleMemberRead();

    /// <summary>Every <c>SessionStatus</c> member the decision site names.</summary>
    [GeneratedRegex(@"SessionStatus\s*\.\s*(?<member>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex SessionStatusMember();

    /// <summary>
    ///     The text of a method's body, starting at its parameter list's
    ///     <c>(</c>. Handles both shapes — a <c>{ … }</c> block and a
    ///     <c>=&gt; … ;</c> expression body — and skips string and char literals
    ///     while counting braces, so a literal holding a brace cannot truncate a
    ///     body into a clean-looking prefix.
    /// </summary>
    /// <returns>The body text, or <c>null</c> when it could not be bounded.</returns>
    private static string? ExtractBody(string text, int openParen)
    {
        int parens = 0;
        int i = openParen;
        for (; i < text.Length; i++)
        {
            if (SkipLiteral(text, ref i))
            {
                continue;
            }

            char c = text[i];
            if (c == '(')
            {
                parens++;
            }
            else if (c == ')')
            {
                parens--;
                if (parens == 0)
                {
                    i++;
                    break;
                }
            }
        }

        if (parens != 0)
        {
            return null;
        }

        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i >= text.Length)
        {
            return null;
        }

        // Expression body: `=> <expr>;`
        if (text[i] == '=' && i + 1 < text.Length && text[i + 1] == '>')
        {
            int braces = 0;
            int exprParens = 0;
            for (int k = i + 2; k < text.Length; k++)
            {
                if (SkipLiteral(text, ref k))
                {
                    continue;
                }

                char c = text[k];
                if (c == '{')
                {
                    braces++;
                }
                else if (c == '}')
                {
                    braces--;
                }
                else if (c == '(')
                {
                    exprParens++;
                }
                else if (c == ')')
                {
                    exprParens--;
                }
                else if (c == ';' && braces <= 0 && exprParens <= 0)
                {
                    return text[(i + 2)..k];
                }
            }

            return text[(i + 2)..];
        }

        // Block body: `{ … }`
        if (text[i] == '{')
        {
            int braces = 0;
            for (int k = i; k < text.Length; k++)
            {
                if (SkipLiteral(text, ref k))
                {
                    continue;
                }

                char c = text[k];
                if (c == '{')
                {
                    braces++;
                }
                else if (c == '}')
                {
                    braces--;
                    if (braces == 0)
                    {
                        return text[(i + 1)..k];
                    }
                }
            }

            return text[(i + 1)..];
        }

        return null;
    }

    /// <summary>
    ///     Advances past a string, verbatim-string or char literal starting at
    ///     <paramref name="i" />, leaving <paramref name="i" /> on the closing
    ///     quote. Returns <c>false</c> when the character is not a quote, in
    ///     which case <paramref name="i" /> is untouched.
    /// </summary>
    private static bool SkipLiteral(string text, ref int i)
    {
        char c = text[i];
        bool verbatim = c == '@' && i + 1 < text.Length && text[i + 1] == '"';
        if (c != '"' && c != '\'' && !verbatim)
        {
            return false;
        }

        char quote = verbatim ? '"' : c;
        i += verbatim ? 2 : 1;
        while (i < text.Length)
        {
            if (text[i] == '\\' && !verbatim)
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
            {
                // A doubled quote inside a verbatim string is an escaped quote,
                // not the end of the literal.
                if (verbatim && i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return true;
            }

            i++;
        }

        return true;
    }

    /// <summary>
    ///     The transcript read that turns a <c>SessionStatus</c>-returning method
    ///     into a violation, or <c>null</c> when the body consults no role.
    /// </summary>
    private static string? TranscriptRead(string body)
    {
        Match role = ChatRoleValue().Match(body);
        if (role.Success)
        {
            return role.Value.Trim();
        }

        Match field = RoleMemberRead().Match(body);
        return field.Success ? field.Value.Trim() : null;
    }

    /// <summary>
    ///     Reads the lifecycle outcomes the decision site assigns, out of the
    ///     decision file itself. A hand-typed list here would age exactly like
    ///     the table it grades.
    /// </summary>
    private static (bool Found, IReadOnlyList<string> Members) ReadDecisionSiteMembers(string repoRoot)
    {
        string absolute = Path.Combine(repoRoot, DecisionFile.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute))
        {
            return (false, []);
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(absolute);
        }
        catch (IOException)
        {
            return (false, []);
        }

        var members = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string line in SourceCommentStripper.StripAll(lines))
        {
            foreach (Match match in SessionStatusMember().Matches(line))
            {
                members.Add(match.Groups["member"].Value);
            }
        }

        return (true, [.. members]);
    }

    private static IEnumerable<string> EnumerateSources(string absoluteDir)
    {
        foreach (string path in Directory.EnumerateFiles(absoluteDir, "*.cs", SearchOption.AllDirectories))
        {
            bool skipped = SkippedDirectories.Any(dir =>
                path.Contains(Path.DirectorySeparatorChar + dir + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal));
            if (!skipped)
            {
                yield return path;
            }
        }
    }

    /// <summary>1-based line number of a character offset in the scanned text.</summary>
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

    private static string MakeRelative(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/');
}

/// <summary>
///     Guard for issue #687: a <see cref="SessionStatus" /> is decided by the
///     core around the run, handed to the presentation layer as a fact, and never
///     recomputed there from the transcript's shape.
/// </summary>
public sealed class SessionStatusSourceRule
{
    private static readonly Lazy<SessionStatusSourceReport> Report = new(
        () => SessionStatusSourceProbe.Scan(RepoPaths.RepoRoot));

    /// <summary>
    ///     No method that RETURNS a <see cref="SessionStatus" /> reads a
    ///     transcript role. The status is decided once, at the transition that
    ///     establishes it, and projected afterwards.
    /// </summary>
    [Test]
    public async Task TranscriptShape_NeverDecidesASessionStatus()
    {
        string[] offenders = [.. Report.Value.DerivedFromTranscript
            .Select(s => $"{s.File}:{s.Line} {s.Method}() reads {s.Evidence}")
            .OrderBy(s => s, StringComparer.Ordinal)];

        await Assert.That(string.Join(" | ", offenders)).IsEqualTo(string.Empty)
            .Because(
                "SessionStatus is a domain fact the core writes around a real run — SubAgentRunner "
                + "stamps Working/Done/Error around a sub-agent, SessionLifecycleService sets "
                + "Idle/Error on session events. Deciding it AGAIN from the transcript is a second "
                + "opinion that does not have to agree with the first, and when it disagrees it is "
                + "the transcript that is wrong: a run that ended in AgentErrorEvent can still be "
                + "sitting on an assistant line, so 'the last line is an assistant line' repaints a "
                + "failed run green and overwrites the Error a lifecycle transition just set. Read "
                + "the status the reducer decided (ChatDomainState.SessionStatus) instead. Offenders: "
                + (offenders.Length == 0
                    ? "(none — the scan graded no method at all, which its own liveness test covers)"
                    : string.Join(" | ", offenders)));
    }

    /// <summary>
    ///     The decision site can still say every outcome a run has. Without this
    ///     half, the rule above is satisfied by a presentation layer that always
    ///     answers <c>Idle</c> — the same bug with the heuristic deleted instead
    ///     of relocated.
    /// </summary>
    [Test]
    public async Task DecisionSite_AssignsEveryLifecycleOutcome()
    {
        SessionStatusSourceReport report = Report.Value;
        await Assert.That(report.DecisionFileFound).IsTrue()
            .Because($"{SessionStatusSourceProbe.DecisionFile} is where the core's AgentEvents become "
                   + "UI state and therefore the only place a status may be decided. If it moved, update "
                   + "DecisionFile in the same commit — do not delete the row");

        string[] missing = [.. SessionStatusSourceProbe.RequiredOutcomes
            .Where(o => !report.DecisionSiteMembers.Contains(o, StringComparer.Ordinal))
            .OrderBy(o => o, StringComparer.Ordinal)];

        await Assert.That(string.Join(" | ", missing)).IsEqualTo(string.Empty)
            .Because(
                "a run has four terminal shapes (working, done, error, aborted) and 'decided in one "
                + "place' has to mean all four of them are decided there. Dropping one to make a rule "
                + "pass is not a refactor — it moves the defect. Missing: "
                + (missing.Length == 0 ? "(none)" : string.Join(" | ", missing)));
    }

    /// <summary>
    ///     The scan really walked a checkout: every perimeter directory derived
    ///     from the layer matrix still exists, files were read, and methods were
    ///     graded. A renamed project or a moved decision site turns this red
    ///     instead of quietly emptying the scan.
    /// </summary>
    [Test]
    public async Task Scan_IsLive()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a repository checkout; without one it grades nothing and every "
                   + "rule in this file is satisfied by having nothing to look at");

        SessionStatusSourceReport report = Report.Value;

        await Assert.That(string.Join(" | ", report.MissingPerimeterDirectories)).IsEqualTo(string.Empty)
            .Because("the perimeter is derived from FullLayerMatrixTests.PresentationLayerAssemblies() "
                   + "plus apps/. A directory listed there but absent from the checkout means a project "
                   + "was renamed or moved, and the scan would be covering less than it claims. Missing: "
                   + (report.MissingPerimeterDirectories.Count == 0
                       ? "(none)"
                       : string.Join(" | ", report.MissingPerimeterDirectories)));

        await Assert.That(report.PerimeterDirectories.Count).IsGreaterThan(1)
            .Because("the perimeter is the Presentation src projects plus apps/; a single directory "
                   + "means the layer-matrix lookup returned nothing and the rule is grading one folder");

        await Assert.That(report.FilesScanned).IsGreaterThan(0)
            .Because("the scan read no .cs file inside the perimeter, so every rule here is vacuously green");

        await Assert.That(report.MethodsGraded).IsGreaterThan(0)
            .Because("no method with a SessionStatus return type was found. The rule grades method "
                   + "bodies; with no such method there is no body to grade and rule A cannot fail");
    }

    /// <summary>
    ///     THE POSITIVE CONTROL. The probe is handed the #687 heuristic verbatim
    ///     and MUST report it, plus three snippets it must NOT.
    /// </summary>
    [Test]
    public async Task NonVacuity_Scan_DetectsATranscriptHeuristicInSyntheticSource()
    {
        // The #687 shape, character for character.
        string heuristic = """
            public SessionStatus DeriveStatus(UiState state)
            {
                if (state.Chat.IsAgentRunning) return SessionStatus.Working;
                if (state.Chat.Lines.Length > 0 && state.Chat.Lines[^1].Role == ChatRole.Assistant)
                    return SessionStatus.Done;
                return SessionStatus.Idle;
            }
            """;

        // The fix: the status the reducer decided, read straight out of state.
        string pureRead = """
            public SessionStatus DeriveStatus(UiState state) => state.Chat.SessionStatus;
            """;

        // A renderer that PAINTS the transcript. Reading roles is its entire job,
        // and it does not return a status — this is UiRenderEngine's shape and
        // the reason rule A is graded per method, not per file.
        string renderer = """
            public string PaintLine(ChatLine line)
            {
                if (line.Role == ChatRole.ToolResult) return FormatResultPreview(line.Text);
                return line.Text;
            }
            """;

        // The #663 canonical table: a SessionStatus-to-label switch, which reads
        // no role and lives in the file the label rule already governs.
        string labeller = """
            public static string SessionStatusToText(SessionStatus status) => status switch
            {
                SessionStatus.Working => "working",
                SessionStatus.Done => "done",
                _ => "idle",
            };
            """;

        // The heuristic again, but with the role read spelled through a local, so
        // the control also pins that the walk reaches past the first `return`.
        string lateRead = """
            public SessionStatus DeriveStatus(UiState state)
            {
                if (state.Chat.IsAgentRunning) return SessionStatus.Working;
                var last = state.Chat.Lines.Length > 0 ? state.Chat.Lines[^1] : null;
                return last is not null && last.Role == ChatRole.Assistant
                    ? SessionStatus.Done
                    : SessionStatus.Idle;
            }
            """;

        var offenders = new List<SessionStatusDerivationSite>();
        SessionStatusSourceProbe.ScanSource("src/Harbor.Ui.Framework.Projection/Rendering/Presenter.cs",
            heuristic.Split('\n'), offenders);
        await Assert.That(offenders.Count).IsEqualTo(1)
            .Because("the first snippet is the #687 defect verbatim: a method that RETURNS a "
                   + "SessionStatus and reads the transcript's last role. A miss means the signature "
                   + "matcher or the body walk stopped working and rule A enforces nothing.");

        var late = new List<SessionStatusDerivationSite>();
        SessionStatusSourceProbe.ScanSource("src/Harbor.Ui.Framework.Projection/Rendering/Presenter.cs",
            lateRead.Split('\n'), late);
        await Assert.That(late.Count).IsEqualTo(1)
            .Because("the fifth snippet returns a status whose answer comes from a role read several "
                   + "lines below the first return. A walk that stopped at the first statement would "
                   + "grade the defect as clean, which is worse than not having the rule.");

        var reads = new List<SessionStatusDerivationSite>();
        SessionStatusSourceProbe.ScanSource("src/Harbor.Ui.Framework.Projection/Rendering/Presenter.cs",
            pureRead.Split('\n'), reads);
        await Assert.That(reads.Count).IsEqualTo(0)
            .Because("the second snippet is the post-fix shape: the status the reducer decided, read "
                   + "out of state. If the probe flags it, the rule cannot be satisfied by the fix it "
                   + "was written for");

        var painted = new List<SessionStatusDerivationSite>();
        SessionStatusSourceProbe.ScanSource("apps/Harbor.App.Avalonia/Services/UiRenderEngine.cs",
            renderer.Split('\n'), painted);
        await Assert.That(painted.Count).IsEqualTo(0)
            .Because("the third snippet reads roles but returns a string — it paints the transcript, "
                   + "which is a renderer's job. Grading it would make the rule flag every renderer "
                   + "for doing its work");

        var labelled = new List<SessionStatusDerivationSite>();
        SessionStatusSourceProbe.ScanSource(
            "src/Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs",
            labeller.Split('\n'), labelled);
        await Assert.That(labelled.Count).IsEqualTo(0)
            .Because("the fourth snippet is the #663 canonical status table. It switches on a status "
                   + "but returns a string, and SessionStatusTableRule already governs where it may "
                   + "live; claiming it here too would be a second rule grading one table");
    }
}
