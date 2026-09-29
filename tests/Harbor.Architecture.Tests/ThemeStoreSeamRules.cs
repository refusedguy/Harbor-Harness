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
//   * Whether the two watchers share a polling base class. That is #479-A6's
//     own proposal ("PollingWatcher base + two concretes, or one
//     IFileSetSource"). Duplication of the *read* is what this file rules;
//     duplication of the *poll skeleton* stays visible in review, where the
//     trade-off is legible, rather than becoming an enforced base class in a
//     debt wave.
//   * The RESULT TYPE. DesignSystem has no PackageReference at all — that is
//     why ThemeParseResult is a hand-rolled record and not Result<T>
//     (ResultFailureConversionTests.Exemptions says so). A rule demanding
//     Result<T> in the port would be unsatisfiable without giving the leaf a
//     dependency, which is a bigger decision than this issue.

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
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        var hits = new List<string>();
        foreach (string relative in relativePaths)
        {
            string path = Path.Combine(root, relative);
            if (!File.Exists(path))
            {
                hits.Add($"{relative}  (file is missing — the guard cannot grade a file it cannot read)");
                continue;
            }

            string[] lines = SourceCommentStripper.StripAll(File.ReadLines(path));
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = FilesystemCall.Match(lines[i]);
                if (match.Success)
                {
                    hits.Add($"{relative}:{i + 1}  {match.Value.Trim()}");
                }
            }
        }

        return hits;
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
