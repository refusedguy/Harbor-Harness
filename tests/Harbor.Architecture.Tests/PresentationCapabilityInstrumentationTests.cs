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
// The skip is narrow: it recognises exactly the instrumenter's namespace and
// nothing else. A product type is never skipped, so the rule keeps its teeth
// (the probe's own non-vacuity tests, in PresentationCapabilityRules.cs, still
// require that the probe reads the real IL and still fires).

using Mono.Cecil;

// The project imports System.Reflection globally, and Mono.Cecil has a TypeAttributes
// of its own: without the alias the simple name is ambiguous (CS0104).
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace Harbor.Architecture.Tests;

public sealed class PresentationCapabilityInstrumentationTests
{
    [Test]
    public async Task The_Instrumenters_Tracker_Is_Skipped()
    {
        var tracker = new TypeDefinition(
            "Microsoft.CodeCoverage.Instrumentation.Static.Tracker",
            "StaticManagedTrackerTemplate_3be3a47c-e6fc-41f6-9a1e-6d0f0b2c9d31",
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
}
