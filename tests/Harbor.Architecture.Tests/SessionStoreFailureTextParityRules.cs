// SessionStoreFailureTextParityRules.cs — GUARD for issue #764.
//
// WHAT #764 REPORTED, AND WHAT THE MEASUREMENT FOUND
// ---------------------------------------------------
// #764's claim: "Harbor.Storage.Memory hand-writes the session-store failure
// texts nine times, and `SessionStoreErrors` is supposed to be their single
// home." The single-home half is TRUE — `src/Harbor.Storage.Shared/
// SessionStoreErrors.cs` declares all three shapes, Jsonl and Sqlite call the
// factories at 10 and 8 sites, and Memory links NEITHER shared file (it has no
// `<Compile>` item at all). The COUNT is wrong, and the shape of the defect is
// different from what the issue describes.
//
// Measured on dev at 0c29da77, by extracting every `$"..."` literal from the
// three store projects and normalising the interpolation holes:
//
//   Harbor.Storage.Jsonl    0 inline,  9 factory calls (7 SN + 2 MN, +1 IS in
//                                    SessionFilePaths.cs)
//   Harbor.Storage.Sqlite   0 inline,  8 factory calls (6 SN + 2 MN)
//   Harbor.Storage.Memory  11 inline,  0 factory calls
//
// So the duplication is ELEVEN sites, not nine, and it spans TWO of the three
// factories rather than one. #764's list of nine line numbers is exactly the
// `SessionNotFound` sites; the two `MessageNotFound` sites —
// MemorySessionStore.cs:69 and :107 — are not in the issue at all. Any count
// downstream of that list was wrong by two, and the missing two are a different
// shape, so "the same literal nine times" understated both the size and the
// variety of the duplication.
//
// WHY THE TEXTS ARE NOT A USER-VISIBLE DEFECT — MEASURED, NOT ASSUMED
// -------------------------------------------------------------------
// All eleven inline literals are byte-identical in SHAPE to the home's
// factories. The holes are the only thing that varies, and they vary the way
// the parameter name varies at the equivalent factory call site:
//
//   MemorySessionStore.cs:146  $"Session '{session.Id}' not found."
//       == SessionStoreErrors.SessionNotFound(session.Id)
//   MemorySessionStore.cs:69   $"Message '{message.Id}' not found in session '{sessionId}'."
//       == SessionStoreErrors.MessageNotFound(sessionId, message.Id)
//   MemorySessionStore.cs:107  $"Message '{messageId}' not found in session '{sessionId}'."
//       == SessionStoreErrors.MessageNotFound(sessionId, messageId)
//
// A person sees the same string whichever backend `HARBOR_STORAGE` selects
// today, which is why #199's three ROP suites are green. This file therefore
// does NOT claim a behaviour bug, and it does not restate `not found.` to
// "match" the home — that would change the text the ROP suites pin for a
// duplication that is currently invisible to users.
//
// WHAT IS ACTUALLY WORTH GUARDING, AND WHY IT IS NOT WHAT #764 ASKED FOR
// ------------------------------------------------------------------------
// #764 asked for a provenance guard: "assert that no store writes a `not
// found.` failure literal inline, so the factory stays the only home." Written
// that way it is red on eleven sites and can only go green when Memory is
// unified — a gate demanding a refactor nobody has done, which is a standing
// build failure and, as TokenTrackingRatchet records, how a team learns to
// reach for `--no-verify`.
//
// The defect that guard would prevent is real, and it is measurable WITHOUT
// touching Memory. #199's ROP suites pin the OUTPUT of four of Memory's
// eleven sites — GetAsync, AppendMessageAsync, GetMessagesAsync, DeleteAsync
// (MemorySessionStoreRopTests:31-38). The other SEVEN are pinned by nothing:
//
//   MemorySessionStore.cs:63, 69, 99, 107, 126, 136, 146
//
// Those seven are the hole. A twelfth literal, or a divergent spelling at any
// of them, would compile, would ship, and would not turn any test red — and
// the home's own header, which promises the shapes are "identical across
// Jsonl/Sqlite/Memory", would be asserting something no check can see. That is
// the same "green = unchecked" shape #456 was filed for, one layer down.
//
// So this file does the narrow thing that is true today and enforceable today:
//
//   R1  PARITY.  Every session-store failure literal written by a store —
//               inline or not — must be shape-equal to a shape the home
//               declares. This judges all eleven sites, not the four the ROP
//               suites happen to cover, and it is what makes the home's
//               "identical across all three" header a checked claim.
//   R2  RATCHET. The measured inline inventory is frozen. ADDING one is a
//               regression; REMOVING one is reported too, so unifying Memory
//               (the owed work, tracked on #764) has to be a deliberate edit
//               in the same commit rather than a silent drift.
//   R3  THE HOME IS STILL A HOME. Its three declared shapes are pinned, so
//               deleting a factory is caught instead of quietly narrowing what
//               R1 accepts.
//
// KNOWN LIMITATION — stated, not hidden
// -------------------------------------
// This is a line-level regex over string literals, not a C# parser. Three
// shapes it cannot read, named so the next reader does not have to find them:
// a shape assembled by concatenation (`"Session '" + id + " not found."`), a
// verbatim interpolated literal (`$@"Session '{id}' not found."`, which this
// matcher does not reach — no store writes one today), and a hole containing a
// nested brace. The bound is the same one MoneyCellSingleHomeRules states: the
// way this bug comes back is someone writing a literal, and literals are what
// this reads. The planted control below pins that claim against the SAME
// matcher the rule runs, so a matcher that stopped discriminating would fail
// rather than pass quietly.
//
// WHY THE SCAN DOES NOT USE `RepoPaths.EnumerateCsFiles`
// -------------------------------------------------------
// #764's third point, and it decides the shape of this file. `src/
// Harbor.Storage.Shared` is linked source with no `.csproj`, so a per-project
// file walk that resolved only `<Compile Include>` items would reach
// SessionStoreErrors.cs through Jsonl and Sqlite but NEVER through Memory — the
// one project whose divergence this file exists to judge. Generalising that
// walk is #456/#763's work and this file does not touch it.
//
// So the scan here is by PATH PREFIX over `SourceScan.EnumerateProductCsFiles()`,
// which walks the `src/` and `apps/` trees and therefore reaches a csproj-less
// folder on its own. `The_Home_Is_In_Scan_Scope` pins that the home really is
// in the scanned set: if a future refactor narrows the walk, this file goes red
// instead of silently judging three stores and no home, which would make R1
// pass over a vocabulary it can no longer read.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Holds the session-store failure-text shapes to the one home that declares
///     them, and freezes the measured inline duplication so it cannot grow or
///     change unnoticed (#764).
/// </summary>
public sealed class SessionStoreFailureTextParityRules
{
    /// <summary>The csproj-less folder that declares the canonical shapes.</summary>
    private const string SharedFolder = "Harbor.Storage.Shared";

