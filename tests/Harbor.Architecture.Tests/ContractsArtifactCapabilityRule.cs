// ContractsArtifactCapabilityRule.cs — GUARD for epic #41 slice B3.1 (#405).
//
// WHAT THIS FILE RULES, AND WHY IT IS NOT THE OBVIOUS THING
// ---------------------------------------------------------
// #405's acceptance criteria say an artifact whose path does not exist is
// "rejected at record time". The record is
// `Harbor.Abstractions.Contracts.Models.RunVerificationRecord`, and that project
// is Layer.Domain (FullLayerMatrixTests.Matrix, line 263) with a csproj that
// describes itself as "pure data + rules" and that had, before #405, exactly
// ZERO `File.*` / `Directory.*` calls in it. So satisfying the AC the obvious
// way puts a filesystem call in the leaf that every other layer is built on top
// of.
//
// The alternative — push the existence probe up into `Harbor.Application`, next
// to `WorkspaceInspector`, which already forks git from there — was rejected for
// one reason and it is the reason this file exists: with the probe outside, the
// probe is a CALLER. `RunArtifact` would then be a plain pair of strings with a
// public constructor, `WithArtifact` would accept one, and the slice's artifact
// rule would be a suggestion the record happened to decline to use. #921's
// standard is the reason that is not acceptable: a permission that is recorded
// but has no enforcement is a hole, not a permission.
//
// So the probe is IN the contract, deliberately, and this file is what makes
// that a decision rather than an accident:
//
//   1. ALLOWED, exactly one site: `File.Exists` inside `RunArtifact.Create`.
//      Named here, with the reason, and counted — so a SECOND read in the same
//      project is red, not quietly accepted. That is the difference between a
//      recorded capability and an opening.
//   2. ARMED with no baseline row at all: the WRITE family — File.Write*,
//      File.Create, File.Delete, File.Move, File.Copy, File.Open*, Directory.
//      Create*/Delete/Move/EnumerateFileSystemEntries, new FileInfo /
//      DirectoryInfo — must be zero. One is red on the spot. This is the half
//      that matters, because "the leaf may look at the filesystem" and "the leaf
//      stores things" are different claims and only the first is being made.
//   3. `System.IO.Path` is NOT ruled and must not be added here: pure string
//      manipulation, no syscall, and a rule that caught it would fire on the
//      path handling the record legitimately does.
//
// WHY THE WRITE BAN HAS NO BASELINE ROW
// ------------------------------------
// Every exemption table in this repository routes through
// `ExemptionReason.RowsWithoutAReason` (docs/ARCHITECTURE_LAYERS.md §5.9), and
// the shared instinct is to add a row for the thing you just allowed. This file
// deliberately does not: the one allowed read is NOT an exemption from a rule,
// it is the rule. `RunArtifact.Create`'s `File.Exists` is the only hit the
// read-rule permits, and a row naming it would be a second place to keep in sync
// with a type that is already self-describing.
//
// NON-VACUITY, IN THREE PLACES
// ----------------------------
//   1. Scan_IsLive — the scan really walked `src/Harbor.Abstractions.Contracts`.
//      Without it a renamed project yields zero hits and all three rules pass
//      because they read nothing.
//   2. The_Allowed_Probe_Is_Exactly_One_Site — asserted against the live tree,
//      so it is RED until #405's file exists and GREEN after. It is also the
//      liveness check for the permission: if `RunArtifact.Create` is refactored
//      to take the existence as a parameter, the permission stops describing
//      anything real and this goes red rather than lingering as a blank excuse.
//   3. PositiveControl_The_Matcher_Fires_On_A_Planted_Offender_Only — the
//      matcher is handed a synthetic read, a synthetic write and a synthetic
//      doc comment that NAMES `File.Exists`, and must report the first two and
//      not the third. A probe whose matchers stopped matching reports nothing,
//      and this rule goes green while enforcing nothing — the state #921 found
//      the type rule in.

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Epic #41 slice B3.1: <c>Harbor.Abstractions.Contracts</c> is a Domain leaf,
///     and the one filesystem capability #405 puts in it is a read inside
///     <c>RunArtifact.Create</c> — with every write armed.
/// </summary>
public sealed class ContractsArtifactCapabilityRule
{
    /// <summary>The audited project directory, as <c>src/&lt;dir&gt;</c>.</summary>
    private const string AuditedProjectDir = "Harbor.Abstractions.Contracts";

    /// <summary>
    ///     The file that holds the single permitted read. Named so the permission
    ///     has a subject: a permission without one is a blanket excuse, which is
    ///     the shape <c>ExtensionAxisFreezeRule.EveryDeclaredAxis_IsStillReal</c>
    ///     exists to stop elsewhere in this project.
    /// </summary>
    private const string PermittedReadFile = "src/Harbor.Abstractions.Contracts/Models/RunVerification.cs";

