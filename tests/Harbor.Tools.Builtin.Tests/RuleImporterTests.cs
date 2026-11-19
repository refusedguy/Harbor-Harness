using CSharpFunctionalExtensions;
using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #2680 F149. <see cref="RuleImporter" /> is a pure conversion leaf. These
///     tests pin its observable output without touching a file system, an
///     environment variable or a clock.
/// </summary>
public class RuleImporterTests
{
    [Test]
    public async Task DetectFormat_ClassifiesKnownFileNames()
    {
        (string? FileName, LegacyRuleFormat Expected)[] cases =
        [
            (".cursorrules", LegacyRuleFormat.CursorRules),
            (".CURSORRULES", LegacyRuleFormat.CursorRules),
            ("/repo/.cursorrules", LegacyRuleFormat.CursorRules),
            (".roorules", LegacyRuleFormat.RooRules),
            (".roorules.json", LegacyRuleFormat.RooRules),
            (".kilocoderules", LegacyRuleFormat.KiloCodeRules),
            ("AGENTS.md", LegacyRuleFormat.Unknown),
            ("", LegacyRuleFormat.Unknown),
            (null, LegacyRuleFormat.Unknown),
        ];

        foreach ((string? fileName, LegacyRuleFormat expected) in cases)
        {
            await Assert.That(RuleImporter.DetectFormat(fileName)).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Import_RejectsBlankFileName()
    {
        Result<ImportedRuleSection> result = RuleImporter.Import(new LegacyRuleFile("   ", "rule"));
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("file name is required");
    }

    [Test]
    public async Task Import_RejectsUnknownFileName()
    {
        Result<ImportedRuleSection> result = RuleImporter.Import(new LegacyRuleFile("README.md", "rule"));
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not a recognised legacy rule file");
    }

    [Test]
    public async Task Import_RejectsEmptyAndWhitespaceContent()
    {
        await Assert.That(RuleImporter.Import(new LegacyRuleFile(".cursorrules", "")).IsFailure).IsTrue();
        await Assert.That(RuleImporter.Import(new LegacyRuleFile(".cursorrules", "\uFEFF\n \t\r\n")).IsFailure).IsTrue();
    }

    [Test]
    public async Task Import_RejectsOversizedContent()
    {
        string huge = new('x', RuleImporter.MaxContentChars + 1);
        Result<ImportedRuleSection> result = RuleImporter.Import(new LegacyRuleFile(".cursorrules", huge));
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("migration limit");
    }

    [Test]
    public async Task Import_AcceptsContentAtTheHardCap()
    {
        string exact = new('x', RuleImporter.MaxContentChars);
        Result<ImportedRuleSection> result = RuleImporter.Import(new LegacyRuleFile(".cursorrules", exact));
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Markdown).Contains(exact);
    }

    [Test]
    public async Task Import_NormalisesBomAndLineEndingsAndPreservesBody()
    {
        Result<ImportedRuleSection> result = RuleImporter.Import(
            new LegacyRuleFile("/repo/.cursorrules", "\uFEFFUse LF\r\nAlways UTF-8\rNext\r\n"));

        await Assert.That(result.IsSuccess).IsTrue();
        ImportedRuleSection section = result.Value;
        await Assert.That(section.SourceFileName).IsEqualTo(".cursorrules");
        await Assert.That(section.Format).IsEqualTo(LegacyRuleFormat.CursorRules);
        await Assert.That(section.Markdown).Contains("Use LF\nAlways UTF-8\nNext");
        await Assert.That(section.Markdown).DoesNotContain("\r");
        await Assert.That(section.Markdown).DoesNotContain("\uFEFF");
    }

    [Test]
    public async Task Import_WrapsBodyInStableMarkersAndEndsWithNewline()
    {
        Result<ImportedRuleSection> result = RuleImporter.Import(new LegacyRuleFile(".kilocoderules", "Be terse."));
        string markdown = result.Value.Markdown;

        await Assert.That(markdown.StartsWith("<!-- harbor:imported-rules:.kilocoderules -->\n")).IsTrue();
        await Assert.That(markdown).Contains("## Project rules (migrated from `.kilocoderules`)");
        await Assert.That(markdown.TrimEnd().EndsWith("<!-- /harbor:imported-rules -->")).IsTrue();
        await Assert.That(markdown.EndsWith("\n")).IsTrue();
    }

    [Test]
    public async Task ImportMany_PreservesOrderAndDropsDuplicateNames()
    {
        Result<IReadOnlyList<ImportedRuleSection>> result = RuleImporter.ImportMany(
        [
            new LegacyRuleFile(".cursorrules", "first"),
            new LegacyRuleFile(".CURSORRULES", "duplicate"),
            new LegacyRuleFile(".roorules", "second"),
        ]);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(2);
        await Assert.That(result.Value[0].SourceFileName).IsEqualTo(".CURSORRULES");
        await Assert.That(result.Value[0].Markdown).Contains("first");
        await Assert.That(result.Value[1].Format).IsEqualTo(LegacyRuleFormat.RooRules);
        await Assert.That(result.Value[1].Markdown).Contains("second");
    }

    [Test]
    public async Task ImportMany_RejectsEmptyListAsSuccess()
    {
        Result<IReadOnlyList<ImportedRuleSection>> result = RuleImporter.ImportMany([]);
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ImportMany_SurfacesFirstConversionError()
    {
        Result<IReadOnlyList<ImportedRuleSection>> result = RuleImporter.ImportMany(
        [
            new LegacyRuleFile(".cursorrules", "ok"),
            new LegacyRuleFile("unknown.txt", "bad"),
        ]);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not a recognised legacy rule file");
    }

    [Test]
    public async Task ToAgentsMarkdown_IsEmptyForNoSections()
    {
        await Assert.That(RuleImporter.ToAgentsMarkdown(null)).IsEqualTo("");
        await Assert.That(RuleImporter.ToAgentsMarkdown([])).IsEqualTo("");
    }

    [Test]
    public async Task ToAgentsMarkdown_JoinsSectionsDeterministically()
    {
        Result<ImportedRuleSection> first = RuleImporter.Import(new LegacyRuleFile(".cursorrules", "A"));
        Result<ImportedRuleSection> second = RuleImporter.Import(new LegacyRuleFile(".roorules", "B"));
        IReadOnlyList<ImportedRuleSection> sections = [first.Value, second.Value];

        string markdown = RuleImporter.ToAgentsMarkdown(sections);
        await Assert.That(markdown).IsEqualTo(RuleImporter.ToAgentsMarkdown(sections));
        await Assert.That(markdown.StartsWith("\n<!-- harbor:imported-rules:start -->\n")).IsTrue();
        await Assert.That(markdown.TrimEnd().EndsWith("<!-- harbor:imported-rules:end -->")).IsTrue();
        await Assert.That(markdown.IndexOf("A", StringComparison.Ordinal)).IsLessThan(markdown.IndexOf("B", StringComparison.Ordinal));
    }
}