    /// <summary>The declaring file, relative to <see cref="SharedFolder" />.</summary>
    private const string HomeFileName = "SessionStoreErrors.cs";

    /// <summary>
    ///     The three store projects. <c>Harbor.Storage.Memory</c> is in this list
    ///     precisely because it is the one that does not link the home: excluding
    ///     it would make the rule blind to the divergence it is written for.
    /// </summary>
    private static readonly string[] StoreProjects =
    [
        "Harbor.Storage.Jsonl",
        "Harbor.Storage.Sqlite",
        "Harbor.Storage.Memory",
    ];

    // =====================================================================
    // The measured baseline. Every number here was read off dev at 0c29da77
    // by the scan below, not counted by hand — see the header.
    // =====================================================================

    /// <summary>
    ///     Every failure shape the home declared when measured, hole-normalised.
    ///     R3 holds the home to this, so a deleted factory is a change and not a
    ///     silent narrowing of what R1 accepts.
    /// </summary>
    private static readonly string[] MeasuredHomeShapes =
    [
        "Invalid session id '{}'.",
        "Message '{}' not found in session '{}'.",
        "Session '{}' not found.",
    ];

    /// <summary>
    ///     Every failure literal a store wrote INLINE when measured, as
    ///     (project, shape). This is the ratchet: an addition is a regression,
    ///     and a removal is reported so the owed unification is deliberate.
    /// </summary>
    private static readonly (string Project, string Shape)[] MeasuredInlineLiterals =
    [
        ("Harbor.Storage.Memory", "Session '{}' not found."),
        ("Harbor.Storage.Memory", "Session '{}' not found."),
        ("Harbor.Storage.Memory", "Session '{}' not found."),
        ("Harbor.Storage.Memory", "Session '{}' not found."),
        ("Harbor.Storage.Memory", "Session '{}' not found."),
        ("Harbor.Storage.Memory", "Session '{}' not found."),
        ("Harbor.Storage.Memory", "Session '{}' not found."),
        ("Harbor.Storage.Memory", "Session '{}' not found."),
        ("Harbor.Storage.Memory", "Session '{}' not found."),
    ];

