// ThemeStoreSeamRules.cs — the guard for #668, and for the duplicate half of
// #479-A6.
//
// WHY THIS FILE EXISTS
// --------------------
// #668 was filed as a capability-rules item ("two CellForge theme widgets call
// System.IO.File") and its fix direction reads like a permission problem. It is
// not. Reading the issue and the code together says something sharper: the two
// CellForge widgets were a SECOND implementation of theme loading, sitting next
// to the one that already existed in Harbor.DesignSystem.
//
//     JsonThemeLoader.LoadFile  ~  ThemeStore.LoadEntry   (read + parse a file)
//     ThemeFileWatcher.Poll     ~  ThemeDirectoryWatcher  (stat-poll + apply)
//
// The capability rule already noticed the symptom — that is why the two types
// are baselined in PresentationCapabilityRules.KnownViolations — but a baseline
// row is a permission, not a fix. It says "this Presentation type may touch the
// filesystem", which is a statement the architecture never wanted to make, and
// it would still be there after the duplicate was deleted. So the two baseline
// rows are not the deliverable; DELETING them is. That is #668's own "готово
// когда", and it is only reachable through a port: the widgets have to read a
// theme file through something they name, and that something has to own the
// File.* calls.
//
// WHAT IS RULED
// -------------
//   1. The port exists, is public, and is declared in Harbor.DesignSystem — the
//      one assembly all four participants can see. DesignSystem carries an EMPTY
//      allowed-reference set (it is the HDS v1 token leaf), so it cannot name a
//      layer that could host the contract, and the CellForge widgets already
//      reference it.
//   2. EXACTLY ONE production type implements it. This is the invariant the
//      whole issue is about, and it is the one that cannot rot silently: a
//      second implementer is a second implementation, which is precisely what
//      #479-A6 ("a THIRD theme-parse path for the same input") is complaining
//      about. Test assemblies are excluded — a fake proves the port is
//      injectable, it reads nobody's files, and a rule that fails on the right
//      kind of code is a rule that gets deleted.
//   3. The two gated widgets call no filesystem API, and the duplicate read
//      (JsonThemeLoader.LoadFile) is gone. Comments are stripped first, so the
//      XML docs that NAME File.Exists to explain the old state do not read as
//      code (SourceCommentStripper exists for exactly this).
//   4. Rules 3's scanner is non-vacuous: the same scanner must still find real
//      calls in a file that legitimately keeps them. A scan that passes because
//      its regex is broken is worse than no scan, and a "there are no
//      violations" result is otherwise indistinguishable from "the scanner read
//      nothing".
//   5. THE FOURTH READER READS THROUGH THE PORT TOO. `ThemeDirectoryWatcher` —
//      the other half of the #479-A6 pair named in line 13 above — was the site
//      #668 could not reach and #720 could not finish: it is in
//      `Harbor.Hosting.Themes`, not in a Presentation assembly, so it was never
//      in rule 3's gated list, and it went on doing its own read-and-parse. That
//      is the "THIRD theme-parse path for the same input" the issue names, and
//      it was the last one standing. See "THE TWO WATCHERS" below for what this
//      file does and does not unify about that pair.
//
// WHY THE PORT LIVES IN DESIGNSYSTEM AND NOT IN DOMAIN
// ----------------------------------------------------
// #536 proposed the same contract ("IThemeStore … declared in Domain"), so the
// two issues meet here and the placement is a decision, not an accident.
// Harbor.DesignSystem's allowed-reference set is EMPTY (FullLayerMatrixTests:
// new(Layer.Presentation, [])), so a ThemeStore that lived in DesignSystem could
// not implement a Domain-declared interface without a forbidden edge. Putting
// the contract in DesignSystem was the placement #668 could actually reach, and
// it did not block #536 — the port could stay put while the persistence moved.
//
// It worked out that way. #536 has landed: the port is STILL declared in
// Harbor.DesignSystem, and `ThemeStore` — its one implementation — now lives in
// `Harbor.Hosting.Themes`. Two consequences this file has to keep true, and
// which are easy to break by editing the wrong half:
//
//   * The "exactly one implementer" rule is unchanged and still counts
//     `ProductionAssemblies()`. The implementer moved assembly, not layer, so
//     the count is still 1 and `SoleImplementation` is still "ThemeStore".
//   * The port's home is still asserted. `ThemeStore_Port_Is_Public_And_Lives_In_
//     DesignSystem` was the reason the placement was legal when it looked like a
//     problem, and it is the reason it is still legal now that the implementer is
//     on the other side of the leaf. A port that drifted down to the
//     implementation's own assembly would be unassertable by anyone who cannot
//     reference it.
//
// The contract is the token catalog's; where the bytes come from is an outer
// layer's — and the outer layer that can reach Presentation is the composition
// root, not Infrastructure, which the matrix forbids.
//
// WHAT IS DELIBERATELY NOT RULED, AND WHY
// ----------------------------------------
//   * System.IO.Path — pure string manipulation (Combine / GetFileName), no
//     syscall, and the widgets legitimately have a path to name. Same reasoning
//     as PresentationCapabilityRules' "DELIBERATELY NOT RULED" note; do not
//     "helpfully" add it here.
//   * The four Harbor.DesignSystem baseline rows. #536 owned those and has now
//     paid them: `ThemeStore` moved to `Harbor.Hosting.Themes` and the four
//     `PresentationCapabilityRules` rows are deleted rather than re-baselined.
//     What that move did NOT do is move the PORT — `IThemeStore` is still
//     declared here, in the leaf, and that is the point: the two halves are
//     deliberately apart, so "the assembly the port lives in" and "the assembly
//     that touches the disk" are no longer the same answer.
//   * `System.IO.Directory` inside `ThemeDirectoryWatcher`. The port answers
//     "read this theme" and "when did this theme change"; it does not answer
//     "which themes exist", and the enumeration is the watcher's entire reason
//     to exist (#622 seals that mechanism as the theme DISCOVERY path — adding
//     a second one is frozen, re-implementing this one is not). A blanket
//     "no File.* and no Directory.*" rule here would forbid the type's job.
//     `File.*` IS ruled for it, and that is the whole delta: the stamp and the
//     read-and-parse are both port members (`TryGetLastWriteUtc`, `LoadFile`),
//     so a watcher that calls `File.*` is calling something the port already
//     offers it by name.
//   * The RESULT TYPE. DesignSystem has no PackageReference at all — that is
//     why ThemeParseResult is a hand-rolled record and not Result<T>
//     (ResultFailureConversionTests.Exemptions says so). A rule demanding
//     Result<T> in the port would be unsatisfiable without giving the leaf a
//     dependency, which is a bigger decision than this issue.
//
// THE TWO WATCHERS: TWO NEEDS THAT SHARE A SHAPE, NOT ONE NEED SPLIT IN TWO
// -------------------------------------------------------------------------
// #479-A6's fix line offered "PollingWatcher base + two concretes, or one
// IFileSetSource". A previous revision of this file left that open: "duplication
// of the POLL SKELETON stays visible in review, where the trade-off is legible".
// The trade-off is now legible, and the answer is that the two watchers must
// NOT share a poll. They are different requirements that happen to have
// similar silhouettes, and the shared surface is two lines:
//
//     _timer = new Timer(_ => Poll(), null, Interval, Interval);
//     public void Dispose() => _timer.Dispose();
//
// …one of which is not even shared, because each type declares its own
// `static readonly Interval` constant. Everything BELOW that pair is the
// requirement, and it differs:
//
//   * THE FILE SET. `ThemeFileWatcher` is one named path that cannot appear or
//     vanish. `ThemeDirectoryWatcher` enumerates a directory every tick, builds
//     a `seen` set, and GCs the stamps of paths that disappeared — the single-file
//     watcher has no counterpart to any of that, and cannot grow one: "a file I
//     was told to watch is now gone" is not a state it can represent.
//   * THE CHANGE SIGNAL. One scalar `DateTime` versus a `Dictionary<string,
//     DateTime>` with per-path eviction. The stamp map IS the difference; a
//     shared base that owned "did anything change" would have to own the map,
//     and then the single-file case would be a degenerate map of one entry.
//   * BLAST RADIUS OF ONE CHANGE. One load, versus N loads in one poll with
//     per-file error isolation and a filename-prefixed error. And the ordering
//     is contractual: `ReplLifecycle.ArmThemeDirectory`'s own XML doc records
//     that the directory watcher applies every parseable `*.json` in name order
//     and the caller leans on that, so "which theme wins" is answered by the
//     watcher and must not be re-derived by a base class.
//   * START POLICY. The file watcher always starts; the directory watcher takes
//     `autoStart` and builds a disabled `Timer` when it is false, which is how
//     its tests get determinism.
//
// A `PollingWatcher` base would own a `Timer` and expose a `virtual Poll()` that
// BOTH types override in full. What it would actually share is two lines of
// timer plumbing; every requirement above would still live in the overrides, so
// the base decides nothing and the hierarchy would exist to hold a `Timer`. It
// is also unreachable: the base has to live in an assembly both can name, the
// only common leaf is `Harbor.DesignSystem` with an EMPTY allowed-reference set,
// and putting a `Timer` in the HDS v1 token leaf is exactly what #536 moved out
// of it. A new shared assembly would additionally need a home added to
// `ThemeAxisStaysDataRules.SealedThemeSourceHomes`.
//
// So the unification here is NOT the poll — it is the READ. One owner for
// "read and parse a theme document" (`ThemeStore`, via the port), two owners for
// "what changed and what should I apply" (the two watchers, each honest about
// its own file set). That is the shape #717 already used on
// `CollapseWhitespace`/`StripWhitespace`: identical-looking signature, opposite
// requirement, so name them apart and cross-reference rather than unify.
//
// Consequences for the rules below: `ThemeDirectoryWatcher` is gated for its READ
// — the same `File.*` scanner rule 3 applies to the two CellForge widgets, plus
// the port-naming rule — and is deliberately NOT gated for its enumeration. Its
// sibling `ThemeFileWatcher` is likewise free to keep owning its own poll
// skeleton; the two are cross-referenced in each other's remarks so the next
// reader does not re-open this question from the issue text alone.
//
// A NOTE ON WHAT THAT ARGUMENT DID NOT COVER (#479-A6), because it was read as
// covering the whole pair and that is how a visible bug got through. Every point
// above is about the POLL. None of them is about the APPLY, and the apply is the
// half a user sees: `ThemeFileWatcher` applied only when the caller passed no
// `onApplied` callback, `ThemeDirectoryWatcher` always applied. Both product call
// sites pass a callback, so `HARBOR_THEME_FILE` / `~/.harbor/theme.json` polled,
// printed "theme: live-reload → …", marked the screen dirty
// (`ReplLifecycle.cs:271`) and changed no color, while the `~/.harbor/themes/`
// directory arm repainted. Same document, different colors by which branch ran.
//
// Why no rule caught it, and why none of these rules would: they are source
// scanners over the READ. The divergence is behavioural, so it needed a test that
// runs both watchers — `ThemeWatcherApplyParityTests`, which drives them over one
// document and compares the ambient palette. Its second test differs from the
// file arm of the first ONLY by the absent callback, so the callback is the named
// discriminator and a live palette read is proved rather than assumed; without it
// a pair of identically-broken watchers would compare equal and report a green
// that meant nothing. Both apply unconditionally now.
//

