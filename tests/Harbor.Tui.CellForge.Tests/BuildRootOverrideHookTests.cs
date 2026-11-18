using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
///     Issue #2435: the generated BuildRoot has a Debug-only trampoline that
///     checks a static Action slot before running its compiled body. Release
///     and AOT builds compile the check out, so the hook contributes no
///     dynamic code to the shipped binary.
/// </summary>
public class BuildRootOverrideHookTests
{
    private static GeneratorDriverRunResult RunText(string text, string targetPath)
    {
        var file = new TestAdditionalText("/checkout/one/" + targetPath, text);
        var options = new TestOptionsProvider(file, targetPath);
        var compilation = CSharpCompilation.Create("OverrideWiring");
        var driver = CSharpGeneratorDriver.Create(
            generators: [new MarkupGenerator().AsSourceGenerator()],
            additionalTexts: [file],
            optionsProvider: options);
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static string Source(GeneratorDriverRunResult run, string hintSuffix) =>
        run.Results.SelectMany(r => r.GeneratedSources)
            .Single(s => s.HintName.EndsWith(hintSuffix, StringComparison.Ordinal))
            .SourceText.ToString();

    private static ImmutableArray<GeneratedSourceResult> Sources(GeneratorDriverRunResult run) =>
        run.Results.SelectMany(r => r.GeneratedSources).ToImmutableArray();

    [Test]
    public async Task Declarations_EmitDebugOnlyOverrideField_OfActionType()
    {
        var run = RunText("panel \"main\":\n    text \"hello\"\n", "Fixtures/Override.harbor-ui");

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var declarations = Source(run, ".Declarations.g.cs");
        await Assert.That(declarations).Contains("#if DEBUG");
        await Assert.That(declarations).Contains(
            "internal static System.Action<System.Action<int>, System.Action<int, string>, System.Action>? BuildRootOverride;");
        int field = declarations.IndexOf("BuildRootOverride;", StringComparison.Ordinal);
        int endIf = declarations.IndexOf("#endif", StringComparison.Ordinal);
        await Assert.That(field).IsGreaterThan(0);
        await Assert.That(endIf).IsGreaterThan(field);
    }

    [Test]
    public async Task BuildRoot_ChecksTheActionSlotBeforeItsCompiledBody()
    {
        var run = RunText("panel \"main\":\n    text \"hello\"\n", "Fixtures/Override.harbor-ui");

        var buildRoot = Source(run, ".BuildRoot.g.cs");
        await Assert.That(buildRoot).Contains("#if DEBUG");
        await Assert.That(buildRoot).Contains(
            "?? global::Harbor.Abstractions.Dsl.RootBuilderOverride.Current;");
        await Assert.That(buildRoot).Contains("devOverride(root.Begin, root.Text, root.End);");
        await Assert.That(buildRoot).Contains("return;");

        int guard = buildRoot.IndexOf("#if DEBUG", StringComparison.Ordinal);
        int firstBegin = buildRoot.IndexOf("root.Begin(0)", StringComparison.Ordinal);
        int endIf = buildRoot.IndexOf("#endif", StringComparison.Ordinal);
        await Assert.That(guard).IsGreaterThan(0);
        await Assert.That(firstBegin).IsGreaterThan(endIf);
    }

    [Test]
    public async Task OverrideHook_UsesNoReflectionOrDynamicCode()
    {
        var run = RunText("panel \"main\":\n    text \"hello\"\n", "Fixtures/OverridePlain.harbor-ui");

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        foreach (var source in Sources(run))
        {
            string code = source.SourceText.ToString();
            await Assert.That(code.Contains("System.Reflection", StringComparison.Ordinal)).IsFalse();
            await Assert.That(code.Contains("Reflection.Emit", StringComparison.Ordinal)).IsFalse();
            await Assert.That(code.Contains("MethodInfo", StringComparison.Ordinal)).IsFalse();
            await Assert.That(code.Contains("Activator.", StringComparison.Ordinal)).IsFalse();
            await Assert.That(code.Contains("dynamic", StringComparison.Ordinal)).IsFalse();
        }
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

    /// <summary>MSBuild hands TargetPath to the generator as a base64 analyzer option.</summary>
    private sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _options;

        internal TestOptionsProvider(AdditionalText file, string targetPath)
        {
            GlobalOptions = new TestOptions(new Dictionary<string, string>());
            _options = new TestOptions(new Dictionary<string, string>
            {
                ["build_metadata.AdditionalFiles.TargetPath"] =
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(targetPath)),
            });
            File = file;
        }

        private AdditionalText File { get; }

        public override AnalyzerConfigOptions GlobalOptions { get; }

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            ReferenceEquals(textFile, File) ? _options : GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        private sealed class TestOptions : AnalyzerConfigOptions
        {
            private readonly IReadOnlyDictionary<string, string> _values;

            internal TestOptions(IReadOnlyDictionary<string, string> values) => _values = values;

            public override bool TryGetValue(string key, out string? value)
            {
                if (_values.TryGetValue(key, out string? found))
                {
                    value = found;
                    return true;
                }

                value = null;
                return false;
            }
        }
    }
}
