namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
/// Builtin <see cref="ILanguageSupport"/> singletons (issue #198): the six
/// language groups of the old <c>CodeTokenizer.IsKeyword</c> switch ladder,
/// moved 1:1 into one class each. Keyword tables are verbatim copies —
/// highlighting behavior is unchanged (golden tests pin it).
/// </summary>
public sealed class CSharpLanguageSupport : LanguageSupportBase
{
    /// <summary>Shared singleton (stateless).</summary>
    public static readonly CSharpLanguageSupport Instance = new();

    /// <inheritdoc />
    public override string LanguageId => "csharp";

    private CSharpLanguageSupport()
        : base(["cs", "c#"],
        [
            "abstract", "as", "base", "bool", "break",
            "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default",
            "delegate", "do", "double", "else", "enum",
            "event", "explicit", "extern", "false", "finally",
            "fixed", "float", "for", "foreach", "goto",
            "if", "implicit", "in", "int", "interface",
            "internal", "is", "lock", "long", "namespace",
            "new", "null", "object", "operator", "out",
            "override", "params", "private", "protected", "public",
            "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string",
            "struct", "switch", "this", "throw", "true",
            "try", "typeof", "uint", "ulong", "unchecked",
            "unsafe", "ushort", "using", "var", "virtual",
            "void", "volatile", "while", "async", "await",
            "yield", "record", "partial",
        ])
    {
    }
}

/// <summary>JavaScript + TypeScript keyword group (fence tags: js, javascript, ts, typescript).</summary>
public sealed class JsLanguageSupport : LanguageSupportBase
{
    /// <summary>Shared singleton (stateless).</summary>
    public static readonly JsLanguageSupport Instance = new();

    /// <inheritdoc />
    public override string LanguageId => "js";

    private JsLanguageSupport()
        : base(["javascript", "ts", "typescript"],
        [
            "var", "let", "const", "function", "return",
            "if", "else", "for", "while", "do",
            "switch", "case", "break", "continue", "new",
            "this", "typeof", "instanceof", "in", "of",
            "class", "extends", "super", "import", "export",
            "from", "default", "try", "catch", "finally",
            "throw", "async", "await", "yield", "delete",
            "void", "null", "undefined", "true", "false",
            "interface", "type", "enum", "public", "private",
            "protected", "readonly", "static", "get", "set",
            "implements", "namespace", "as", "is", "satisfies",
        ])
    {
    }
}

/// <summary>Python keyword group (fence tags: python, py).</summary>
public sealed class PythonLanguageSupport : LanguageSupportBase
{
    /// <summary>Shared singleton (stateless).</summary>
    public static readonly PythonLanguageSupport Instance = new();

    /// <inheritdoc />
    public override string LanguageId => "python";

    private PythonLanguageSupport()
        : base(["py"],
        [
            "def", "return", "if", "elif", "else",
            "for", "while", "break", "continue", "in",
            "not", "and", "or", "is", "None",
            "True", "False", "class", "import", "from",
            "as", "try", "except", "finally", "raise",
            "with", "lambda", "yield", "global", "nonlocal",
            "pass", "assert", "del", "print", "self",
            "cls", "async", "await",
        ])
    {
    }
}

/// <summary>Go keyword group (fence tags: go, golang).</summary>
public sealed class GoLanguageSupport : LanguageSupportBase
{
    /// <summary>Shared singleton (stateless).</summary>
    public static readonly GoLanguageSupport Instance = new();

    /// <inheritdoc />
    public override string LanguageId => "go";

    private GoLanguageSupport()
        : base(["golang"],
        [
            "func", "return", "if", "else", "for",
            "range", "switch", "case", "default", "break",
            "continue", "package", "import", "type", "struct",
            "interface", "var", "const", "go", "defer",
            "select", "chan", "map", "nil", "true",
            "false", "make", "new", "len", "cap",
            "append",
        ])
    {
    }
}

/// <summary>Rust keyword group (fence tags: rust, rs).</summary>
public sealed class RustLanguageSupport : LanguageSupportBase
{
    /// <summary>Shared singleton (stateless).</summary>
    public static readonly RustLanguageSupport Instance = new();

    /// <inheritdoc />
    public override string LanguageId => "rust";

    private RustLanguageSupport()
        : base(["rs"],
        [
            "fn", "let", "mut", "const", "static",
            "if", "else", "for", "while", "loop",
            "match", "return", "break", "continue", "struct",
            "enum", "trait", "impl", "pub", "use",
            "mod", "as", "in", "ref", "move",
            "async", "await", "self", "Self", "super",
            "crate", "where", "dyn", "unsafe", "extern",
            "type", "true", "false",
        ])
    {
    }
}

/// <summary>
/// SQL keyword group (fence tag: sql). Lists both cases per word with an
/// ordinal comparer — the Avalonia source quirk where <c>"Select"</c> is NOT
/// a keyword is preserved 1:1.
/// </summary>
public sealed class SqlLanguageSupport : LanguageSupportBase
{
    /// <summary>Shared singleton (stateless).</summary>
    public static readonly SqlLanguageSupport Instance = new();

    /// <inheritdoc />
    public override string LanguageId => "sql";

    private SqlLanguageSupport()
        : base([],
        [
            "SELECT", "select", "FROM", "from", "WHERE", "where",
            "INSERT", "insert", "UPDATE", "update", "DELETE", "delete",
            "CREATE", "create", "TABLE", "table", "INDEX", "index",
            "DROP", "drop", "ALTER", "alter", "INTO", "into",
            "VALUES", "values", "SET", "set", "JOIN", "join",
            "INNER", "inner", "LEFT", "left", "RIGHT", "right",
            "OUTER", "outer", "ON", "on", "GROUP", "group",
            "BY", "by", "ORDER", "order", "HAVING", "having",
            "LIMIT", "limit", "OFFSET", "offset", "AS", "as",
            "AND", "and", "OR", "or", "NOT", "not",
            "NULL", "null", "PRIMARY", "primary", "KEY", "key",
            "FOREIGN", "foreign", "REFERENCES", "references", "UNIQUE", "unique",
            "DEFAULT", "default", "CASCADE", "cascade",
        ])
    {
    }
}