using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Rules for the theme-loading seam introduced by #668: one port, one
///     implementation, and Presentation widgets that reach the disk only
///     through it.
/// </summary>
public sealed class ThemeStoreSeamRules
{
    private const string PortName = "IThemeStore";

    /// <summary>
    ///     The port's read member, by name. Matched against comment-stripped source
    ///     rather than through reflection because a guard for a port that does not exist
    ///     yet has to compile before the port does, or it cannot be landed red-first.
    /// </summary>
    private const string ReadMember = "LoadFile";

    /// <summary>The assembly the port and its single implementation belong to.</summary>
    private const string HomeAssembly = "Harbor.DesignSystem";

    /// <summary>The single implementing type, by simple name.</summary>
    private const string SoleImplementation = "ThemeStore";

    /// <summary>
    ///     The Presentation theme widgets that must read a theme file through the
    ///     port. Both were baselined for <c>System.IO.File</c> in
    ///     PresentationCapabilityRules; both rows are gone with this rule green.
    /// </summary>
    private static readonly string[] GatedWidgets =
    [
        "src/Harbor.Tui.CellForge/Chat/Widgets/JsonThemeLoader.cs",
        "src/Harbor.Tui.CellForge/Chat/Widgets/ThemeFileWatcher.cs",
    ];