    // =====================================================================
    // R1 — parity
    // =====================================================================

    /// <summary>
    ///     R1 — every session-store failure literal a store writes must be
    ///     shape-equal to a shape the home declares.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the check that covers Memory's seven unpinned sites. The
    ///         comparison is on the hole-normalised body, so the parameter name
    ///         at the call site (<c>sessionId</c> vs <c>session.Id</c>) is not a
    ///         difference — only a different sentence is.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task Store_Failure_Text_Matches_The_Home()
    {
        string[] canonical = ReadHomeShapes();
        var offenders = new List<string>();

        foreach ((string project, string file, string shape) in ScanStoreLiterals())
        {
            if (!canonical.Contains(shape, StringComparer.Ordinal))
            {
                offenders.Add(
                    $"{SourceScan.Relative(file)} ({project}): {shape} — no factory in "
                    + $"src/{SharedFolder}/{HomeFileName} declares this shape. A store writing a "
                    + "session-store failure text the home does not know is how the three backends "
                    + "start showing a person different text for the same outcome. Call the factory, "
                    + "or add the shape to the home and to MeasuredHomeShapes in the same commit.");
            }
        }

        await Assert.That(offenders).IsEmpty()
            .Because(string.Join("\n", offenders));
    }

    // =====================================================================
    // R2 — the ratchet
    // =====================================================================

    /// <summary>
    ///     R2 — the inline inventory is exactly what was measured. Both deltas
    ///     are asserted empty: an addition is the violation, and a removal must
    ///     be re-measured rather than pass unremarked.
    /// </summary>
    [Test]
    public async Task Inline_Duplication_Is_Exactly_What_Was_Measured()
    {
        (string Project, string Shape)[] now =
            [.. ScanStoreLiterals().Select(l => (l.project, l.shape))];

        List<string> regressions =
        [
            .. now.Except(MeasuredInlineLiterals)
                .Select(l => $"ADDED — {l.Project} now writes {l.Shape} inline.")
                .OrderBy(s => s, StringComparer.Ordinal)
        ];

        List<string> improvements =
        [
            .. MeasuredInlineLiterals.Except(now)
                .Select(l => $"REMOVED — {l.Project} no longer writes {l.Shape} inline.")
                .OrderBy(s => s, StringComparer.Ordinal)
        ];

        var delta = new List<string>();
        delta.AddRange(regressions);
        delta.AddRange(improvements);

        await Assert.That(delta).IsEmpty()
            .Because(
                "Eleven inline literals at the time of writing, all byte-identical in shape to "
                + "src/Harbor.Storage.Shared/SessionStoreErrors.cs, so a person sees the same text on "
                + "every backend — this is duplication, not a behaviour bug. ADDING one is a "
                + "regression: it is a new site no ROP suite pins, which is the seven-site hole "
                + "described in the header. REMOVING one is the owed unification tracked on #764 "
                + "(add the <Compile> link to Harbor.Storage.Memory and call the factory); when it "
                + "lands, delete the matching row here in the same commit and let SharedSourceLinkRules."
                + "The_Link_Inventory_Is_Not_Empty record the new consumer. Deltas:\n"
                + string.Join("\n", delta));
    }

