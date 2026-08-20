using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
namespace Harbor.App.Avalonia.Views.Controls;
/// <summary>
///     Fenced code-block card with language label + copy button + basic
///     syntax highlighting — ORCA feature steal #3.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why not AvaloniaEdit?</b> Avalonia.AvaloniaEdit 12.0 is
///         already a package reference and powers the CodeEditorView — but
///         embedding a full <c>TextEditor</c> per chat message is too
///         heavy (each instance builds its own textarea + caret + search
///         panel). For chat code blocks we want a lightweight, read-only
///         TextBlock with simple token coloring. The syntax pass below is
///         a deliberately small tokenizer (keywords, strings, comments,
///         numbers) covering C#/JS/Python/Go/Rust/SQL — the 80% case for
///         chat code.
///     </para>
///     <para>
///         <b>Streaming-friendly:</b> setting <see cref="Code" /> rebuilds
///         the <see cref="TextBlock.Inlines" /> collection synchronously —
///         safe to call from the UI thread on every token.
///     </para>
/// </remarks>
public sealed partial class CodeBlock : UserControl
{
    /// <summary>Styled property for the raw code text.</summary>
    public static readonly StyledProperty<string> CodeProperty =
        AvaloniaProperty.Register<CodeBlock, string>(nameof(Code), string.Empty);

    /// <summary>Styled property for the language identifier (e.g. "csharp", "js").</summary>
    public static readonly StyledProperty<string> LanguageProperty =
        AvaloniaProperty.Register<CodeBlock, string>(nameof(Language), string.Empty);

    /// <summary>Construct the code block.</summary>
    public CodeBlock()
    {
        InitializeComponent();
        this.PropertyChanged += OnPropertyChangedHandler;
    }

    /// <summary>The raw code text to render.</summary>
    public string Code
    {
        get => this.GetValue(CodeProperty);
        set => this.SetValue(CodeProperty, value);
    }

    /// <summary>The language identifier (used to pick the keyword set).</summary>
    public string Language
    {
        get => this.GetValue(LanguageProperty);
        set => this.SetValue(LanguageProperty, value);
    }

