// PresentationCapabilityInstrumentationTests.cs — the probe must read the
// PRODUCT's IL, not the coverage instrumenter's.
//
// WHAT BROKE
// ----------
// The nightly coverage lane instruments every assembly it measures, and the
// instrumentation INJECTS a tracker into each one: a generated
// `Microsoft.CodeCoverage.Instrumentation.Static.Tracker.*` class whose job is
// to record which lines ran, which it does by writing files. The capability
// probe reads the IL of every Presentation assembly, so in that lane it read
// the tracker's own filesystem calls and reported a filesystem capability in
// the Presentation layer — a capability the product does not have.
//
// The consequence was not a red job but a blind one: the coverage registry is
// a ratchet whose strict half (the uncovered set) is only compared when the
// measurement is COMPLETE. A suite that fails makes the measurement partial,
// and a partial measurement skips the strict half. So the tracker broke the
// arch suite, and the arch suite breaking made the coverage gate check less.
//
// WHAT THIS FILE PROVES
// ---------------------
// The skip is narrow: it recognises exactly the instrumenter's markers and
// nothing else. A product type is never skipped, so the rule keeps its teeth
// (the probe's own non-vacuity tests, in PresentationCapabilityRules.cs, still
// require that the probe reads the real IL and still fires).
//
// The proof is BEHAVIOURAL, not a text match: the same fixture is scanned with
// the skip off and with the skip on, and the two verdicts are compared. The
// skip-off verdict is the one CI run 39550199613 printed seventeen times over,
// against Presentation assemblies that had no filesystem capability at all.

using Mono.Cecil;
// The project has a global `using System.Reflection;`, and both namespaces
// carry a `TypeAttributes` and a `FieldAttributes`. The aliases name the ones
// this file means.
using TypeAttributes = Mono.Cecil.TypeAttributes;
using FieldAttributes = Mono.Cecil.FieldAttributes;

namespace Harbor.Architecture.Tests;

public sealed class PresentationCapabilityInstrumentationTests
{
    /// <summary>
    ///     The tracker the coverage engine injects, in the namespace it injects
    ///     it into today. The name is the one CI run 39550199613 printed.
    /// </summary>
    private const string TrackerNamespace = "Microsoft.CodeCoverage.Instrumentation.Static.Tracker";

    /// <summary>
    ///     The name the coverage engine stamps on the tracker it injects:
    ///     <c>StaticManagedTrackerTemplate_&lt;guid&gt;</c>. Measured, not
    ///     guessed — CI run 39550199613 (nightly, job <c>coverage</c>) printed
    ///     the type it injected into every Presentation assembly.
    /// </summary>
    private const string TrackerName = "StaticManagedTrackerTemplate_3be3a47c-e6fc-4113-bd3f-ed61f5edac9f";

    [Test]
    public async Task The_Instrumenters_Tracker_Is_Skipped()
    {
        var tracker = new TypeDefinition(
            TrackerNamespace,
            TrackerName,
            TypeAttributes.Class | TypeAttributes.NestedPublic);

        await Assert.That(IlCapabilityProbe.IsInstrumentationInjected(tracker)).IsTrue();
    }

    [Test]
    public async Task A_Product_Type_Is_Not_Skipped()
    {
        var product = new TypeDefinition(
            "Harbor.Tui.CellForge.Panels",
            "CellForgeFileTreePanel",
            TypeAttributes.Class | TypeAttributes.Public);

        await Assert.That(IlCapabilityProbe.IsInstrumentationInjected(product)).IsFalse();
    }

    [Test]
    public async Task A_Product_Type_That_Mentions_The_Instrumenter_Is_Not_Skipped()
    {
        // The product's own CALLS into the tracker are still read: the tracker is
        // not a System.IO type, so such a call matches no rule prefix. What the
        // skip removes is the instrumenter's body, never the product's.
        var product = new TypeDefinition(
            "Harbor.Tui.CellForge.Panels",
            "CellForgeFileTreePanel",
            TypeAttributes.Class | TypeAttributes.Public);

        await Assert.That(IlCapabilityProbe.IsInstrumentationInjected(product)).IsFalse();
        await Assert.That(product.Namespace).IsEqualTo("Harbor.Tui.CellForge.Panels");
    }