    /// <summary>
    ///     The other half of the #479-A6 watcher pair, gated for its READ and not
    ///     for its enumeration. <c>Directory.*</c> stays legal here on purpose: the
    ///     port answers "read this theme" and "when did it change", never "which
    ///     themes exist", and that enumeration is the file-set discovery #622 seals
    ///     rather than duplicates. <c>File.*</c> is the ruled half, and both members
    ///     it would want — <c>TryGetLastWriteUtc</c> and <c>LoadFile</c> — are on
    ///     the port, so the rule is enforceable rather than aspirational.
    /// </summary>
    private static readonly string[] GatedDirectoryWatcher =
    [
        "src/Harbor.Hosting/Themes/ThemeDirectoryWatcher.cs",
    ];

    /// <summary>
    ///     The one production type allowed to read and parse a theme document: the
    ///     port's single implementation. It is also the non-vacuity anchor for the
    ///     file-touching scanner, because it is the file that legitimately still
    ///     calls <c>File.*</c> and <c>ThemeJson.Parse</c> itself.
    /// </summary>
    private const string ReadOwner = "src/Harbor.Hosting/Themes/ThemeStore.cs";

    /// <summary>
    ///     The sibling watcher, which already does this correctly: it holds an
    ///     <c>IThemeStore</c> field and calls <c>_store.LoadFile(</c> through it. This
    ///     is the non-vacuity anchor for the port-naming probe — "the file names the
    ///     port" must be a claim the scanner can contradict, and a sibling that
    ///     passes is a far better control than a synthetic string.
    /// </summary>
    private const string PortNamingControl = "src/Harbor.Tui.CellForge/Chat/Widgets/ThemeFileWatcher.cs";