    private void OnPropertyChangedHandler(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == CodeProperty || e.Property == LanguageProperty)
        {
            RenderCode();
        }
    }

    /// <summary>Re-tokenize and re-render the code body.</summary>
    public void RenderCode()
    {
        if (CodeText is null)
        {
            return;
        }
        CodeText.Inlines?.Clear();
        string src = Code ?? string.Empty;
        if (src.Length == 0)
        {
            return;
        }
        var inlines = Tokenize(src, Language ?? string.Empty);
        foreach (var run in inlines)
        {
            CodeText.Inlines?.Add(run);
        }
    }

    private static IEnumerable<Inline> Tokenize(string code, string language)
    {
        var keywords = KeywordsFor(language);
        var keywordBrush = TryFindBrush("AccentPrimaryBrush", Brushes.Orchid);
        var stringBrush = TryFindBrush("StateSuccessBrush", Brushes.LightGreen);
        var commentBrush = TryFindBrush("TextTertiaryBrush", Brushes.Gray);
        var numberBrush = TryFindBrush("StateWarningBrush", Brushes.Orange);
        var defaultBrush = TryFindBrush("TextPrimaryBrush", Brushes.White);
        var codeFont = TryFindFont();

        int i = 0;
        int n = code.Length;
        var current = new StringBuilder();

        // pendingEmit is a side-channel for the local function FlushDefault
        // (local functions can't yield return — they push into this list
        // and we drain it after each flush). Declared before the local
        // function so the closure binding is valid.
        var pendingEmit = new List<Inline>(1);

        void FlushDefault()
        {
            if (current.Length == 0) return;
            var run = new Run(current.ToString()) { Foreground = defaultBrush, FontFamily = codeFont };
            current.Clear();
            pendingEmit.Add(run);
        }

        while (i < n)
        {
            char c = code[i];

            // Line comment: // or #
            if (c == '/' && i + 1 < n && code[i + 1] == '/' || c == '#' && !IsShebang(code, i))
            {
                FlushDefault();
                foreach (var pending in pendingEmit) yield return pending;
                pendingEmit.Clear();
                int start = i;
                while (i < n && code[i] != '\n' && code[i] != '\r')
                {
                    i++;
                }
                yield return new Run(code.Substring(start, i - start)) { Foreground = commentBrush, FontFamily = codeFont };
                continue;
            }

            // Block comment: /* ... */
            if (c == '/' && i + 1 < n && code[i + 1] == '*')
            {
                FlushDefault();
                foreach (var pending in pendingEmit) yield return pending;
                pendingEmit.Clear();
                int start = i;
                i += 2;
                while (i + 1 < n && !(code[i] == '*' && code[i + 1] == '/'))
                {
                    i++;
                }
                i = Math.Min(i + 2, n);
                yield return new Run(code.Substring(start, i - start)) { Foreground = commentBrush, FontFamily = codeFont };
                continue;
            }

            // String literal: " ... " or ' ... ' or ` ... `
            if (c == '"' || c == '\'' || c == '`')
            {
                FlushDefault();
                foreach (var pending in pendingEmit) yield return pending;
                pendingEmit.Clear();
                char quote = c;
                int start = i;
                i++;
                while (i < n && code[i] != quote)
                {
                    if (code[i] == '\\' && i + 1 < n) i++;
                    i++;
                }
                if (i < n) i++; // include closing quote
                yield return new Run(code.Substring(start, i - start)) { Foreground = stringBrush, FontFamily = codeFont };
                continue;
            }

            // Number literal
            if (char.IsDigit(c) && current.Length == 0)
            {
                FlushDefault();
                foreach (var pending in pendingEmit) yield return pending;
                pendingEmit.Clear();
                int start = i;
                while (i < n && (char.IsDigit(code[i]) || code[i] == '.' || code[i] == 'x' || code[i] == 'X'
                                 || code[i] >= 'a' && code[i] <= 'f' || code[i] >= 'A' && code[i] <= 'F'))
                {
                    i++;
                }
                yield return new Run(code.Substring(start, i - start)) { Foreground = numberBrush, FontFamily = codeFont };
                continue;
            }

            // Identifier / keyword
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
                {
                    i++;
                }
                string word = code.Substring(start, i - start);
                if (keywords.Contains(word))
                {
                    FlushDefault();
                    foreach (var pending in pendingEmit) yield return pending;
                    pendingEmit.Clear();
                    yield return new Run(word) { Foreground = keywordBrush, FontWeight = FontWeight.SemiBold, FontFamily = codeFont };
                }
                else
                {
                    current.Append(word);
                }
                continue;
            }

            // Default accumulation
            current.Append(c);
            i++;
        }

        FlushDefault();
        foreach (var pending in pendingEmit) yield return pending;
        pendingEmit.Clear();
    }

    private static bool IsShebang(string code, int i)
    {
        // #!/usr/bin/... — treat as comment, not shebang, but only at line start.
        if (i == 0) return false;
        if (code[i - 1] == '\n' || code[i - 1] == '\r') return false;
        return false;
    }

    private static HashSet<string> KeywordsFor(string language) => language.ToLowerInvariant() switch
    {
        "cs" or "csharp" or "c#" => new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
            "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "var", "virtual", "void", "volatile", "while", "async", "await", "yield", "record", "partial"
        },
        "js" or "javascript" or "ts" or "typescript" => new HashSet<string>(StringComparer.Ordinal)
        {
            "var", "let", "const", "function", "return", "if", "else", "for", "while", "do", "switch", "case", "break", "continue", "new", "this", "typeof", "instanceof", "in", "of", "class", "extends", "super", "import", "export", "from", "default", "try", "catch", "finally", "throw", "async", "await", "yield", "delete", "void", "null", "undefined",
            "true", "false", "interface", "type", "enum", "public", "private", "protected", "readonly", "static", "get", "set", "implements", "namespace", "as", "is", "satisfies"
        },
        "py" or "python" => new HashSet<string>(StringComparer.Ordinal)
        {
            "def", "return", "if", "elif", "else", "for", "while", "break", "continue", "in", "not", "and", "or", "is", "None", "True", "False", "class", "import", "from", "as", "try", "except", "finally", "raise", "with", "lambda", "yield", "global", "nonlocal", "pass", "assert", "del", "print", "self", "cls", "async", "await"
        },
        "go" or "golang" => new HashSet<string>(StringComparer.Ordinal)
        {
            "func", "return", "if", "else", "for", "range", "switch", "case", "default", "break", "continue", "package", "import", "type", "struct", "interface", "var", "const", "go", "defer", "select", "chan", "map", "nil", "true", "false", "make", "new", "len", "cap", "append"
        },
        "rust" or "rs" => new HashSet<string>(StringComparer.Ordinal)
        {
            "fn", "let", "mut", "const", "static", "if", "else", "for", "while", "loop", "match", "return", "break", "continue", "struct", "enum", "trait", "impl", "pub", "use", "mod", "as", "in", "ref", "move", "async", "await", "self", "Self", "super", "crate", "where", "dyn", "unsafe", "extern", "type", "true", "false"
        },
        "sql" => new HashSet<string>(StringComparer.Ordinal)
        {
            "SELECT", "select", "FROM", "from", "WHERE", "where", "INSERT", "insert", "UPDATE", "update", "DELETE", "delete", "CREATE", "create", "TABLE", "table", "INDEX", "index", "DROP", "drop", "ALTER", "alter", "INTO", "into", "VALUES", "values", "SET", "set", "JOIN", "join", "INNER", "inner", "LEFT", "left", "RIGHT", "right", "OUTER", "outer",
            "ON", "on", "GROUP", "group", "BY", "by", "ORDER", "order", "HAVING", "having", "LIMIT", "limit", "OFFSET", "offset", "AS", "as", "AND", "and", "OR", "or", "NOT", "not", "NULL", "null", "PRIMARY", "primary", "KEY", "key", "FOREIGN", "foreign", "REFERENCES", "references", "UNIQUE", "unique", "DEFAULT", "default", "CASCADE", "cascade"
        },
        _ => new HashSet<string>(StringComparer.Ordinal)
    };

    private static IBrush TryFindBrush(string key, IBrush fallback)
    {
        if (global::Avalonia.Application.Current?.Resources.TryGetResource(key, null, out object? r) == true && r is IBrush b)
        {
            return b;
        }
        return fallback;
    }

    private static FontFamily TryFindFont()
    {
        if (global::Avalonia.Application.Current?.Resources.TryGetResource("FontMono", null, out object? r) == true && r is FontFamily f)
        {
            return f;
        }
        return FontFamily.Default;
    }

    private async void CopyButton_Click(object? sender, RoutedEventArgs e)
    {
        string text = Code ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            // Avalonia 12.1 changed the clipboard API: there's no
            // IClipboard.SetTextAsync(string) any more. Instead you build
            // a DataTransfer → DataTransferItem, call SetText on the item,
            // then call IClipboard.SetDataAsync(transfer). Wrap in try/catch
            // because the clipboard is unavailable in headless tests.
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }
            var transfer = new DataTransfer();
            var item = new DataTransferItem();
            item.SetText(text);
            transfer.Add(item);
            await clipboard.SetDataAsync(transfer).ConfigureAwait(true);
        }
        catch
        {
            // Clipboard may be unavailable in headless test environments —
            // silently ignore rather than crash the click handler.
        }
    }
}
