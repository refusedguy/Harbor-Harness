using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Harbor.Tui.CellForge.CodeGen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2425 drives the generator from <see cref="AdditionalText"/>;
///     issue #2428 adds the two-phase <c>BuildRoot</c> output. Issue #2431
///     adds build-breaking <c>HZ0001</c> diagnostics with a location, refuses
///     any generated source for a document with diagnostics, and carries a
///     source checksum and positional comments.
/// </summary>
public class MarkupGeneratorAdditionalTextTests
{
    private const string SampleTargetPath = "Fixtures/Sample.harbor-ui";
    private const string ContractHint = "CellForgeRootContract.g.cs";

    private static readonly string SampleFullPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", SampleTargetPath));

    [Test]
    public async Task Generator_ReadsMarkupAdditionalText_AndUsesTargetPath()
    {
        var file = new TestAdditionalText("/checkout/one/" + SampleTargetPath, File.ReadAllText(SampleFullPath));
        var run = Run(file, TestOptionsProvider.For(file, SampleTargetPath));

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var declarations = Source(run, ".Declarations.g.cs");
        await Assert.That(declarations).Contains("TargetPath = \"" + SampleTargetPath + "\"");
        await Assert.That(SourceExists(run, ".BuildRoot.g.cs")).IsTrue();
        await Assert.That(SourceExists(run, ContractHint)).IsTrue();
    }

    [Test]
    public async Task Generator_KeysOnTargetPath_NotOnAbsoluteCheckoutPath()
    {
        var text = File.ReadAllText(SampleFullPath);
        var first = new TestAdditionalText("/checkout/one/" + SampleTargetPath, text);
        var elsewhere = new TestAdditionalText("/agent-z/checkouts/other/" + SampleTargetPath, text);

        var a = Run(first, TestOptionsProvider.For(first, SampleTargetPath));
        var b = Run(elsewhere, TestOptionsProvider.For(elsewhere, SampleTargetPath));

        var sourcesA = Sources(a);
        var sourcesB = Sources(b);

        await Assert.That(a.Diagnostics.Length).IsEqualTo(0);
        await Assert.That(b.Diagnostics.Length).IsEqualTo(0);
        await Assert.That(sourcesA.Length).IsEqualTo(sourcesB.Length);
        foreach (var source in sourcesA)
        {
            await Assert.That(sourcesB.Any(other =>
                other.HintName == source.HintName
                && other.SourceText.ContentEquals(source.SourceText))).IsTrue();
        }
    }

    [Test]
    public async Task Generator_IgnoresOtherAdditionalFiles()
    {
        var file = new TestAdditionalText("/checkout/one/Schemas/pin.json", "{}");
        var run = Run(file, TestOptionsProvider.For(file, "Schemas/pin.json"));

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        // Only the shared root contract survives: no markup, no partial class.
        await Assert.That(Sources(run).Length).IsEqualTo(1);
        await Assert.That(SourceExists(run, ContractHint)).IsTrue();
    }

    [Test]
    public async Task Generator_ParsesMarkup_AndForwardsSyntaxDiagnostics()
    {
        var valid = new TestAdditionalText("/checkout/one/" + SampleTargetPath, "text \"hello\"\n");
        var validRun = Run(valid, TestOptionsProvider.For(valid, SampleTargetPath));

        await Assert.That(validRun.Diagnostics.Length).IsEqualTo(0);
        await Assert.That(Source(validRun, ".Declarations.g.cs")).Contains("RootCount = 1");
        await Assert.That(Source(validRun, ".BuildRoot.g.cs")).Contains("root.Text(1, \"hello\")");

        var invalid = new TestAdditionalText("/checkout/one/Bad.harbor-ui", "widget \"x\"\n");
        var invalidRun = Run(invalid, TestOptionsProvider.For(invalid, "Bad.harbor-ui"));

        await Assert.That(invalidRun.Diagnostics.Length).IsEqualTo(1);
        await Assert.That(invalidRun.Diagnostics[0].Id).IsEqualTo("HZ0001");
        await Assert.That(invalidRun.Diagnostics[0].GetMessage()).Contains("Unknown node kind");
        await Assert.That(SourceExists(invalidRun, ".Declarations.g.cs")).IsFalse();
        await Assert.That(SourceExists(invalidRun, ".BuildRoot.g.cs")).IsFalse();
    }

