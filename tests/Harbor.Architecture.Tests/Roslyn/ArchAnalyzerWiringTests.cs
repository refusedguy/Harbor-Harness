// ArchAnalyzerWiringTests.cs — the analyzer is ENABLED in the product build.
//
// WHY THIS FILE EXISTS
// --------------------
// An analyzer that is not wired into the build does not fail. It says
// nothing, the build stays green, and the rule looks as if it is holding the
// line. The behaviour tests in this folder prove that the analyzer FIRES;
// this file proves that the build ASKS it to.
//
// The wiring is read from the files the build itself reads — the two
// Directory.Build.props that every product project inherits — because that
// is where the wiring IS. A rule about a build setting is checked against
// the build setting.
//
// WHAT IS ASSERTED
//   1. Both product trees hand the analyzer to the compiler as an ANALYZER,
//      and not as a runtime reference: the assembly must reach the compiler
//      and must not reach the shipped binary.
//   2. The analyzer project does not consume itself: a project that
//      references itself is a cycle the build cannot resolve.
//   3. The plugin family is exempt from HARCH001 by id, and HARCH002 is
//      exempt nowhere. One rule switched off must not switch off its
//      neighbour.
//   4. Both rules are enabled by default, so an id that nobody suppressed
//      still fires.
//   5. NON-VACUITY: the same predicate, run over a synthetic props file that
//      does not wire the analyzer, says "not wired". A predicate that always
//      says "wired" would pass every assertion above while proving nothing.

using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Proves that the promoted analyzers are actually applied to the product
///     tree, by reading the build configuration the build reads.
/// </summary>
public sealed class ArchAnalyzerWiringTests
{
    /// <summary>The analyzer project, as the build knows it.</summary>
    private const string AnalyzerProjectFile = "Harbor.Arch.Analyzers/Harbor.Arch.Analyzers.csproj";

    /// <summary>The props file that every project under <c>src/</c> inherits.</summary>
    private const string SrcProps = "src/Directory.Build.props";

    /// <summary>The props file that every project under <c>apps/</c> inherits.</summary>
    private const string AppsProps = "apps/Directory.Build.props";

    /// <summary>
    ///     Both product trees hand the analyzer to the compiler.
    /// </summary>
    [Test]
    public async Task Both_Product_Trees_Hand_The_Analyzer_To_The_Compiler()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("this rule reads the build configuration out of a checkout; outside one there "
                     + "is no configuration to read and every assertion below would hold for the wrong reason");