    /// <summary>
    ///     The stamped type name is the SECOND net. The engine's tracker
    ///     namespace is a configurable value — the string
    ///     <c>TrackerNamespace</c> is in
    ///     <c>Microsoft.CodeCoverage.Instrumentation</c> 18.8.1 — so a namespace
    ///     alone is one version bump away from a probe that blames the product
    ///     for the instrumenter's I/O, which is the defect this file is about.
    /// </summary>
    [Test]
    public async Task The_Trackers_Stamped_Name_Is_Skipped_Even_Outside_The_Engine_Namespace()
    {
        var tracker = new TypeDefinition(
            "Some.Namespace.The.Engine.Might.Pick.Instead",
            TrackerName,
            TypeAttributes.Class | TypeAttributes.Public);

        await Assert.That(IlCapabilityProbe.IsInstrumentationInjected(tracker)).IsTrue()
            .Because("the engine stamps the tracker with this name whatever namespace it "
                   + "injects it into, and the name is the marker that survives a change "
                   + "of namespace. Without it, a newer engine's tracker is read as the "
                   + "product's I/O and the rule fires on the measuring apparatus again.");
    }

    /// <summary>
    ///     A binary that carries the instrumenter's marker is an INSTRUMENTED
    ///     copy, and the probe says so. The verdict it reports is still the
    ///     product's — the instrumenter's own types are skipped — but a reader
    ///     of a failure is told which file the verdict was read from.
    /// </summary>
    [Test]
    public async Task An_Instrumented_Copy_Is_Recognised_As_Such()
    {
        var product = new TypeDefinition(
            "Harbor.Tui.CellForge.Panels",
            "CellForgeFileTreePanel",
            TypeAttributes.Class | TypeAttributes.Public);
        var tracker = new TypeDefinition(
            TrackerNamespace,
            TrackerName,
            TypeAttributes.Class | TypeAttributes.Public);

        await Assert.That(IlCapabilityProbe.IsInstrumentedCopy([product])).IsFalse()
            .Because("a build nobody instruments is the product's own build, and saying "
                   + "otherwise would put a lie in every failure message.");
        await Assert.That(IlCapabilityProbe.IsInstrumentedCopy([product, tracker])).IsTrue()
            .Because("the tracker in the module is the marker of an instrumented copy, and "
                   + "the probe has to be able to say that the file it read is not the "
                   + "product's own build.");
    }

