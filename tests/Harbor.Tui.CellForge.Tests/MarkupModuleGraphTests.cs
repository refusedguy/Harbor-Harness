using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Harbor.Abstractions.Dsl;
using Harbor.Tui.CellForge.CodeGen;
using Harbor.Tui.CellForge.CodeGen.DevReload;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #3083: imports build a module graph, every module gets its own
///     generated builder, cycles are diagnostics, and the dev watcher follows
///     the whole graph.
/// </summary>
public class MarkupModuleGraphTests
{
    private const string SharedText = "panel \"shared-header\":\n    text \"Shared header\"\n";
    private const string ScreenText =
        "import \"Fixtures/Shared.harbor-ui\"\n\npanel \"screen\":\n    .use \"Fixtures/Shared.harbor-ui\"\n    text \"Screen body\"\n";

    // The dev test writes two sibling files in a temp directory, so its
    // imports are relative to that directory rather than to Fixtures/.
    private const string DevScreenText =
        "import \"Shared.harbor-ui\"\n\npanel \"screen\":\n    .use \"Shared.harbor-ui\"\n    text \"Screen body\"\n";

    [Test]
    public async Task Parser_ImportDirective_CapturesRawPathAndPosition()
    {
        var result = MarkupParser.Parse(ScreenText);

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Imports.Count).IsEqualTo(1);
        await Assert.That(result.Imports[0].Path).IsEqualTo("Fixtures/Shared.harbor-ui");
        await Assert.That(result.Imports[0].Line).IsEqualTo(1);
        await Assert.That(result.Imports[0].Column).IsEqualTo(1);
        // The import is a directive, not an extra widget node.
        await Assert.That(result.Arena.Roots.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Parser_ImportWithoutQuotedPath_ReportsDiagnostic()
    {
        var result = MarkupParser.Parse("import Shared\ntext \"x\"\n");

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics[0].Message).Contains("one quoted path");
    }

    [Test]
    public async Task Resolver_TwoFiles_ExposesImportedSpecByName_AndCountsSequences()
    {
        var graph = MarkupModuleResolver.Resolve(Sources(
            ("Fixtures/Shared.harbor-ui", SharedText),
            ("Fixtures/Screen.harbor-ui", ScreenText)));

        await Assert.That(graph.Findings.Count).IsEqualTo(0);
        await Assert.That(graph.Modules.Count).IsEqualTo(2);
        await Assert.That(graph.Arena.FindModule("Fixtures/Shared.harbor-ui")).IsNotNull();
        await Assert.That(graph.Arena.FindModule("Fixtures/Screen.harbor-ui")!.Imports.Count).IsEqualTo(1);
        await Assert.That(graph.SequenceCounts["Fixtures/Shared.harbor-ui"]).IsEqualTo(3);
        // Screen reserves one sequence for its panel, three for the imported
        // module and two for its own text node.
        await Assert.That(graph.SequenceCounts["Fixtures/Screen.harbor-ui"]).IsEqualTo(6);
    }

    [Test]
    public async Task Generator_TwoFiles_EmitsOneBuilderPerModule_AndCallsImportedBuilder()
    {
        var run = Run(
            ("Fixtures/Shared.harbor-ui", SharedText),
            ("Fixtures/Screen.harbor-ui", ScreenText));

        await Assert.That(run.Diagnostics.Length).IsEqualTo(0);
        var buildRoots = Generated(run, ".BuildRoot.g.cs");
        await Assert.That(buildRoots.Length).IsEqualTo(2);

        var screen = buildRoots.Single(source => source.Contains("class HarborUi_Fixtures_Screen_harbor_ui"));
        var shared = buildRoots.Single(source => source.Contains("class HarborUi_Fixtures_Shared_harbor_ui"));

        await Assert.That(screen).Contains("internal static void BuildModule(ICellForgeRoot root, int baseSequence)");
        await Assert.That(screen).Contains("HarborUi_Fixtures_Shared_harbor_ui.BuildModule(root, baseSequence + 1)");
        await Assert.That(shared).Contains("internal static void BuildRoot(ICellForgeRoot root)");
        await Assert.That(shared).Contains("root.Text(baseSequence + 2, \"Shared header\")");
    }

