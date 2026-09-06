using Harbor.Tui.CellForge.Rendering;
using Harbor.Tui.CellForge.Widgets;

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

    [Test]
    public async Task CSharp_Keyword_Highlighted()
    {
        await AssertFirstKeyword("class Foo {}", "csharp", "class");
    }

    [Test]
    public async Task CSharp_Alias_Cs_Highlighted()
    {
        await AssertFirstKeyword("namespace Harbor {}", "cs", "namespace");
    }

    [Test]
    public async Task Js_Keyword_Highlighted()
    {
        await AssertFirstKeyword("function f() {}", "js", "function");
    }

    [Test]
    public async Task Ts_Alias_Keyword_Highlighted()
    {
        await AssertFirstKeyword("interface I {}", "typescript", "interface");
    }

    [Test]
    public async Task Python_Keyword_Highlighted()
    {
        await AssertFirstKeyword("def f():", "python", "def");
    }

    [Test]
    public async Task Python_Alias_Py_Highlighted()
    {
        await AssertFirstKeyword("None", "py", "None");
    }

    [Test]
    public async Task Go_Keyword_Highlighted()
    {
        await AssertFirstKeyword("func main() {}", "go", "func");
    }

    [Test]
    public async Task Rust_Keyword_Highlighted()
    {
        await AssertFirstKeyword("fn main() {}", "rust", "fn");
    }

    [Test]
    public async Task Rust_Alias_Rs_Highlighted()
    {
        await AssertFirstKeyword("let x = 1;", "rs", "let");
    }

    [Test]
    public async Task Sql_Keyword_Highlighted()
    {
        await AssertFirstKeyword("SELECT a FROM t", "sql", "SELECT");
    }

    [Test]
    public async Task Sql_Lowercase_Keyword_Highlighted()
    {
        await AssertFirstKeyword("select a from t", "sql", "select");
    }

    [Test]
    public async Task NonKeyword_Identifier_StaysPlain()
    {
        var spans = CodeTokenizer.Tokenize("foobar", "csharp");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Text).IsEqualTo("foobar");
        await Assert.That(spans[0].Style).IsEqualTo(CellStyle.Plain);
    }

    [Test]
    public async Task UnknownLanguage_NoKeywords()
    {
        var spans = CodeTokenizer.Tokenize("class x", "brainfuck");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CellStyle.Plain);
    }

    [Test]
    public async Task NullLanguage_NoKeywords()
    {
        var spans = CodeTokenizer.Tokenize("class x", null);
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CellStyle.Plain);
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
    public async Task SingleQuoted_String_Highlighted()
    {
        var spans = CodeTokenizer.Tokenize("'it'", "python");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.StringStyle);
    }

    [Test]
    public async Task Backtick_String_Highlighted()
    {
        var spans = CodeTokenizer.Tokenize("`tmpl`", "js");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.StringStyle);
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
    public async Task SlashSlash_Comment_Highlighted()
    {
        var spans = CodeTokenizer.Tokenize("// hello", "csharp");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.CommentStyle);
    }

    [Test]
    public async Task Hash_Comment_Highlighted()
    {
        var spans = CodeTokenizer.Tokenize("# hello", "python");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.CommentStyle);
    }

    [Test]
    public async Task Block_Comment_Highlighted()
    {
        var spans = CodeTokenizer.Tokenize("/* hi */", "go");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.CommentStyle);
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
    public async Task Integer_Highlighted()
    {
        var spans = CodeTokenizer.Tokenize("42", "csharp");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.NumberStyle);
    }

    [Test]
    public async Task Float_Highlighted()
    {
        var spans = CodeTokenizer.Tokenize("3.14", "python");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.NumberStyle);
    }

    [Test]
    public async Task Hex_Highlighted()
    {
        var spans = CodeTokenizer.Tokenize("0xFF", "rust");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CodeTokenizer.NumberStyle);
    }

    [Test]
    public async Task DigitMidWord_StaysPlain()
    {
        var spans = CodeTokenizer.Tokenize("abc123", "csharp");
        await Assert.That(spans.Count).IsEqualTo(1);
        await Assert.That(spans[0].Style).IsEqualTo(CellStyle.Plain);
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
        await Assert.That(CodeTokenizer.KeywordStyle).IsEqualTo(new CellStyle(ChatPalette.Accent, attrs: StyleAttr.Bold));
        await Assert.That(CodeTokenizer.StringStyle).IsEqualTo(new CellStyle(ChatPalette.Success));
        await Assert.That(CodeTokenizer.CommentStyle).IsEqualTo(new CellStyle(ChatPalette.Muted));
        await Assert.That(CodeTokenizer.NumberStyle).IsEqualTo(new CellStyle(ChatPalette.Warning));
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