    /// <summary>
    ///     The widget that also owned a public static disk read of its own, which
    ///     is the second implementation in its purest form.
    /// </summary>
    private const string DuplicateReader = "Harbor.Tui.CellForge.Widgets.JsonThemeLoader";

    /// <summary>
    ///     A filesystem call: <c>File.X</c> / <c>Directory.X</c> static members, or
    ///     a constructed <c>FileInfo</c> / <c>DirectoryInfo</c>. Deliberately
    ///     narrow — see the "System.IO.Path" note in the file header.
    /// </summary>
    private static readonly Regex FilesystemCall = new(
        @"(?:\b(?:File|Directory)\s*\.\s*[A-Za-z_])|\bnew\s+(?:FileInfo|DirectoryInfo)\s*\(",
        RegexOptions.CultureInvariant);

    /// <summary>
    ///     The <c>File.*</c> half of <see cref="FilesystemCall" />, for the directory
    ///     watcher, which may enumerate its theme set but may not stat or read it. The
    ///     split is the point: the port answers "read this theme" and "when did it
    ///     change" and deliberately does not answer "which themes exist", so a
    ///     watcher that calls <c>Directory.EnumerateFiles</c> is doing its job and one
    ///     that calls <c>File.GetLastWriteTimeUtc</c> is duplicating a port member.
    /// </summary>
    private static readonly Regex FileCall = new(
        @"(?:\bFile\s*\.\s*[A-Za-z_])|\bnew\s+FileInfo\s*\(",
        RegexOptions.CultureInvariant);


    [Test]
    public async Task ThemeStore_Port_Is_Public_And_Lives_In_DesignSystem()
    {
        var found = FindPortDeclarations();

        await Assert.That(found.Count).IsEqualTo(1).Because(
            "The theme port must be declared exactly once. Two IThemeStore interfaces "
            + "in two assemblies is the duplication this issue removes, just with a "
            + "port-shaped name. Found: " + Describe(found));

        Type port = found[0];
        await Assert.That(port.IsPublic).IsTrue()
            .Because($"{port.FullName} is the seam every theme reader depends on. An "
                     + "internal port cannot be implemented outside its assembly, so it "
                     + "cannot do the job it was introduced for.");
        await Assert.That(port.Assembly.GetName().Name).IsEqualTo(HomeAssembly).Because(
            "Harbor.DesignSystem is the one assembly all four participants can name: it "
            + "carries an EMPTY allowed-reference set (it is the HDS v1 token leaf, so it "
            + "cannot reference a layer that could host the contract), and the CellForge "
            + "widgets already reference it. Found it in " + port.Assembly.GetName().Name + ".");
    }

