using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;
// #436: the tokenizer trio speaks the Rendering style vocabulary; the engine
// namespace import below would otherwise resolve the expected values to the
// engine's verbatim ports.
using UIR = Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// CF-E-013: cell-grid port of the Avalonia <c>CodeBlock</c> tokenizer —
/// keyword sets for all 6 language groups, strings/comments/numbers,
/// shebang quirk, empty input, and the fence-body overlay.
/// </summary>
public class CodeTokenizerTests
{
    private static async Task AssertFirstKeyword(string code, string language, string keyword)
    {
        var spans = CodeTokenizer.Tokenize(code, language);
        await Assert.That(spans.Count).IsGreaterThan(0);
        await Assert.That(spans[0].Text).IsEqualTo(keyword);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.KeywordStyle);
    }

    // #1770: the language groups used to be one copy-pasted method each. The
    // assert never changed — only the (code, language, keyword) triple did.
    // Every row keeps its original test name as the case display name.
    [Test]
    [Arguments("class Foo {}", "csharp", "class", DisplayName = "CSharp_Keyword_Highlighted")]
    [Arguments("namespace Harbor {}", "cs", "namespace", DisplayName = "CSharp_Alias_Cs_Highlighted")]
    [Arguments("function f() {}", "js", "function", DisplayName = "Js_Keyword_Highlighted")]
    [Arguments("interface I {}", "typescript", "interface", DisplayName = "Ts_Alias_Keyword_Highlighted")]
    [Arguments("def f():", "python", "def", DisplayName = "Python_Keyword_Highlighted")]
    [Arguments("None", "py", "None", DisplayName = "Python_Alias_Py_Highlighted")]
    [Arguments("func main() {}", "go", "func", DisplayName = "Go_Keyword_Highlighted")]
    [Arguments("fn main() {}", "rust", "fn", DisplayName = "Rust_Keyword_Highlighted")]
    [Arguments("let x = 1;", "rs", "let", DisplayName = "Rust_Alias_Rs_Highlighted")]
    [Arguments("SELECT a FROM t", "sql", "SELECT", DisplayName = "Sql_Keyword_Highlighted")]
    [Arguments("select a from t", "sql", "select", DisplayName = "Sql_Lowercase_Keyword_Highlighted")]
    public async Task Keyword_IsHighlighted(string code, string language, string keyword)
    {
        await AssertFirstKeyword(code, language, keyword);
    }

    [Test]
    public async Task NonKeyword_Identifier_StaysPlain()
    {
        var spans = CodeTokenizer.Tokenize("foobar", "csharp");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Text).IsEqualTo("foobar");
        await Assert.That(spans[0].Style).IsEqualTo(CellStyle.Plain);
    }

    // #1770: the "one span, one style" families used to be one method per
    // input. One table per style keeps the expected style a literal in the
    // assert — a table per style is one behaviour, and the row says which
    // input it is.
    [Test]
    [Arguments("class x", "brainfuck", DisplayName = "UnknownLanguage_NoKeywords")]
    [Arguments("class x", null, DisplayName = "NullLanguage_NoKeywords")]
    [Arguments("abc123", "csharp", DisplayName = "DigitMidWord_StaysPlain")]
    public async Task SingleSpan_HasPlainStyle(string code, string? language)
    {
        var spans = CodeTokenizer.Tokenize(code, language);
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CellStyle.Plain);
    }

    [Test]
    [Arguments("'it'", "python", DisplayName = "SingleQuoted_String_Highlighted")]
    [Arguments("`tmpl`", "js", DisplayName = "Backtick_String_Highlighted")]
    public async Task SingleSpan_HasStringStyle(string code, string? language)
    {
        var spans = CodeTokenizer.Tokenize(code, language);
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.StringStyle);
    }

    [Test]
    [Arguments("// hello", "csharp", DisplayName = "SlashSlash_Comment_Highlighted")]
    [Arguments("# hello", "python", DisplayName = "Hash_Comment_Highlighted")]
    [Arguments("/* hi */", "go", DisplayName = "Block_Comment_Highlighted")]
    public async Task SingleSpan_HasCommentStyle(string code, string? language)
    {
        var spans = CodeTokenizer.Tokenize(code, language);
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.CommentStyle);
    }

    [Test]
    [Arguments("42", "csharp", DisplayName = "Integer_Highlighted")]
    [Arguments("3.14", "python", DisplayName = "Float_Highlighted")]
    [Arguments("0xFF", "rust", DisplayName = "Hex_Highlighted")]
    public async Task SingleSpan_HasNumberStyle(string code, string? language)
    {
        var spans = CodeTokenizer.Tokenize(code, language);
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.NumberStyle);
    }

    [Test]
    public async Task DoubleQuoted_String_Highlighted()
    {
        var spans = CodeTokenizer.Tokenize("var s = \"hello\";", "js");
        var str = spans[^2];
        await Assert.That(str.Text).IsEqualTo("\"hello\"");
        await Assert.That(str.Style).IsEqualTo(CodeTokenizer.StringStyle);
    }

    [Test]
    public async Task EscapedQuote_StaysOneStringSpan()
    {
        var spans = CodeTokenizer.Tokenize("\"a\\\"b\"", "csharp");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Text).IsEqualTo("\"a\\\"b\"");
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.StringStyle);
    }

    [Test]
    public async Task Unterminated_BlockComment_RunsToEnd()
    {
        var spans = CodeTokenizer.Tokenize("/* hi", "rust");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Text).IsEqualTo("/* hi");
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.CommentStyle);
    }

    [Test]
    public async Task BlockComment_Continues_OnNextLine()
    {
        bool inBlock = false;
        var first = CodeTokenizer.TokenizeLine("/* open".AsSpan(), "csharp", ref inBlock);
        await Assert.That(inBlock).IsTrue();
        await Assert.That(first.Count).IsEqualTo(1);
        await Assert.That(first[0].Style).IsEqualTo(CodeTokenizer.CommentStyle);

        var second = CodeTokenizer.TokenizeLine("still comment */ var x".AsSpan(), "csharp", ref inBlock);
        await Assert.That(inBlock).IsFalse();
        await Assert.That(second[0].Text).IsEqualTo("still comment */");
        await Assert.That(second[0].Style).IsEqualTo(CodeTokenizer.CommentStyle);
    }

    [Test]
    public async Task Shebang_LexesAsComment()
    {
        // Faithful-port quirk: upstream IsShebang() is a constant-false
        // stub, so '#' (including '#!') always starts a line comment.
        var spans = CodeTokenizer.Tokenize("#!/usr/bin/env python", "python");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Text).IsEqualTo("#!/usr/bin/env python");
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.CommentStyle);
    }

    [Test]
    public async Task EmptyInput_ReturnsNoSpans()
    {
        await Assert.That(CodeTokenizer.Tokenize(string.Empty, "csharp").Count).IsEqualTo(0);
        await Assert.That(CodeTokenizer.Tokenize(string.Empty, null).Count).IsEqualTo(0);
    }

    [Test]
    public async Task StyleMapping_MatchesPalette()
    {
        await Assert.That(CodeTokenizer.KeywordStyle).IsEqualTo(new UIR.CellStyle(ChatPalette.Accent, attrs: UIR.StyleAttr.Bold));
        await Assert.That(CodeTokenizer.StringStyle).IsEqualTo(new UIR.CellStyle(ChatPalette.Success));
        await Assert.That(CodeTokenizer.CommentStyle).IsEqualTo(new UIR.CellStyle(ChatPalette.Muted));
        await Assert.That(CodeTokenizer.NumberStyle).IsEqualTo(new UIR.CellStyle(ChatPalette.Warning));
    }

    [Test]
    public async Task HighlightFenceBodies_MarksBodyLinesOnly()
    {
        var lines = new List<MdLine>
        {
            new([new MdSpan("```csharp", MdStyle.Fence)]),
            new([new MdSpan("class Foo {}", MdStyle.Normal)]),
            new([new MdSpan("```", MdStyle.Fence)]),
            new([new MdSpan("class after", MdStyle.Normal)]),
        };

        var map = CodeTokenizer.HighlightFenceBodies(lines);
        await Assert.That(map).IsNotNull();
        await Assert.That(map!.ContainsKey(1)).IsTrue();
        await Assert.That(map.ContainsKey(0)).IsFalse();
        await Assert.That(map.ContainsKey(2)).IsFalse();
        await Assert.That(map.ContainsKey(3)).IsFalse();
        await Assert.That(map[1][0].Text).IsEqualTo("class");
        await Assert.That(map[1][0].Style).IsEqualTo(CodeTokenizer.KeywordStyle);
    }

    [Test]
    public async Task HighlightFenceBodies_NoFences_ReturnsNull()
    {
        var lines = new List<MdLine>
        {
            new([new MdSpan("just text", MdStyle.Normal)]),
        };

        await Assert.That(CodeTokenizer.HighlightFenceBodies(lines)).IsNull();
    }

    [Test]
    public async Task HighlightFenceBodies_UnknownLanguage_ReturnsNull()
    {
        var lines = new List<MdLine>
        {
            new([new MdSpan("```brainfuck", MdStyle.Fence)]),
            new([new MdSpan("class Foo {}", MdStyle.Normal)]),
            new([new MdSpan("```", MdStyle.Fence)]),
        };

        await Assert.That(CodeTokenizer.HighlightFenceBodies(lines)).IsNull();
    }

    [Test]
    public async Task HighlightFenceBodies_UnclosedFence_HighlightsToEnd()
    {
        var lines = new List<MdLine>
        {
            new([new MdSpan("```go", MdStyle.Fence)]),
            new([new MdSpan("func main() {}", MdStyle.Normal)]),
        };

        var map = CodeTokenizer.HighlightFenceBodies(lines);
        await Assert.That(map).IsNotNull();
        await Assert.That(map!.ContainsKey(1)).IsTrue();
        await Assert.That(map[1][0].Text).IsEqualTo("func");
    }

    [Test]
    public async Task MarkdownBlock_PaintsKeywordStyle()
    {
        const string doc = "```csharp\nclass A {}\n```\n";
        var block = new AssistantMarkdownBlock(doc);
        var measure = block.Measure(40);
        var buffer = new ScreenBuffer(40, measure.MinLines);
        buffer.Fill(new Rect(0, 0, 40, measure.MinLines), Cell.Blank);
        block.Paint(new BlockPaintContext(buffer, new Rect(0, 0, 40, measure.MinLines), tick: 0));

        bool sawKeyword = false;
        for (int y = 0; y < buffer.Rows && !sawKeyword; y++)
        {
            for (int x = 0; x < buffer.Cols; x++)
            {
                if (buffer.Get(x, y).Style == CodeTokenizer.KeywordStyle)
                {
                    sawKeyword = true;
                    break;
                }
            }
        }

        await Assert.That(sawKeyword).IsTrue();
    }
}