    /// <summary>
    ///     THE BEHAVIOURAL PROOF, on one fixture: the scan WITHOUT the skip
    ///     reports the instrumenter's file I/O, the scan WITH the skip does not,
    ///     and the product's own I/O is reported by both.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The old verdict is not a story. It is what CI run 39550199613
    ///         printed, seventeen times over:
    ///         <c>[PRESENTATION-MUST-NOT-TOUCH-THE-FILESYSTEM-FILES] Harbor.Terminal.Abstractions:
    ///         Microsoft.CodeCoverage.Instrumentation.Static.Tracker.StaticManagedTrackerTemplate_...
    ///         uses reading or writing files from a Presentation assembly</c> — while the
    ///         Presentation layer had no filesystem capability at all. The instrumenter
    ///         writes the coverage data; the product did not.
    ///     </para>
    ///     <para>
    ///         The two fixture types carry DIFFERENT capabilities — the tracker
    ///         writes files (that is what the real tracker does) and the product
    ///         type enumerates directories (that is what the real
    ///         <c>CellForgeFileTreePanel</c> used to do) — so the two hits are
    ///         told apart by the RULE that fired, not by the name of the type
    ///         they belong to.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task The_Skip_Hides_The_Instrumenters_Io_And_Only_The_Instrumenters_Io()
    {
        using AssemblyDefinition definition = ReadThisAssembly();
        ModuleDefinition module = definition.MainModule;

        CapabilityRule fileRule = new(
            "NO-FILES",
            "reading or writing files",
            "the reason the rule exists",
            ["System.IO.File"]);
        CapabilityRule directoryRule = new(
            "NO-DIRECTORIES",
            "enumerating or creating directories",
            "the reason the rule exists",
            ["System.IO.Directory"]);

        TypeDefinition tracker = TypeWithAFieldOf(
            module,
            TrackerNamespace,
            TrackerName,
            "System.IO",
            "FileStream");
        TypeDefinition product = TypeWithAFieldOf(
            module,
            "Harbor.Tui.CellForge.Panels",
            "CellForgeFileTreePanel",
            "System.IO",
            "Directory");

        IReadOnlyList<CapabilityHit> withSkip = IlCapabilityProbe.ScanTypes(
            [tracker, product], "Harbor.Fake.Presentation", [fileRule, directoryRule]);
        IReadOnlyList<CapabilityHit> withoutSkip = IlCapabilityProbe.ScanTypes(
            [tracker, product], "Harbor.Fake.Presentation", [fileRule, directoryRule], static _ => false);

        // The OLD verdict: the instrumenter's own body was read, and the probe
        // reported a filesystem capability the product does not have.
        await Assert.That(withoutSkip.Count).IsEqualTo(2)
            .Because("without the skip the probe reads the instrumenter's body as well as "
                   + "the product's, and that is the verdict CI run 39550199613 printed "
                   + "against seventeen Presentation assemblies. If this stops holding, the "
                   + "fixture no longer reproduces the defect and the assertions below "
                   + "would prove nothing.");
        await Assert.That(withoutSkip.Any(hit => hit.RuleId == "NO-FILES")).IsTrue()
            .Because("the instrumenter's own file I/O is what the old probe reported as a "
                   + "capability of the Presentation layer.");

        // The NEW verdict: the instrumenter's body is skipped.
        await Assert.That(withSkip.Any(hit => hit.RuleId == "NO-FILES")).IsFalse()
            .Because("the instrumenter's body is the instrumenter's: it is absent from an "
                   + "uninstrumented build and says nothing about the Presentation layer.");
        await Assert.That(withSkip.Count).IsEqualTo(1)
            .Because("exactly one capability is left — the product's own — and a second "
                   + "one would mean the skip let something through.");

        // And the rule keeps its teeth: the product's own I/O is still reported.
        await Assert.That(withSkip.Any(hit => hit.RuleId == "NO-DIRECTORIES")).IsTrue()
            .Because("the skip removes the instrumenter's body and nothing else — a "
                   + "Presentation type that touches the filesystem must still be reported, "
                   + "or the rule is green for a reason that has nothing to do with the "
                   + "architecture.");
    }

    /// <summary>
    ///     This test assembly's own module, read the way the probe reads the
    ///     product's: a <see cref="TypeReference" /> needs a module and a scope,
    ///     and this is the one module the test already knows how to find.
    /// </summary>
    private static AssemblyDefinition ReadThisAssembly()
    {
        Assembly self = typeof(PresentationCapabilityInstrumentationTests).Assembly;
        string name = self.GetName().Name
            ?? throw new InvalidOperationException("the test assembly has no simple name");
        return AssemblyDefinition.ReadAssembly(Path.Combine(AppContext.BaseDirectory, name + ".dll"));
    }

    /// <summary>
    ///     A type with one field of the given BCL type — the shape the rules
    ///     match on: the prefix <c>System.IO.File</c> covers the whole
    ///     <c>File*</c> family, so a field is as good a fixture as a method
    ///     body and needs no assembly of its own.
    /// </summary>
    private static TypeDefinition TypeWithAFieldOf(
        ModuleDefinition module,
        string ns,
        string name,
        string fieldTypeNamespace,
        string fieldTypeName)
    {
        var type = new TypeDefinition(ns, name, TypeAttributes.Public);
        type.Fields.Add(new FieldDefinition(
            "buffer",
            FieldAttributes.Public,
            new TypeReference(fieldTypeNamespace, fieldTypeName, module, module.TypeSystem.CoreLibrary)));
        return type;
    }
}