    /// <summary>
    ///     Any <c>File.X</c> / <c>Directory.X</c> static member, or a constructed
    ///     <c>FileInfo</c> / <c>DirectoryInfo</c>. This is the READ rule: one site
    ///     is permitted and it is named; anything else in the project is a finding.
    /// </summary>
    /// <remarks>
    ///     The member name is captured WHOLE, unlike the older
    ///     <c>DesktopSharedTakesNoIoRules.DiskCall</c>, which stops after one
    ///     character and therefore reports <c>File.E</c>. This rule asserts on the
    ///     matched text, and a finding a reader cannot act on is a finding that
    ///     gets ignored — which is how <c>File.WriteAllText</c> and
    ///     <c>File.Exists</c> would end up indistinguishable in the message.
    /// </remarks>
    private static readonly Regex DiskCall = new(
        @"(?:\b(?:File|Directory)\s*\.\s*[A-Za-z_]\w*)|\bnew\s+(?:FileInfo|DirectoryInfo)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     The subset of <see cref="DiskCall" /> that MUTATES: writes, creates,
    ///     deletes, moves, copies, opens for writing, or enumerates the tree.
    ///     Zero tolerance and no baseline row — see the file header.
    /// </summary>
    /// <remarks>
    ///     <c>new FileInfo(…)</c> and <c>new DirectoryInfo(…)</c> are in here as
    ///     well as in <see cref="DiskCall" />. The CI run that proved this rule
    ///     red (#1019, commit <c>91e426a4</c>) caught the omission: the positive
    ///     control planted six mutators and the matcher reported five, because
    ///     <c>new FileInfo("p").Delete()</c> matched the READ pattern and not
    ///     this one. The armed half must not depend on the read half being
    ///     total in order to catch a deletion — the day the permitted site moves,
    ///     the read pattern's coverage is exactly what stops being obvious.
    /// </remarks>
    private static readonly Regex DiskWrite = new(
        @"(?:\b(?:File|Directory)\s*\.\s*"
        + @"(?:Write\w*|Create\w*|Delete\w*|Move\w*|Copy\w*|Append\w*|Open\w*|Replace\w*|Enumerate\w*"
        + @"|SetLastWriteTime\w*|Encrypt|Decrypt)\b)"
        + @"|\bnew\s+(?:FileInfo|DirectoryInfo)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Test]
    public async Task Scan_IsLive()
    {
        IReadOnlyList<string> files = RepoPaths.EnumerateCsFiles(AuditedProjectDir);

        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the scan needs a checkout; without one it reports zero disk calls in a Domain leaf "
                   + "and every rule in this file is satisfied by having nothing to look at");

        await Assert.That(files.Count).IsGreaterThan(0).Because(
            "Outside a checkout, or if the project directory were renamed, EnumerateCsFiles returns "
            + "nothing and the rules below pass because they read nothing. A rule that cannot see its "
            + "subject is not a rule about its subject. Found " + files.Count + " .cs files under src/"
            + AuditedProjectDir + ".");
    }

    [Test]
    public async Task The_Domain_Leaf_Stores_Nothing()
    {
        IReadOnlyList<string> hits = Find(DiskWrite, RepoPaths.EnumerateCsFiles(AuditedProjectDir));

        await Assert.That(hits).IsEmpty().Because(
            "Harbor.Abstractions.Contracts is Layer.Domain (FullLayerMatrixTests) and every other layer "
            + "compiles against it, so a persister here is a storage engine nobody declared, in a "
            + "project whose csproj calls itself \"pure data + rules\" and which had zero File.* calls "
            + "before #405. This is the same defect #534 fixed in Harbor.Desktop.Abstractions — a "
            + "published leaf doing persistence behind a green build — one layer further in, and it is "
            + "ARMED: no baseline row, so one call is red on the spot and there is no row to widen. "
            + "#405 needed a read (does this artifact path exist?), and a read cannot be widened into a "
            + "write by accident. Found: " + (hits.Count == 0 ? "(none)" : string.Join("\n", hits)));
    }

    [Test]
    public async Task The_Allowed_Probe_Is_Exactly_One_Site()
    {
        IReadOnlyList<string> hits = Find(DiskCall, RepoPaths.EnumerateCsFiles(AuditedProjectDir));

        // THE LIVENESS HALF OF THE PERMISSION. Stated positively, not as a
        // baseline row, so it is red in BOTH directions: red before
        // RunVerification.cs exists, and red again the moment a second disk
        // call appears. A permission nobody checks is the hole #921 removed five
        // of.
        await Assert.That(string.Join(" | ", hits))
            .IsEqualTo($"{PermittedReadFile}:{ExpectedPermittedReadLine()}  File.Exists")
            .Because(
                "The single filesystem capability #405 puts in this Domain leaf is the existence probe in "
                + "RunArtifact.Create, and it is allowed HERE rather than in an exemption table so that "
                + "the permission has exactly one subject and is graded against the live tree. A second "
                + "call is not automatically fine: it means the leaf's I/O is growing, and the decision "
                + "to allow it belongs in a review of this file, not in a diff nobody was looking at. If "
                + "RunArtifact.Create is refactored to accept existence as a parameter, the reason this "
                + "probe exists evaporates — move the check up to Harbor.Application beside "
                + "WorkspaceInspector and delete the row here in the same commit. Found: "
                + (hits.Count == 0 ? "(none)" : string.Join(" | ", hits)));

        await Assert.That(hits.Count()).IsEqualTo(1).Because(
            "one probe, one site. A Domain leaf that reaches the filesystem in two places is two "
            + "decisions pretending to be one, and only one of them was taken.");
    }

    [Test]
    public async Task PositiveControl_The_Matcher_Fires_On_A_Planted_Offender_Only()
    {
        // A READ, which the read rule must catch…
        IReadOnlyList<string> read = ScanSnippet(
            DiskCall,
            """
            public static bool Probe(string path) => File.Exists(path);
            """);

        await Assert.That(read.Count()).IsEqualTo(1).Because(
            "The read matcher must be able to fail, or \"one allowed site\" and \"no sites\" are the same "
            + "observation. If this ever goes red the fix is the REGEX, not RunArtifact.");

        // …a WRITE, which the write rule must catch, including the families a
        // narrower regex would miss.
        IReadOnlyList<string> writes = ScanSnippet(
            DiskWrite,
            """
            public sealed class Sneaky
            {
                public void A() => File.WriteAllText("p", "x");
                public void B() => File.Delete("p");
                public void C() => Directory.CreateDirectory("d");
                public void D() => new FileInfo("p").Delete();
                public void E() => File.OpenWrite("p");
                public void F() => Directory.EnumerateFileSystemEntries("d");
            }
            """);

        await Assert.That(writes.Count()).IsEqualTo(6).Because(
            "The write matcher is the armed half and has no baseline row, so it has to be right about the "
            + "whole family rather than about the two spellings this issue happened to use. A matcher "
            + "that caught only File.Write* would leave File.Delete and Directory.CreateDirectory open in "
            + "a leaf every other layer sits on.");

        // …and prose that NAMES the calls, which must NOT be graded as code.
        // This is the case that would make the rule unmaintainable: the allowed
        // site is documented at length, in a file whose remarks discuss
        // File.Exists and File.WriteAllText by name.
        IReadOnlyList<string> prose = ScanSnippet(
            DiskCall,
            """
            /// <remarks>
            ///     The existence probe is a deliberate read. A `File.WriteAllText` here
            ///     would be the storage engine nobody declared; see the file header.
            /// </remarks>
            public sealed class Documented { }
            """);

        await Assert.That(prose).IsEmpty().Because(
            "Comment stripping is load-bearing. RunVerification.cs explains at length why the probe is "
            + "there, and its own remarks discuss File.Exists and File.WriteAllText by name — a probe "
            + "that graded those would make the rule impossible to satisfy without deleting the "
            + "explanation, which is how a guard gets switched off. A name in prose is not a call.");
    }

    /// <summary>
    ///     The line number the permitted probe is expected on, computed rather than
    ///     hard-coded: <see cref="The_Allowed_Probe_Is_Exactly_One_Site" /> grades
    ///     the LIVE tree, and pinning a bare line number would redden the file on
    ///     any edit above it, which is how a guard gets switched off.
    /// </summary>
    private static int ExpectedPermittedReadLine()
    {
        string path = Path.Combine(RepoPaths.RepoRoot!, PermittedReadFile);
        if (!File.Exists(path))
        {
            return -1;
        }

        string[] lines = SourceCommentStripper.StripAll(File.ReadLines(path));
        for (int i = 0; i < lines.Length; i++)
        {
            if (DiskCall.IsMatch(lines[i]))
            {
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Every match of <paramref name="pattern" /> in the named ABSOLUTE files,
    ///     one entry per site, as <c>repo-relative-path:line  matched-text</c>.
    ///     Comments are stripped first, so prose naming a call is not graded as
    ///     code.
    /// </summary>
    private static IReadOnlyList<string> Find(Regex pattern, IReadOnlyList<string> files)
    {
        var hits = new List<string>();
        foreach (string file in files)
        {
            if (!File.Exists(file))
            {
                hits.Add($"{SourceScan.Relative(file)}  (file is missing — the probe cannot grade a "
                        + "file it cannot read)");
                continue;
            }

            string[] lines = SourceCommentStripper.StripAll(File.ReadLines(file));
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = pattern.Match(lines[i]);
                if (match.Success)
                {
                    hits.Add($"{SourceScan.Relative(file)}:{i + 1}  {match.Value.Trim()}");
                }
            }
        }

        return hits;
    }

    /// <summary>
    ///     The positive control's probe: writes the snippet to a temp file, drives
    ///     the REAL matcher over it — comment stripping included, so the control
    ///     cannot get a weaker second copy of the logic it is testing — and
    ///     removes the file either way.
    /// </summary>
    private static IReadOnlyList<string> ScanSnippet(Regex pattern, string snippet)
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-405-probe-{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllText(path, snippet);
            return Find(pattern, [path]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