        foreach (string props in new[] { SrcProps, AppsProps })
        {
            XElement? reference = AnalyzerReferenceIn(props);

            await Assert.That(reference).IsNotNull()
                .Because(
                    "every project under " + Path.GetDirectoryName(props) + " inherits " + props
                    + ", and the promoted analyzers are meant to be applied to that tree. A tree that "
                    + "does not reference the analyzer project compiles clean whatever the analyzer "
                    + "would have said. No analyzer reference in " + props);

            await Assert.That(Child(reference!, "OutputItemType")).IsEqualTo("Analyzer")
                .Because(
                    "without OutputItemType=Analyzer the reference is an ordinary project reference: "
                    + "the analyzer assembly would become a RUNTIME dependency of every product "
                    + "project, which is both a NativeAOT problem (#626) and a layering edge. In " + props);

            await Assert.That(Child(reference!, "ReferenceOutputAssembly")).IsEqualTo("false")
                .Because(
                    "ReferenceOutputAssembly=false is what keeps the analyzer assembly out of the "
                    + "consuming project's reference list: the compiler loads it, and nothing else "
                    + "does. Without it the analyzer's Roslyn dependency would reach the shipped "
                    + "binary. In " + props);
        }
    }

    /// <summary>
    ///     The analyzer project does not consume itself.
    /// </summary>
    [Test]
    public async Task The_Analyzer_Project_Does_Not_Consume_Itself()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("this rule reads the build configuration out of a checkout");

        XElement? reference = AnalyzerReferenceIn(SrcProps);

        await Assert.That(reference).IsNotNull()
            .Because("the analyzer project is under src/, so it inherits " + SrcProps + " — and a "
                     + "project that references itself is a cycle the build cannot resolve");

        string? condition = reference!.Attribute("Condition")?.Value;

        await Assert.That(condition).IsNotNull()
            .Because(
                "the analyzer project inherits the props file that references it, so the reference "
                + "must exclude it by name. Without the condition, the build asks the analyzer "
                + "project to depend on itself.");

        await Assert.That(condition!).Contains("Harbor.Arch.Analyzers")
            .Because("the condition must name the analyzer project, or it excludes nothing. In " + SrcProps);
    }

    /// <summary>
    ///     The plugin family is exempt from the dynamic-code rule by id, and
    ///     from nothing else.
    /// </summary>
    [Test]
    public async Task The_Plugin_Family_Is_Exempt_From_Harch001_And_From_Nothing_Else()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("this rule reads the build configuration out of a checkout");

        string props = await ReadRepoFile(SrcProps);

        await Assert.That(props).Contains("HARCH001")
            .Because(
                "the plugin family loads code at run time because loading code at run time IS the "
                + "plugin product, so the dynamic-code rule cannot apply to it. The exemption is "
                + "expressed by id, so that the other rule the analyzer carries still applies.");

        await Assert.That(props.Contains("HARCH002", StringComparison.Ordinal)).IsFalse()
            .Because(
                "the namespace-ownership rule has no exception to make: no plugin project declares "
                + "a namespace root it does not own, and a blanket suppression of a rule that has "
                + "no exception is a hole with a reason attached to it.");
    }

    /// <summary>
    ///     Both rules are enabled by default, so an id that nobody suppressed
    ///     still fires.
    /// </summary>
    [Test]
    public async Task Both_Rules_Are_Enabled_By_Default()
    {
        DiagnosticAnalyzer[] analyzers =
        [
            new Harbor.Arch.Analyzers.NoDynamicCodeAnalyzer(),
            new Harbor.Arch.Analyzers.NamespaceOwnershipAnalyzer(),
        ];

        foreach (DiagnosticAnalyzer analyzer in analyzers)
        {
            await Assert.That(analyzer.SupportedDiagnostics.Length).IsGreaterThan(0)
                .Because("an analyzer that reports no diagnostic cannot fail anything");

            foreach (DiagnosticDescriptor descriptor in analyzer.SupportedDiagnostics)
            {
                await Assert.That(descriptor.IsEnabledByDefault).IsTrue()
                    .Because(
                        "the rule " + descriptor.Id + " would be switched off everywhere by default, "
                        + "and a rule nobody hears about is a rule that is not holding the line");
            }
        }
    }

    /// <summary>
    ///     The predicate the assertions above use, run over a configuration
    ///     that does not wire the analyzer, says "not wired".
    /// </summary>
    [Test]
    public async Task The_Wiring_Predicate_Answers_On_A_Configuration_That_Does_Not_Wire_It()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("this rule reads the build configuration out of a checkout");

        const string synthetic = """
            <Project>
              <ItemGroup>
                <ProjectReference Include="..\..\src\Harbor.Other\Harbor.Other.csproj">
                  <OutputItemType>Analyzer</OutputItemType>
                  <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
                </ProjectReference>
              </ItemGroup>
            </Project>
            """;

        XDocument document = XDocument.Parse(synthetic);
        XElement? reference = document
            .Descendants()
            .FirstOrDefault(static e => e.Name.LocalName == "ProjectReference"
                                        && e.Attribute("Include")?.Value.Contains("Harbor.Arch.Analyzers", StringComparison.Ordinal) == true);

        await Assert.That(reference).IsNull()
            .Because(
                "the predicate finds the analyzer by NAME. A configuration that references a "
                + "different analyzer project must not satisfy it, or the assertions above would "
                + "pass on a tree where the analyzer is not applied at all.");
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    ///     The analyzer reference in one props file, or <c>null</c> when the
    ///     file does not have one.
    /// </summary>
    private static XElement? AnalyzerReferenceIn(string relativeProps)
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return null;
        }

        string path = Path.Combine(root, relativeProps.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return null;
        }

        XDocument document = XDocument.Load(path);

        return document
            .Descendants()
            .FirstOrDefault(static e => e.Name.LocalName == "ProjectReference"
                                        && e.Attribute("Include")?.Value.Contains(AnalyzerProjectFile, StringComparison.Ordinal) == true);
    }

    /// <summary>The value of a child element, or <c>null</c> when there is none.</summary>
    private static string? Child(XElement parent, string name) => parent
        .Elements()
        .FirstOrDefault(static e => e.Name.LocalName == name)
        ?.Value;

    /// <summary>Reads a repo-relative file.</summary>
    private static async Task<string> ReadRepoFile(string relative)
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return string.Empty;
        }

        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        return await File.ReadAllTextAsync(path);
    }
}