    [Test]
    public async Task Generator_CyclicImport_ReportsHZ0002WithPosition_AndDoesNotCrash()
    {
        var first = "import \"Fixtures/B.harbor-ui\"\ntext \"a\"\n";
        var second = "import \"Fixtures/A.harbor-ui\"\ntext \"b\"\n";
        var run = Run(
            ("Fixtures/A.harbor-ui", first),
            ("Fixtures/B.harbor-ui", second));

        var cycle = run.Diagnostics.Single(diagnostic => diagnostic.Id == "HZ0002");
        await Assert.That(cycle.Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(cycle.GetMessage()).Contains("Cyclic import");
        var position = cycle.Location.GetLineSpan().StartLinePosition;
        await Assert.That(position.Line).IsEqualTo(0);
        await Assert.That(position.Character).IsEqualTo(0);
        // A cycle blocks emission; the generator still produces its contract.
        await Assert.That(Generated(run, ".BuildRoot.g.cs").Length).IsEqualTo(0);
        await Assert.That(Generated(run, "CellForgeRootContract.g.cs").Length).IsEqualTo(1);
    }

    [Test]
    public async Task Generator_MissingImport_ReportsHZ0003AtTheImportLine()
    {
        var run = Run(("Fixtures/Lonely.harbor-ui",
            "import \"Fixtures/Absent.harbor-ui\"\ntext \"x\"\n"));

        var missing = run.Diagnostics.Single(diagnostic => diagnostic.Id == "HZ0003");
        await Assert.That(missing.GetMessage()).Contains("Absent.harbor-ui");
        await Assert.That(missing.Location.GetLineSpan().StartLinePosition.Line).IsEqualTo(0);
    }

    [Test]
    public async Task Generator_EditingImportedFile_ChangesThatModuleSource_AndKeepsTheCall()
    {
        var before = Run(
            ("Fixtures/Shared.harbor-ui", SharedText),
            ("Fixtures/Screen.harbor-ui", ScreenText));
        var after = Run(
            ("Fixtures/Shared.harbor-ui", SharedText.Replace("Shared header", "Shared changed", StringComparison.Ordinal)),
            ("Fixtures/Screen.harbor-ui", ScreenText));

        var sharedBefore = Generated(before, ".BuildRoot.g.cs")
            .Single(source => source.Contains("class HarborUi_Fixtures_Shared_harbor_ui"));
        var sharedAfter = Generated(after, ".BuildRoot.g.cs")
            .Single(source => source.Contains("class HarborUi_Fixtures_Shared_harbor_ui"));
        await Assert.That(sharedBefore).IsNotEqualTo(sharedAfter);
        await Assert.That(sharedAfter).Contains("Shared changed");

        var screenAfter = Generated(after, ".BuildRoot.g.cs")
            .Single(source => source.Contains("class HarborUi_Fixtures_Screen_harbor_ui"));
        await Assert.That(screenAfter).Contains("HarborUi_Fixtures_Shared_harbor_ui.BuildModule");
    }

    [Test]
    public async Task DevWatcher_ImportedFileChange_PatchesTheLiveExpandedTree()
    {
        string directory = Path.Combine(Path.GetTempPath(), "harbor-imports-" + Guid.NewGuid().ToString("N"));
        string sharedPath = Path.Combine(directory, "Shared.harbor-ui");
        string screenPath = Path.Combine(directory, "Screen.harbor-ui");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(sharedPath, SharedText);
            File.WriteAllText(screenPath, DevScreenText);

            var texts = ReadGraph(directory);
            string root = MarkupPaths.Normalize(screenPath);
            await Assert.That(DevReloadInterpreter.TryCreateFromGraph(
                texts, root, out var interpreter, out var error)).IsTrue();
            await Assert.That(error).IsEmpty();

            var channel = new DevLogChannel();
            interpreter!.AttachLog(channel);
            await Assert.That(interpreter.TextOf(4)).IsEqualTo("Shared header");

            var sharedKey = MarkupPaths.Normalize(sharedPath);
            var stamps = new Dictionary<string, DateTime>(StringComparer.Ordinal)
            {
                [sharedKey] = File.GetLastWriteTimeUtc(sharedPath),
                [root] = File.GetLastWriteTimeUtc(screenPath),
            };
            var sources = new Dictionary<string, DevMarkupFileSource>(StringComparer.Ordinal);
            foreach (var path in texts.Keys)
            {
                string captured = path;
                sources[path] = new DevMarkupFileSource(
                    () => stamps[captured],
                    () => File.ReadAllText(captured));
            }

            using var watcher = new DslDevFileWatcher(root, sources, interpreter, channel,
                pollInterval: TimeSpan.Zero, debounce: TimeSpan.Zero);
            watcher.Poll();
            await Assert.That(channel.Read().Count).IsEqualTo(0);

            File.WriteAllText(sharedPath, SharedText.Replace("Shared header", "Shared changed", StringComparison.Ordinal));
            stamps[sharedKey] = stamps[sharedKey].AddSeconds(2);
            watcher.Poll(); // detect
            watcher.Poll(); // apply

            await Assert.That(interpreter.TextOf(4)).IsEqualTo("Shared changed");
            var messages = channel.Read(10).Select(entry => $"[{entry.Source}] {entry.Message}").ToArray();
            await Assert.That(messages.Any(message => message.Contains("[watcher] change detected"))).IsTrue();
            await Assert.That(messages.Any(message => message.Contains("[interpreter] Patched"))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task DevCycle_IsRejectedWithoutThrowing()
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a.harbor-ui"] = "import \"b.harbor-ui\"\ntext \"a\"\n",
            ["b.harbor-ui"] = "import \"a.harbor-ui\"\ntext \"b\"\n",
        };

        await Assert.That(DevReloadInterpreter.TryCreateFromGraph(
            texts, "a.harbor-ui", out _, out var error)).IsFalse();
        await Assert.That(error).Contains("Cyclic import");
    }

    private static Dictionary<string, string> ReadGraph(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(directory, "*.harbor-ui"))
            result[MarkupPaths.Normalize(path)] = File.ReadAllText(path);
        return result;
    }