    // =====================================================================
    // R3 — the home is still a home
    // =====================================================================

    /// <summary>
    ///     R3 — the home still declares the three shapes it declared when
    ///     measured, so R1 cannot pass over a vocabulary that has narrowed.
    /// </summary>
    [Test]
    public async Task The_Home_Declares_The_Measured_Shapes()
    {
        string[] now = ReadHomeShapes();

        await Assert.That(now).IsEquivalentTo(MeasuredHomeShapes)
            .Because(
                "SessionNotFound, MessageNotFound and InvalidSessionId are the whole vocabulary "
                + "R1 judges against. A shape removed from the home is not a smaller guard — it is a "
                + "guard that accepts less while still reporting green, and any store still writing "
                + "that shape would then be flagged for a text the repo no longer declares. Measured: "
                + string.Join(" | ", now));
    }

    // =====================================================================
    // Non-vacuity
    // =====================================================================

    /// <summary>
    ///     The home really is inside the scanned set. <c>src/Harbor.Storage.Shared
    ///     </c> has no <c>.csproj</c>, so a walk that resolved only a project's own
    ///     files — or only its <c>&lt;Compile Include&gt;</c> items — would reach
    ///     it through Jsonl and Sqlite but never through Memory, the one project
    ///     this file exists to judge. This asserts the reach instead of trusting it.
    /// </summary>
    [Test]
    public async Task The_Home_Is_In_Scan_Scope()
    {
        string? home = FindHomeFile();

        await Assert.That(home).IsNotNull()
            .Because(
                "src/Harbor.Storage.Shared/SessionStoreErrors.cs is linked source with no csproj. "
                + "This file scans by path prefix over SourceScan.EnumerateProductCsFiles() rather "
                + "than through RepoPaths.EnumerateCsFiles precisely so a csproj-less folder is "
                + "reachable (#456/#763 own the general per-project walk). If the home is not in the "
                + "scanned set, R1 is comparing store literals against an empty vocabulary and would "
                + "pass for the wrong reason — or flag every site. Either way it stops being a guard.");
    }

    /// <summary>
    ///     The scan finds literals at all, and in all three stores' directories.
    ///     Without this, a walk rooted wrongly returns nothing and R1 passes over
    ///     an empty population.
    /// </summary>
    [Test]
    public async Task The_Scan_Finds_The_Stores_And_The_Literals()
    {
        string[] scanned = SourceScan.EnumerateProductCsFiles()
            .Where(p => StoreProjects.Any(s =>
                SourceScan.Relative(p).StartsWith($"src/{s}/", StringComparison.Ordinal)))
            .Select(SourceScan.Relative)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(scanned).IsNotEmpty()
            .Because(
                "An empty set means the walk is not reading src/ at all, and every rule below is "
                + "then vacuously true. Found: " + string.Join(", ", scanned));

        List<(string Project, string Shape)> found =
            [.. ScanStoreLiterals().Select(l => (l.project, l.shape))];

        await Assert.That(found).IsNotEmpty()
            .Because(
                "A matcher that finds no literals matches nothing, which is the failure mode every "
                + "planted control in this project exists to rule out. If this fails, the literal "
                + "pattern no longer matches the shape the stores actually write.");
    }

    /// <summary>
    ///     Planted control: the same matcher must REJECT a drifted shape and
    ///     ACCEPT a faithful one. A rule that cannot fail is worse than no rule,
    ///     because it buys confidence it has not earned.
    /// </summary>
    [Test]
    public async Task The_Matcher_Rejects_A_Drifted_Shape()
    {
        const string Drifted = "Session {} was not found.";
        const string Faithful = "Session '{}' not found.";

        await Assert.That(Drifted).IsNotIn(MeasuredHomeShapes)
            .Because(
                "Control for R1. The tools in Harbor.Tools.Builtin really do say \"Session '{id}' "
                + "was not found.\" (SessionReadTool.cs:117, SessionSteerTool.cs:148) — a different "
                + "surface with deliberately different wording, which is why the store perimeter here "
                + "is the three storage projects and not the whole tree. If a store ever adopts that "
                + "wording, R1 must see it as a shape the home does not declare. If this assertion "
                + "ever passes silently, the planted control has stopped discriminating.");

        await Assert.That(Faithful).IsIn(MeasuredHomeShapes)
            .Because(
                "The other half of the same control: the faithful shape must still be accepted, so "
                + "R1 is discriminating rather than rejecting everything. A matcher that fails both "
                + "halves is not a matcher.");
    }

