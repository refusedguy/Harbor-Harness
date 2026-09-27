using System.Collections.Frozen;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Issue #198: <see cref="LanguageSupportRegistry"/> resolution,
/// <see cref="CodeHighlightPalette"/> injection, and instance/static parity.
/// Golden highlighting behavior itself stays pinned in
/// <c>CodeTokenizerTests</c> (untouched).
/// </summary>
public class LanguageSupportRegistryTests
{
    private sealed class ToyLanguageSupport : ILanguageSupport
    {
        public static readonly ToyLanguageSupport Instance = new();

        public string LanguageId => "toylang";

        public IReadOnlyList<string> Aliases => ["toy"];

        public IReadOnlySet<string> Keywords => _keywords;

        private static readonly FrozenSet<string> _keywords =
            new[] { "wobble" }.ToFrozenSet(StringComparer.Ordinal);

        public bool IsKeyword(ReadOnlySpan<char> word) => _keywords.Contains(word.ToString());
    }

    [Test]
    public async Task Resolve_CanonicalAndAliases()
    {
        var registry = LanguageSupportRegistry.Default;
        await Assert.That(registry.Resolve("csharp")).IsEqualTo(CSharpLanguageSupport.Instance);
        await Assert.That(registry.Resolve("cs")).IsEqualTo(CSharpLanguageSupport.Instance);
        await Assert.That(registry.Resolve("c#")).IsEqualTo(CSharpLanguageSupport.Instance);
        await Assert.That(registry.Resolve("typescript")).IsEqualTo(JsLanguageSupport.Instance);
        await Assert.That(registry.Resolve("py")).IsEqualTo(PythonLanguageSupport.Instance);
        await Assert.That(registry.Resolve("golang")).IsEqualTo(GoLanguageSupport.Instance);
        await Assert.That(registry.Resolve("rs")).IsEqualTo(RustLanguageSupport.Instance);
        await Assert.That(registry.Resolve("sql")).IsEqualTo(SqlLanguageSupport.Instance);
    }

    [Test]
    public async Task Resolve_CaseInsensitive_UnknownOrBlank_Null()
    {
        var registry = LanguageSupportRegistry.Default;
        await Assert.That(registry.Resolve("CSharp")).IsEqualTo(CSharpLanguageSupport.Instance);
        await Assert.That(registry.Resolve("  go  ")).IsEqualTo(GoLanguageSupport.Instance);
        await Assert.That(registry.Resolve("brainfuck")).IsNull();
        await Assert.That(registry.Resolve((string?)null)).IsNull();
        await Assert.That(registry.Resolve(string.Empty)).IsNull();
        await Assert.That(registry.Resolve("   ")).IsNull();
    }

    [Test]
    public async Task Keywords_SqlCaseQuirk_Preserved()
    {
        var sql = SqlLanguageSupport.Instance;
        await Assert.That(sql.IsKeyword("SELECT")).IsTrue();
        await Assert.That(sql.IsKeyword("select")).IsTrue();
        await Assert.That(sql.IsKeyword("Select")).IsFalse();
        await Assert.That(CSharpLanguageSupport.Instance.IsKeyword("class")).IsTrue();
        await Assert.That(CSharpLanguageSupport.Instance.IsKeyword("foobar")).IsFalse();
    }

    [Test]
    public async Task CustomLanguage_Highlights_WithoutTouchingTokenizer()
    {
        var registry = new LanguageSupportRegistry([ToyLanguageSupport.Instance]);
        var tokenizer = new CodeSyntaxTokenizer(registry: registry);

        var spans = tokenizer.Tokenize("wobble x", "toy");
        await Assert.That(spans.Count).IsGreaterThan(0);
        await Assert.That(spans[0].Text).IsEqualTo("wobble");
        await Assert.That(spans[0].Style).IsEqualTo(tokenizer.KeywordStyle);

        // Unknown to the custom registry: no keywords, all plain.
        var plain = tokenizer.Tokenize("wobble x", "csharp");
        foreach (var span in plain)
        {
            await Assert.That(span.Style).IsEqualTo(CellStyle.Plain);
        }
    }

    [Test]
    public async Task InjectedPalette_OverridesStyles()
    {
        var palette = new CodeHighlightPalette(
            new CellStyle(PackedColor.Rgb(1, 2, 3), attrs: StyleAttr.Bold),
            new CellStyle(PackedColor.Rgb(4, 5, 6)),
            new CellStyle(PackedColor.Rgb(7, 8, 9)),
            new CellStyle(PackedColor.Rgb(10, 11, 12)));
        var tokenizer = new CodeSyntaxTokenizer(palette);

        var spans = tokenizer.Tokenize("class Foo {}", "csharp");
        await Assert.That(spans[0].Style).IsEqualTo(palette.Keyword);

        var strings = tokenizer.Tokenize("\"hi\"", "js");
        await Assert.That(strings[0].Style).IsEqualTo(palette.String);

        var comments = tokenizer.Tokenize("// hi", "csharp");
        await Assert.That(comments[0].Style).IsEqualTo(palette.Comment);

        var numbers = tokenizer.Tokenize("42", "csharp");
        await Assert.That(numbers[0].Style).IsEqualTo(palette.Number);
    }

    [Test]
    public async Task InstanceDefault_Matches_StaticFacade()
    {
        const string code = "class Foo // hi\nvar s = \"x\";";
        var expected = CodeTokenizer.Tokenize(code, "csharp");
        var actual = new CodeSyntaxTokenizer().Tokenize(code, "csharp");

        await Assert.That(actual.Count).IsEqualTo(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            await Assert.That(actual[i].Text).IsEqualTo(expected[i].Text);
            await Assert.That(actual[i].Style).IsEqualTo(expected[i].Style);
        }
    }
}