    [Test]
    public async Task ThemeStore_Port_Has_Exactly_One_Implementation()
    {
        IReadOnlyList<Type> declared = FindPortDeclarations();
        await Assert.That(declared.Count).IsEqualTo(1).Because(
            "This rule is about implementation count, and it can only count once there "
            + "is a port to implement. Asserted here too, so a missing port fails BOTH "
            + "port rules rather than passing this one on an empty probe. Found: "
            + Describe(declared));

        Type port = declared[0];
        var implementers = FindImplementers(port);
        string described = Describe(implementers);

        await Assert.That(implementers.Count).IsEqualTo(1).Because(
            "One interface with two implementations is the duplication #668 exists to "
            + "remove, and a second implementer is invisible in review: each one looks "
            + "correct on its own. #479-A6 names the same shape — a THIRD theme-parse path "
            + "for the same input. Found: " + described);

        await Assert.That(implementers[0].Name).IsEqualTo(SoleImplementation).Because(
            "The port was introduced over Harbor.DesignSystem's existing store, and that "
            + "store is still the one implementer — #536 moved it to Harbor.Hosting.Themes "
            + "rather than replacing it, so the name is what identifies the implementation, "
            + "not the assembly it sits in. A SECOND implementer is a new file-reading "
            + "implementation, which is the thing being removed, and it would look perfectly "
            + "correct on its own. Found: " + described);
    }

    [Test]
    public async Task CellForge_Theme_Widgets_Read_Through_The_Port_Not_The_Filesystem()
    {
        var hits = FindFilesystemCalls(GatedWidgets);

        // `.Count`, not IsEmpty(): TUnit routes IsEmpty/IsNotEmpty by the STATIC
        // collection type, and IReadOnlyList is not one of the shapes it names.
        await Assert.That(hits.Count).IsEqualTo(0).Because(
            "A theme widget that calls File.* cannot be given the shared store, so the "
            + "duplicate survives and the #668 baseline rows can never be deleted — the "
            + "rule would be permanent instead of fixed. Read through IThemeStore: "
            + (hits.Count == 0 ? "(none)" : string.Join("\n", hits)));
    }

