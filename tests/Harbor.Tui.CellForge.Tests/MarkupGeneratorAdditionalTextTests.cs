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
///     Issue #2425: the <c>.harbor-ui</c> file reaches the incremental
///     generator as an <see cref="AdditionalText"/>, and the key it is cached
///     under is the project-relative <c>TargetPath</c>, not the absolute path.
/// </summary>
/// <remarks>
///     The driver half drives the real generator with the real sample file. A
///     second run uses the same <c>TargetPath</c> under a different absolute
///     checkout root; if the generator keyed on <see cref="AdditionalText.Path"/>
///     the two outputs would differ, which is the regression this guards.
/// </remarks>
public class MarkupGeneratorAdditionalTextTests
{
    private const string SampleTargetPath = "Fixtures/Sample.harbor-ui";

    private static readonly string SampleFullPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", SampleTargetPath));

    [Test]
    public async Task Generator_ReadsMarkupAdditionalText_AndUsesTargetPath()
    {
        var file = new TestAdditionalText("/checkout/one/" + SampleTargetPath, File.ReadAllText(SampleFullPath));
        var run = Run(file, TestOptionsProvider.For(file, SampleTargetPath));

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var sources = run.Results.SelectMany(r => r.GeneratedSources).ToImmutableArray();
        await Assert.That(sources.Length).IsEqualTo(1);
        await Assert.That(sources[0].SourceText.ToString())
            .Contains("TargetPath = \"" + SampleTargetPath + "\"");
    }

    [Test]
    public async Task Generator_KeysOnTargetPath_NotOnAbsoluteCheckoutPath()
    {
        var text = File.ReadAllText(SampleFullPath);
        var first = new TestAdditionalText("/checkout/one/" + SampleTargetPath, text);
        var elsewhere = new TestAdditionalText("/agent-z/checkouts/other/" + SampleTargetPath, text);

        var a = Run(first, TestOptionsProvider.For(first, SampleTargetPath));
        var b = Run(elsewhere, TestOptionsProvider.For(elsewhere, SampleTargetPath));

        var sourcesA = a.Results.SelectMany(r => r.GeneratedSources).ToImmutableArray();
        var sourcesB = b.Results.SelectMany(r => r.GeneratedSources).ToImmutableArray();

        await Assert.That(a.Diagnostics.Length).IsEqualTo(0);
        await Assert.That(b.Diagnostics.Length).IsEqualTo(0);
        await Assert.That(sourcesA.Length).IsEqualTo(1);
        await Assert.That(sourcesB.Length).IsEqualTo(1);
        await Assert.That(sourcesA[0].HintName).IsEqualTo(sourcesB[0].HintName);
        await Assert.That(sourcesA[0].SourceText.ContentEquals(sourcesB[0].SourceText)).IsTrue();
    }

    [Test]
    public async Task Generator_IgnoresOtherAdditionalFiles()
    {
        var file = new TestAdditionalText("/checkout/one/Schemas/pin.json", "{}");
        var run = Run(file, TestOptionsProvider.For(file, "Schemas/pin.json"));

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        await Assert.That(run.Results.SelectMany(r => r.GeneratedSources).Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Generator_ParsesMarkup_AndForwardsSyntaxDiagnostics()
    {
        var valid = new TestAdditionalText("/checkout/one/" + SampleTargetPath, "text \"hello\"\n");
        var validRun = Run(valid, TestOptionsProvider.For(valid, SampleTargetPath));

        await Assert.That(validRun.Diagnostics.Length).IsEqualTo(0);
        var validSource = validRun.Results.SelectMany(r => r.GeneratedSources).Single().SourceText.ToString();
        await Assert.That(validSource).Contains("RootCount = 1");

        var invalid = new TestAdditionalText("/checkout/one/Bad.harbor-ui", "widget \"x\"\n");
        var invalidRun = Run(invalid, TestOptionsProvider.For(invalid, "Bad.harbor-ui"));

        await Assert.That(invalidRun.Diagnostics.Length).IsEqualTo(1);
        await Assert.That(invalidRun.Diagnostics[0].Id).IsEqualTo("HUI001");
        await Assert.That(invalidRun.Diagnostics[0].GetMessage()).Contains("Unknown node kind");
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
            provider._perFile[file] = new TestOptions(new Dictionary<string, string>
            {
                ["build_metadata.AdditionalFiles.TargetPath"] =
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(targetPath)),
            });
            return provider;
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