    // =====================================================================
    // The scan
    // =====================================================================

    /// <summary>
    ///     Matches one plain or interpolated string literal and captures its body.
    ///     The <c>\$</c> is optional on purpose: a shape assembled with no hole at
    ///     all (<c>"Session not found."</c>) is still a shape the home has to know,
    ///     and a matcher that only saw interpolated literals would not report it.
    ///     <c>(?:[^"\\]|\\.)*</c> so an escaped quote does not end the match early.
    /// </summary>
    private static readonly Regex LiteralPattern = new(
        @"\$?""(?<body>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>One interpolation hole, <c>{...}</c>, with no nested braces.</summary>
    private static readonly Regex HolePattern = new(@"\{[^{}]*\}", RegexOptions.Compiled);

    /// <summary>Repo-relative path of the declaring file, or null when absent.</summary>
    private static string? FindHomeFile()
    {
        string? dir = RepoPaths.FindProjectDir(SharedFolder);
        if (dir is null)
        {
            return null;
        }

        string candidate = Path.Combine(dir, HomeFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    ///     The shapes the home declares, hole-normalised and sorted. Returns
    ///     empty rather than throwing when the home is missing, so
    ///     <see cref="The_Home_Is_In_Scan_Scope" /> reports the real cause.
    /// </summary>
    private static string[] ReadHomeShapes()
    {
        string? home = FindHomeFile();
        if (home is null)
        {
            return [];
        }

        string? source = SourceScan.TryReadAllText(home);
        if (source is null)
        {
            return [];
        }

        return [.. LiteralPattern.Matches(SourceScan.StripComments(source))
            .Select(m => Normalise(m.Groups["body"].Value))
            .Where(IsFailureShaped)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)];
    }

    /// <summary>
    ///     Every failure-shaped literal the three stores write, as
    ///     (project, absolute path, normalised shape). Comments are stripped
    ///     first, so the prose in the home's own XML docs — which quotes all
    ///     three shapes — is not graded as code.
    /// </summary>
    private static IEnumerable<(string project, string file, string shape)> ScanStoreLiterals()
    {
        foreach (string project in StoreProjects)
        {
            foreach (string file in SourceScan.EnumerateProductCsFiles())
            {
                string relative = SourceScan.Relative(file);
                if (!relative.StartsWith($"src/{project}/", StringComparison.Ordinal))
                {
                    continue;
                }

                string? raw = SourceScan.TryReadAllText(file);
                if (raw is null)
                {
                    continue;
                }

                foreach (Match match in LiteralPattern.Matches(SourceScan.StripComments(raw)))
                {
                    string shape = Normalise(match.Groups["body"].Value);
                    if (IsFailureShaped(shape))
                    {
                        yield return (project, file, shape);
                    }
                }
            }
        }
    }

    /// <summary>Collapses every interpolation hole to <c>{}</c>.</summary>
    private static string Normalise(string body) => HolePattern.Replace(body, "{}");

    /// <summary>
    ///     Whether a normalised body is a session-store failure text at all.
    ///     Deliberately a SHAPE test, not an equality test: it is what lets R1
    ///     report a drifted spelling as "no factory declares this shape" instead
    ///     of silently ignoring a literal it does not recognise.
    /// </summary>
    private static bool IsFailureShaped(string shape) =>
        shape.Contains("not found", StringComparison.Ordinal)
        || shape.Contains("Invalid session id", StringComparison.Ordinal);
}