    [Test]
    public async Task JsonThemeLoader_No_Longer_Reads_The_Disk_Itself()
    {
        Type? loader = FindType(DuplicateReader);

        await Assert.That(loader).IsNotNull().Because(
            $"{DuplicateReader} is the widget #668 names. If it is gone, the duplicate is "
            + "gone with it and this rule has nothing left to say — which is a rename, not "
            + "a removal, and the port is what a renamed reader would still have to use.");

        Type found = loader!;
        MethodInfo? duplicate = found.GetMethod(
            "LoadFile",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null);

        await Assert.That(duplicate).IsNull().Because(
            "A public static LoadFile(string) here IS the second implementation: the same "
            + "read-and-parse ThemeStore already performs, in a Presentation assembly, "
            + "reachable without the port. Deleting the disk half is what makes the "
            + "baseline rows deletable — leaving the method and routing only the watcher "
            + "through the port would keep the duplicate alive for every other caller.");

        // Non-vacuity for the reflection probe above, in the same shape as the
        // filesystem scanner's: the type must still be findable and must still
        // expose the pure parse, or "LoadFile is null" is trivially true.
        MethodInfo? parse = found.GetMethod(
            "Parse",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string)],
            modifiers: null);

        await Assert.That(parse).IsNotNull().Because(
            "JsonThemeLoader is the pure parser and stays; only its disk half moves. If "
            + "Parse(string) is gone too, the probe above passes because the type was "
            + "emptied, not because the duplicate was removed.");
    }

    /// <summary>
    ///     #479-A6's surviving half. The directory watcher stat-ed and read each theme
    ///     file itself, in a third file, beside the port that already offered both
    ///     operations by name — which is the "THIRD theme-parse path for the same
    ///     input" the issue is literally about. Deliberately <c>File.*</c> and not
    ///     <c>Directory.*</c>: see <see cref="GatedDirectoryWatcher" />.
    /// </summary>
    [Test]
    public async Task ThemeDirectoryWatcher_Does_Not_Touch_The_File_Api()
    {
        var hits = FindCalls(GatedDirectoryWatcher, FileCall);

        await Assert.That(hits.Count).IsEqualTo(0).Because(
            "The port already offers this watcher both halves of what it would call here: "
            + "IThemeStore.TryGetLastWriteUtc for the stamp and IThemeStore.LoadFile for the "
            + "read-and-parse. A File.* call in a type that holds the port is a private copy of "
            + "a member it was handed, and a private copy is the defect #668/#720 were closing, "
            + "not a leftover of it. Enumerating the directory stays allowed — the file set is "
            + "this type's job. Found: "
            + (hits.Count == 0 ? "(none)" : string.Join("\n", hits)));
    }

    /// <summary>
    ///     The other half of the same delta, and the one that keeps the previous rule from
    ///     being satisfiable by deletion: forbidding <c>File.*</c> would also be satisfied by
    ///     the watcher simply no longer reading anything. It has to read, and it has to read
    ///     through a port it names.
    /// </summary>
    [Test]
    public async Task ThemeDirectoryWatcher_Reads_The_Theme_Through_The_Port()
    {
        var missing = FindUnmetPortRead(GatedDirectoryWatcher);

        await Assert.That(missing.Count).IsEqualTo(0).Because(
            "Live-reload has to keep working, so the read cannot simply be removed along with "
            + "the File.* call. It moves to the port the sibling watcher already uses "
            + "(ThemeFileWatcher holds an IThemeStore and calls _store.LoadFile). Unmet: "
            + (missing.Count == 0 ? "(none)" : string.Join("\n", missing)));
    }

    /// <summary>
    ///     Non-vacuity for both probes added by #479-A6, in the same shape as
    ///     <see cref="Filesystem_Scanner_Still_Sees_A_Real_Call" /> and for the same reason:
    ///     each of them can go green for the wrong reason, and a guard whose failure modes
    ///     are indistinguishable from "the guard is broken" teaches the next reader to
    ///     delete it.
    /// </summary>
    [Test]
    public async Task Directory_Watcher_Probes_Are_Non_Vacuous()
    {
        IReadOnlyList<string> fileHits = FindCalls([ReadOwner], FileCall);
        await Assert.That(fileHits.Count).IsGreaterThan(0).Because(
            "The File.* scanner in ThemeDirectoryWatcher_Does_Not_Touch_The_File_Api must be "
            + "able to fail. " + ReadOwner + " is the port's one implementer and legitimately "
            + "still reads the disk, so a scan that finds nothing there is a broken regex — or "
            + "an unreadable repo root — and the watcher's pass is then meaningless. It also "
            + "keeps the check honest about a RENAMED watcher: a missing path is reported as a "
            + "hit, never as a clean file.");

        IReadOnlyList<string> unmet = FindUnmetPortRead([PortNamingControl]);
        await Assert.That(unmet.Count).IsEqualTo(0).Because(
            "The port-naming probe in ThemeDirectoryWatcher_Reads_The_Theme_Through_The_Port "
            + "must be able to fail. " + PortNamingControl + " is the sibling that already does "
            + "this correctly, so if it does not satisfy the probe the probe is wrong and the "
            + "watcher's pass means nothing. Unmet: "
            + (unmet.Count == 0 ? "(none)" : string.Join("\n", unmet)));
    }

    /// <summary>
    ///     Non-vacuity for the scanner in
    ///     <see cref="CellForge_Theme_Widgets_Read_Through_The_Port_Not_The_Filesystem" />,
    ///     the only check in this file that can go green for the wrong reason.
    /// </summary>
    [Test]
    public async Task Filesystem_Scanner_Still_Sees_A_Real_Call()
    {
        // #536 moved the theme store out of Harbor.DesignSystem, so this control's
        // anchor moved with it. Re-anchoring is the whole job: deleting the control
        // would leave `CellForge_Theme_Widgets_Read_Through_The_Port_Not_The_`
        // Filesystem` free to pass on a broken regex, and leaving it where it was
        // would have kept it green for the wrong reason — a MISSING file is
        // reported as a hit by FindFilesystemCalls, so a stale path reads as
        // "the scanner still works" while proving nothing about any file.
        // DesignSystemLeafTakesNoIoRules is the rule that covers the leaf's own
        // disk access; this one proves the widget scanner can still fail.
        string[] host =
        [
            "src/Harbor.Hosting/Themes/ThemeStore.cs",
        ];

        IReadOnlyList<string> hits = FindFilesystemCalls(host);

        await Assert.That(hits.Count).IsGreaterThan(0).Because(
            "The scanner in CellForge_Theme_Widgets_Read_Through_The_Port_Not_The_"
            + "Filesystem must be able to fail. If it reports nothing even here, it is "
            + "reporting nothing everywhere and the widgets' pass is meaningless.");
    }

    // ---------------------------------------------------------------------
    // Probes.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Every public interface named <see cref="PortName" /> across the loaded
    ///     PRODUCTION Harbor assemblies. Resolved by NAME, not by a compile-time
    ///     type reference: a guard for a port that does not exist yet has to
    ///     compile before the port does, or it cannot be landed red-first.
    /// </summary>
    private static IReadOnlyList<Type> FindPortDeclarations()
    {
        var found = new List<Type>();
        foreach (Type type in LoadableTypes(ProductionAssemblies()))
        {
            if (type.IsInterface && type.IsPublic && string.Equals(type.Name, PortName, StringComparison.Ordinal))
            {
                found.Add(type);
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.AssemblyQualifiedName, b.AssemblyQualifiedName));
        return found;
    }

    /// <summary>Concrete production types implementing <paramref name="port" />, sorted for stable messages.</summary>
    private static IReadOnlyList<Type> FindImplementers(Type port)
    {
        var found = new List<Type>();
        foreach (Type type in LoadableTypes(ProductionAssemblies()))
        {
            if (type == port || type.IsInterface || type.IsAbstract || !type.IsClass)
            {
                continue;
            }

            if (port.IsAssignableFrom(type))
            {
                found.Add(type);
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.AssemblyQualifiedName, b.AssemblyQualifiedName));
        return found;
    }

    /// <summary>
    ///     The loaded assemblies the port rules count over: production code only.
    /// </summary>
    /// <remarks>
    ///     A fake <c>IThemeStore</c> in a test assembly is deliberate and good —
    ///     it is how a test proves the port is injectable without touching anybody's
    ///     files — and it is also a second implementer by every measure this file
    ///     has. <c>LoadHarborAssemblies</c> sweeps the test bin directory, so the
    ///     test assemblies are in that dictionary under names starting with
    ///     "Harbor", and <c>Harbor.Architecture.Tests</c> is one of them. Without
    ///     this filter the implementer count is 1 + (number of fakes), and the
    ///     rule can only ever be satisfied by faking nothing.
    /// </remarks>
    private static IEnumerable<Assembly> ProductionAssemblies()
    {
        Assembly self = typeof(ThemeStoreSeamRules).Assembly;
        return ArchitectureTestHelpers.LoadHarborAssemblies()
            .Values
            .Where(a => !ReferenceEquals(a, self) && !IsTestAssembly(a));
    }

    private static bool IsTestAssembly(Assembly assembly)
    {
        string name = assembly.GetName().Name ?? string.Empty;
        return name.EndsWith("Tests", StringComparison.Ordinal) // Harbor.X.Tests
               || name.EndsWith(".TestKit", StringComparison.Ordinal)
               || string.Equals(name, "Harbor.TestKit", StringComparison.Ordinal)
               || name.EndsWith(".Benchmarks", StringComparison.Ordinal)
               || name.StartsWith("testhost", StringComparison.Ordinal)
               || name.StartsWith("Microsoft.Testing", StringComparison.Ordinal);
    }

    /// <summary>
    ///     A production type by full name, or <c>null</c>. Name-based
    ///     for the same reason the port probe is: the guard must compile against
    ///     dev, where the refactor it guards has not happened.
    /// </summary>
    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in ProductionAssemblies())
        {
            foreach (Type type in LoadableTypes(new[] { assembly }))
            {
                if (string.Equals(type.FullName, fullName, StringComparison.Ordinal))
                {
                    return type;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Every filesystem call in the named repo-relative files, one entry per
    ///     site, as <c>repo-relative-path:line  matched-text</c>. Comments are
    ///     stripped first so a doc line naming the old <c>File.Exists</c> is not
    ///     graded as code. Returns empty when the repo root is unavailable, which
    ///     <see cref="Filesystem_Scanner_Still_Sees_A_Real_Call" /> is the
    ///     backstop for.
    /// </summary>
    private static IReadOnlyList<string> FindFilesystemCalls(IReadOnlyList<string> relativePaths)
        => FindCalls(relativePaths, FilesystemCall);

    /// <summary>
    ///     Every call to <paramref name="pattern" /> in the named repo-relative files, one
    ///     entry per site, as <c>repo-relative-path:line  matched-text</c>. Comments are
    ///     stripped first so a doc line naming the old <c>File.Exists</c> is not graded as
    ///     code. Returns empty when the repo root is unavailable, which the non-vacuity
    ///     tests are the backstop for.
    /// </summary>
    private static IReadOnlyList<string> FindCalls(IReadOnlyList<string> relativePaths, Regex pattern)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var hits = new List<string>();
        foreach (string relative in relativePaths)
        {
            if (!TryReadStripped(relative, out string[] lines))
            {
                hits.Add(MissingFile(relative));
                continue;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                Match match = pattern.Match(lines[i]);
                if (match.Success)
                {
                    hits.Add($"{relative}:{i + 1}  {match.Value.Trim()}");
                }
            }
        }

        return hits;
    }

    /// <summary>
    ///     The ways a file fails to read a theme document THROUGH the port: it never names
    ///     the port, or it names it without calling the read member. Both are reported, and
    ///     a missing file is a failure rather than a pass — a renamed watcher has to redden
    ///     this rule, not satisfy it.
    /// </summary>
    private static IReadOnlyList<string> FindUnmetPortRead(IReadOnlyList<string> relativePaths)
    {
        var unmet = new List<string>();
        foreach (string relative in relativePaths)
        {
            if (RepoPaths.RepoRoot is null)
            {
                unmet.Add("(repo root unavailable — the non-vacuity test is the backstop)");
                continue;
            }

            if (!TryReadStripped(relative, out string[] lines))
            {
                unmet.Add(MissingFile(relative));
                continue;
            }

            string text = string.Join('\n', lines);
            if (!text.Contains(PortName, StringComparison.Ordinal))
            {
                unmet.Add($"{relative}  never names {PortName}");
            }

            if (!text.Contains(ReadMember, StringComparison.Ordinal))
            {
                unmet.Add($"{relative}  calls no {PortName}.{ReadMember}(");
            }
        }

        return unmet;
    }

    private static string MissingFile(string relative)
        => $"{relative}  (file is missing — the guard cannot grade a file it cannot read)";

    private static bool TryReadStripped(string relative, out string[] lines)
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            lines = [];
            return false;
        }

        string path = Path.Combine(root, relative);
        if (!File.Exists(path))
        {
            lines = [];
            return false;
        }

        lines = SourceCommentStripper.StripAll(File.ReadLines(path));
        return true;
    }

    /// <summary>
    ///     An assembly's types, skipping the ones a partially-referenced assembly
    ///     cannot resolve. A <see cref="ReflectionTypeLoadException" /> here means
    ///     "this assembly is not fully loadable in this host", and dropping the
    ///     unresolvable types is what the rest of this suite does.
    /// </summary>
    private static IEnumerable<Type> LoadableTypes(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.Where(static t => t is not null).Select(static t => t!)];
            }
            catch (Exception)
            {
                continue;
            }

            foreach (Type type in types)
            {
                yield return type;
            }
        }
    }

    private static string Describe(IReadOnlyList<Type> types)
        => types.Count == 0 ? "(none)" : string.Join(", ", types.Select(static t => t.FullName));
}