    private static MarkupModuleSource[] Sources(params (string Path, string Text)[] files) =>
        files.Select(file => new MarkupModuleSource(file.Path, file.Text)).ToArray();

    private static GeneratorDriverRunResult Run(params (string Path, string Text)[] files)
    {
        var additional = new List<AdditionalText>();
        var options = new TestOptionsProvider();
        foreach (var file in files)
        {
            var text = new TestAdditionalText("/checkout/" + file.Path, file.Text);
            additional.Add(text);
            options.With(text, file.Path);
        }

        var driver = CSharpGeneratorDriver.Create(
            generators: [new MarkupGenerator().AsSourceGenerator()],
            additionalTexts: additional.ToImmutableArray(),
            optionsProvider: options);
        return driver.RunGenerators(CSharpCompilation.Create("ModuleGraphWiring")).GetRunResult();
    }

    private static ImmutableArray<string> Generated(GeneratorDriverRunResult run, string hintSuffix) =>
        run.Results.SelectMany(result => result.GeneratedSources)
            .Where(source => source.HintName.EndsWith(hintSuffix, StringComparison.Ordinal)
                             || source.HintName == hintSuffix)
            .Select(source => source.SourceText.ToString())
            .ToImmutableArray();

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

    private sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly Dictionary<AdditionalText, AnalyzerConfigOptions> _perFile = [];

        public override AnalyzerConfigOptions GlobalOptions { get; } =
            new TestOptions(new Dictionary<string, string>());

        internal TestOptionsProvider With(AdditionalText file, string targetPath)
        {
            _perFile[file] = new TestOptions(new Dictionary<string, string>
            {
                ["build_metadata.AdditionalFiles.TargetPath"] =
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(targetPath)),
            });
            return this;
        }

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            _perFile.TryGetValue(textFile, out var options) ? options : GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

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
}