    [Test]
    public async Task Generator_BadMarkup_ReportsHZ0001_WithLineAndColumn_AndEmitsNoSources()
    {
        const string bad = "panel \"ok\"\n  not_a_widget \"x\"\n";
        var file = new TestAdditionalText("/checkout/one/" + SampleTargetPath, bad);
        var run = Run(file, TestOptionsProvider.For(file, SampleTargetPath));

        await Assert.That(run.Diagnostics.Length).IsEqualTo(1);
        var diagnostic = run.Diagnostics[0];
        await Assert.That(diagnostic.Id).IsEqualTo("HZ0001");
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);

        var position = diagnostic.Location.GetLineSpan().StartLinePosition;
        await Assert.That(position.Line).IsEqualTo(1);
        await Assert.That(position.Character).IsEqualTo(2);
        await Assert.That(SourceExists(run, ".Declarations.g.cs")).IsFalse();
        await Assert.That(SourceExists(run, ".BuildRoot.g.cs")).IsFalse();
    }

    [Test]
    public async Task Generator_DocumentWithDiagnostics_GetsNoSource_WhileValidSiblingDoes()
    {
        var good = new TestAdditionalText("/checkout/one/Good.harbor-ui", "text \"ok\"\n");
        var bad = new TestAdditionalText("/checkout/one/Bad.harbor-ui", "widget \"x\"\n");
        var options = TestOptionsProvider.For(good, "Good.harbor-ui").With(bad, "Bad.harbor-ui");
        var driver = CSharpGeneratorDriver.Create(
            generators: [new MarkupGenerator().AsSourceGenerator()],
            additionalTexts: [good, bad],
            optionsProvider: options);
        var run = driver.RunGenerators(CSharpCompilation.Create("MarkupWiring")).GetRunResult();

        // The bad document reports an error and is filtered out of both source
        // outputs. Its sibling still emits both halves: the incremental
        // pipeline carries no generated type for a document that failed.
        await Assert.That(run.Diagnostics.Length).IsEqualTo(1);
        await Assert.That(run.Diagnostics[0].Id).IsEqualTo("HZ0001");
        var declarations = run.Results.SelectMany(r => r.GeneratedSources)
            .Where(s => s.HintName.EndsWith(".Declarations.g.cs", StringComparison.Ordinal))
            .ToImmutableArray();
        await Assert.That(declarations.Length).IsEqualTo(1);
        await Assert.That(declarations[0].SourceText.ToString()).Contains("TargetPath = \"Good.harbor-ui\"");
        await Assert.That(run.Results.SelectMany(r => r.GeneratedSources)
            .Count(s => s.HintName.EndsWith(".BuildRoot.g.cs", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task Generator_EmitsRootContractOnce_BeforePerFileSources()
    {
        var run = RunText(
            "panel \"outer\":\n    text \"a\"\n",
            "Fixtures/Two.harbor-ui");

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var contracts = Sources(run).Where(s => s.HintName == ContractHint).ToArray();
        await Assert.That(contracts.Length).IsEqualTo(1);

        var contract = contracts[0].SourceText.ToString();
        await Assert.That(contract).Contains("interface ICellForgeRoot");
        await Assert.That(contract).Contains("void Begin(int sequence)");
        await Assert.That(contract).Contains("void Text(int sequence, string text)");
        await Assert.That(contract).Contains("void End()");
    }

    [Test]
    public async Task Generator_TwoPhases_DeclarationsThenBuildRoot()
    {
        var run = RunText(
            "panel \"outer\":\n    text \"a\"\n",
            "Fixtures/Phases.harbor-ui");

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var declarations = Source(run, ".Declarations.g.cs");
        await Assert.That(declarations).Contains("partial class HarborUi_Fixtures_Phases_harbor_ui");
        await Assert.That(declarations).Contains("internal const string TargetPath");
        await Assert.That(declarations).Contains("RootCount = 1");
        await Assert.That(declarations).Contains("SequenceCount = 3");

        var buildRoot = Source(run, ".BuildRoot.g.cs");
        await Assert.That(buildRoot).Contains("partial class HarborUi_Fixtures_Phases_harbor_ui");
        await Assert.That(buildRoot).Contains("internal static void BuildRoot(ICellForgeRoot root)");
        await Assert.That(buildRoot).Contains("root.Begin(0)");
        await Assert.That(buildRoot).Contains("root.Text(2, \"a\")");
        await Assert.That(buildRoot).Contains("root.End()");
    }

    [Test]
    public async Task BuildRoot_AssignsSequenceNumbersInPreOrder()
    {
        var markup = string.Join("\n",
            "panel \"outer\":",
            "    text \"a\"",
            "    panel \"inner\":",
            "        text \"b\"");
        var run = RunText(markup, "Fixtures/Nested.harbor-ui");

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var buildRoot = Source(run, ".BuildRoot.g.cs");
        var expected = string.Join("\n",
            "        root.Begin(0); // Panel, line 1",
            "        root.Begin(1); // Text, line 2",
            "        root.Text(2, \"a\"); // Text, line 2",
            "        root.End(); // Text, line 2",
            "        root.Begin(3); // Panel, line 3",
            "        root.Begin(4); // Text, line 4",
            "        root.Text(5, \"b\"); // Text, line 4",
            "        root.End(); // Text, line 4",
            "        root.End(); // Panel, line 3",
            "        root.End(); // Panel, line 1");
        await Assert.That(buildRoot).Contains(expected);
    }

    [Test]
    public async Task Declarations_CarrySourceChecksum_ThatChangesWithText()
    {
        var first = RunText("text \"one\"\n", "Fixtures/Checksum.harbor-ui");
        var second = RunText("text \"two\"\n", "Fixtures/Checksum.harbor-ui");

        var sourceA = Source(first, ".Declarations.g.cs");
        var sourceB = Source(second, ".Declarations.g.cs");

        await Assert.That(sourceA).Contains("SourceChecksum = \"sha256:");
        await Assert.That(sourceA).IsNotEqualTo(sourceB);
    }

    [Test]
    public async Task GeneratedCode_CarriesNodeAndOperationPositions()
    {
        const string markup = """
            panel "main":
              .title "Files"
            """;
        var run = RunText(markup, "Fixtures/Positions.harbor-ui");

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var declarations = Source(run, ".Declarations.g.cs");
        await Assert.That(declarations).Contains("// node Panel @ 1:1");
        await Assert.That(declarations).Contains("// op new @ 1:1");
        await Assert.That(declarations).Contains("// op title @ 2:4");
    }

    [Test]
    public async Task GeneratedOutput_IsPlainCSharp()
    {
        var run = RunText("panel \"x\":\n    text \"hi\\nthere\"\n", "Fixtures/Plain.harbor-ui");

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var buildRoot = Source(run, ".BuildRoot.g.cs");
        await Assert.That(buildRoot).Contains("root.Text(2, \"hi\\nthere\")");
        foreach (var source in Sources(run))
        {
            var code = source.SourceText.ToString();
            await Assert.That(code.Contains("System.Reflection", StringComparison.Ordinal)).IsFalse();
            await Assert.That(code.Contains("Reflection.Emit", StringComparison.Ordinal)).IsFalse();
            await Assert.That(code.Contains("Activator.", StringComparison.Ordinal)).IsFalse();
            await Assert.That(code.Contains("MethodInfo", StringComparison.Ordinal)).IsFalse();
            await Assert.That(code.Contains("dynamic", StringComparison.Ordinal)).IsFalse();
        }
    }

    [Test]
    public async Task Generator_CachesOnFileInputs()
    {
        const string text = "text \"same\"\n";
        var a = RunText(text, "Fixtures/Cache.harbor-ui");
        var b = RunText(text, "Fixtures/Cache.harbor-ui");

        await Assert.That(Sources(a).Length).IsEqualTo(Sources(b).Length);
        foreach (var source in Sources(a))
        {
            await Assert.That(Sources(b).Any(other =>
                other.HintName == source.HintName
                && other.SourceText.ContentEquals(source.SourceText))).IsTrue();
        }

        var changedText = RunText("text \"different\"\n", "Fixtures/Cache.harbor-ui");
        await Assert.That(Source(changedText, ".BuildRoot.g.cs"))
            .IsNotEqualTo(Source(a, ".BuildRoot.g.cs"));

        var otherPath = RunText(text, "Fixtures/Other.harbor-ui");
        // The target path is part of the generated identifier, so both halves
        // move with it; the sequence structure itself stays the same.
        await Assert.That(Source(otherPath, ".BuildRoot.g.cs"))
            .IsNotEqualTo(Source(a, ".BuildRoot.g.cs"));
        await Assert.That(Source(otherPath, ".BuildRoot.g.cs"))
            .Contains("root.Begin(0)");
        await Assert.That(Source(otherPath, ".Declarations.g.cs"))
            .Contains("TargetPath = \"Fixtures/Other.harbor-ui\"");
    }

    /// <summary>
    ///     Issue #2518: layout nodes emit real runtime construction code into
    ///     the phase-2 partial class. Row/Column map to FlexSplitNode, Overlay
    ///     to ZStackNode, and .track maps to a FlexTrack factory.
    /// </summary>
    [Test]
    public async Task Generator_LayoutNodes_EmitFlexSplitAndZStackWithTracks()
    {
        const string render = "global::Harbor.Tui.CellForge.Rendering.";
        const string markup = """
            row "main":
                .gap 2
                .justify space-between
                text "a":
                    .track fill 2
                text "b":
                    .track fixed 12
                text "c":
                    .track min 4
                text "d":
                    .track max 8
                text "e":
                    .track percent 0.5
            column "side":
                text "top"
            overlay "stack":
                text "front"
            """;

        var run = RunText(markup, "Fixtures/Layout.harbor-ui");

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var source = Source(run, ".BuildRoot.g.cs");

        await Assert.That(source).Contains("INode? BuildLayout()");
        await Assert.That(source)
            .Contains($"FlexSplitNode({render}SplitDir.Horizontal, 2, {render}FlexJustify.SpaceBetween)");
        await Assert.That(source).Contains($"FlexSplitNode({render}SplitDir.Vertical, 0)");
        await Assert.That(source).Contains("ZStackNode()");
        await Assert.That(source).Contains($"{render}FlexTrack.Fill(2f)");
        await Assert.That(source).Contains($"{render}FlexTrack.Fixed(12)");
        await Assert.That(source).Contains($"{render}FlexTrack.Min(4)");
        await Assert.That(source).Contains($"{render}FlexTrack.Max(8)");
        await Assert.That(source).Contains($"{render}FlexTrack.Percent(0.5f)");
    }

    [Test]
    public async Task Generator_EmitsNodeIdsFromMarkup()
    {
        const string markup = "panel \"main\":\n    .id \"files-panel\"\n";
        var run = RunText(markup, "Fixtures/Ids.harbor-ui");

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var source = Source(run, ".Declarations.g.cs");
        await Assert.That(source).Contains("NodeIds");
        await Assert.That(source).Contains("\"files-panel\"");
    }

    /// <summary>
    ///     #2438: the declared .id reaches the automation protocol through
    ///     BuildRoot, not through a test double.
    /// </summary>
    [Test]
    public async Task Generator_BuildRoot_EmitsTestIdCallsFromDeclaredIds()
    {
        const string markup = """
            panel "main":
                .id "files-panel"
                text "x":
                    .id "row-1"
            """;
        var run = RunText(markup, "Fixtures/TestIds.harbor-ui");

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var buildRoot = Source(run, ".BuildRoot.g.cs");
        await Assert.That(buildRoot).Contains("root.TestId(0, \"files-panel\")");
        await Assert.That(buildRoot).Contains("root.TestId(1, \"row-1\")");
        await Assert.That(Source(run, ContractHint)).Contains("void TestId(int sequence, string testId)");
    }

    [Test]
    public async Task Generator_KeyedChild_UsesNamedAddOverload()
    {
        const string markup = """
            row "main":
                text "a":
                    .id "alpha"
                text "b"
            """;
        var run = RunText(markup, "Fixtures/Keyed.harbor-ui");
        var buildRoot = Source(run, ".BuildRoot.g.cs");

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        await Assert.That(buildRoot).Contains("n0.Add(n1, \"alpha\", global::Harbor.Tui.CellForge.Rendering.FlexTrack.Fill());");
        await Assert.That(buildRoot).Contains("n0.Add(n2, global::Harbor.Tui.CellForge.Rendering.FlexTrack.Fill());");
    }

    private static GeneratorDriverRunResult Run(AdditionalText file, AnalyzerConfigOptionsProvider options)
    {
        var compilation = CSharpCompilation.Create("MarkupWiring");
        var driver = CSharpGeneratorDriver.Create(
            generators: [new MarkupGenerator().AsSourceGenerator()],
            additionalTexts: [file],
            optionsProvider: options);
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static GeneratorDriverRunResult RunText(string text, string targetPath)
    {
        var file = new TestAdditionalText("/checkout/one/" + targetPath, text);
        return Run(file, TestOptionsProvider.For(file, targetPath));
    }

    private static ImmutableArray<GeneratedSourceResult> Sources(GeneratorDriverRunResult run) =>
        run.Results.SelectMany(r => r.GeneratedSources).ToImmutableArray();

    private static bool SourceExists(GeneratorDriverRunResult run, string hintSuffix) =>
        Sources(run).Any(s => s.HintName.EndsWith(hintSuffix, StringComparison.Ordinal));

    private static string Source(GeneratorDriverRunResult run, string hintSuffix) =>
        Sources(run).Single(s => s.HintName.EndsWith(hintSuffix, StringComparison.Ordinal))
            .SourceText.ToString();

    /// <summary>An AdditionalText whose text is fixed and whose path is synthetic.</summary>
    private sealed class TestAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        internal TestAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text, Encoding.UTF8);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    /// <summary>
    ///     Mimics what MSBuild hands the compiler: the TargetPath metadata is
    ///     a base64-encoded per-file analyzer option.
    /// </summary>
    private sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly Dictionary<AdditionalText, AnalyzerConfigOptions> _perFile = [];

        private TestOptionsProvider()
        {
            GlobalOptions = new TestOptions(new Dictionary<string, string>());
        }

        internal static TestOptionsProvider For(AdditionalText file, string targetPath)
        {
            var provider = new TestOptionsProvider();
            return provider.With(file, targetPath);
        }

        internal TestOptionsProvider With(AdditionalText file, string targetPath)
        {
            _perFile[file] = new TestOptions(new Dictionary<string, string>
            {
                ["build_metadata.AdditionalFiles.TargetPath"] =
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(targetPath)),
            });
            return this;
        }

        public override AnalyzerConfigOptions GlobalOptions { get; }

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            _perFile.TryGetValue(textFile, out var options) ? options : GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
    }

    private sealed class TestOptions : AnalyzerConfigOptions
    {
        private readonly IReadOnlyDictionary<string, string> _values;

        internal TestOptions(IReadOnlyDictionary<string, string> values) => _values = values;

        public override bool TryGetValue(string key, out string? value)
        {
            if (_values.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }

            value = null;
            return false;
        }

        public override IEnumerable<string> Keys => _values.Keys;
    }
}
