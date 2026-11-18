using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Harbor.Abstractions.Dsl;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>
///     The single grammar reader for <c>.harbor-ui</c> files. The compiler
///     generator and the dev interpreter both call this method, so they
///     cannot drift into two parsers.
/// </summary>
/// <remarks>
///     <para>
///         One line declares one node: a kind, optional literal arguments and
///         an optional trailing colon. Lines indented further are its children.
///         A line starting with a dot records an operation on the nearest
///         enclosing node. Text after a hash on its own line is a comment.
///     </para>
///     <para>
///         The reader depends only on the BCL and Harbor.Abstractions.Contracts.
///         It holds no Roslyn, CellForge runtime or renderer state, so the dev
///         interpreter can call it without a compiler and the generator can
///         call it without a UI assembly.
///     </para>
/// </remarks>
public static class MarkupParser
{
    private const string NewOperation = "new";
    private const string StateDirective = "state:";

    /// <summary>Parses markup text into a <see cref="SpecArena"/>.</summary>
    /// <param name="text">Whole file contents.</param>
    /// <returns>The nodes built so far and any syntax errors.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public static MarkupParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var arena = new SpecArena();
        var diagnostics = new List<MarkupDiagnostic>();
        var levels = new List<Level>();
        string? stateType = null;
        var stateTypeLine = 0;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var lineNumber = index + 1;
            var indent = 0;
            while (indent < raw.Length && raw[indent] == ' ')
                indent++;

            if (indent < raw.Length && raw[indent] == '\t')
            {
                Add(diagnostics, lineNumber, indent + 1, "Tabs are not allowed in indentation; use spaces.");
                continue;
            }

            var content = raw[indent..].TrimEnd();
            if (content.Length == 0 || content[0] == '#')
                continue;

            // #2429. A state directive names the C# type every {Binding} in
            // this file resolves against. It is parsed once here, never as a
            // node, so the arena stays a tree of widgets only.
            if (content.StartsWith(StateDirective, StringComparison.OrdinalIgnoreCase))
            {
                var typeName = content[StateDirective.Length..].Trim();
                if (typeName.Length == 0)
                    Add(diagnostics, lineNumber, indent + StateDirective.Length + 1,
                        "A state directive needs a type name.");
                else if (stateType is not null)
                    Add(diagnostics, lineNumber, indent + 1,
                        "Only one state directive is allowed per file.");
                else
                {
                    stateType = typeName;
                    stateTypeLine = lineNumber;
                }

                continue;
            }

            if (content[0] == '.')
            {
                while (levels.Count > 0 && levels[^1].Indent > indent)
                    levels.RemoveAt(levels.Count - 1);

                if (levels.Count == 0)
                {
                    Add(diagnostics, lineNumber, indent + 1, "An operation needs a node above it.");
                    continue;
                }

                var operationTokens = Tokenize(content[1..], lineNumber, indent + 2, diagnostics);
                if (operationTokens.Count == 0 || operationTokens[0].Kind != SpecValueKind.Identifier)
                {
                    Add(diagnostics, lineNumber, indent + 2, "An operation needs an identifier after the dot.");
                    continue;
                }

                var name = operationTokens[0].Text;
                var arguments = ArgumentsFrom(operationTokens, 1);
                var target = levels[^1].Node;
                if (string.Equals(name, "id", StringComparison.Ordinal))
                {
                    if (arguments.Length == 1
                        && arguments[0].Kind == SpecValueKind.String
                        && arguments[0].Text.Length > 0)
                    {
                        target.SetId(arguments[0].Text);
                    }
                    else
                    {
                        Add(diagnostics, lineNumber, indent + 2, ".id needs one quoted name.");
                    }
                }

                target.AddOperation(new SpecOp(name, arguments, lineNumber, indent + 2));
                continue;
            }

            while (levels.Count > 0 && levels[^1].Indent >= indent)
                levels.RemoveAt(levels.Count - 1);

            var declaration = content;
            if (declaration[^1] == ':')
                declaration = declaration[..^1].TrimEnd();

            var tokens = Tokenize(declaration, lineNumber, indent + 1, diagnostics);
            if (tokens.Count == 0 || tokens[0].Kind != SpecValueKind.Identifier)
            {
                Add(diagnostics, lineNumber, indent + 1, "A node line needs a kind identifier.");
                continue;
            }

            if (!TryResolveKind(tokens[0].Text, out var kind))
            {
                Add(diagnostics, lineNumber, indent + 1, $"Unknown node kind '{tokens[0].Text}'.");
                continue;
            }

            var node = arena.CreateNode(kind, lineNumber, indent + 1);
            if (tokens.Count > 1)
                node.AddOperation(
                    new SpecOp(NewOperation, ArgumentsFrom(tokens, 1), lineNumber, indent + 1));

            if (levels.Count > 0)
                levels[^1].Node.AddChild(node);
            else
                arena.AddRoot(node);

            levels.Add(new Level(indent, node));
        }

        return new MarkupParseResult(arena, diagnostics, stateType, stateTypeLine);
    }

    private static SpecValue[] ArgumentsFrom(List<SpecValue> tokens, int first) =>
        tokens.GetRange(first, tokens.Count - first).ToArray();

    /// <summary>
    ///     Maps a markup name to a kind. Underscores and dashes are optional,
    ///     so filter_list, filter-list and FilterList are the same declaration.
    /// </summary>
    private static bool TryResolveKind(string name, out SpecKind kind)
    {
        kind = default;
        if (name.Length == 0 || !char.IsLetter(name[0]))
            return false;

        var candidate = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse(candidate, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    private static List<SpecValue> Tokenize(
        string text,
        int line,
        int firstColumn,
        List<MarkupDiagnostic> diagnostics)
    {
        var tokens = new List<SpecValue>();
        var position = 0;
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
                continue;
            }

            var start = position;
            if (text[position] == '"')
            {
                position++;
                var value = new StringBuilder();
                var closed = false;
                while (position < text.Length)
                {
                    var current = text[position++];
                    if (current == '"')
                    {
                        closed = true;
                        break;
                    }

                    if (current == '\\' && position < text.Length)
                    {
                        var escaped = text[position++];
                        value.Append(escaped switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            _ => escaped
                        });
                        continue;
                    }

                    value.Append(current);
                }

                if (!closed)
                {
                    Add(diagnostics, line, firstColumn + start, "Unterminated string literal.");
                    break;
                }

                tokens.Add(new SpecValue(SpecValueKind.String, value.ToString()));
                continue;
            }

            if (char.IsLetter(text[position]) || text[position] == '_')
            {
                position++;
                while (position < text.Length
                       && (char.IsLetterOrDigit(text[position]) || text[position] is '_' or '-' or '.' or '/'))
                    position++;

                var word = text[start..position];
                var kind = word.Equals("true", StringComparison.OrdinalIgnoreCase)
                           || word.Equals("false", StringComparison.OrdinalIgnoreCase)
                    ? SpecValueKind.Boolean
                    : SpecValueKind.Identifier;
                tokens.Add(new SpecValue(kind, kind == SpecValueKind.Boolean ? word.ToLowerInvariant() : word));
                continue;
            }

            if (char.IsDigit(text[position]) || text[position] == '-'
                && position + 1 < text.Length && char.IsDigit(text[position + 1]))
            {
                if (text[position] == '-')
                    position++;
                while (position < text.Length && char.IsDigit(text[position]))
                    position++;

                if (position < text.Length && text[position] == '.')
                {
                    var dot = position;
                    position++;
                    while (position < text.Length && char.IsDigit(text[position]))
                        position++;
                    if (position == dot + 1)
                        position = dot;
                }

                var number = text[start..position];
                if (decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    tokens.Add(new SpecValue(SpecValueKind.Number, number));
                else
                    Add(diagnostics, line, firstColumn + start, $"Invalid number '{number}'.");
                continue;
            }

            Add(diagnostics, line, firstColumn + start, $"Unexpected character '{text[position]}'.");
            position++;
        }

        return tokens;
    }

    private static void Add(List<MarkupDiagnostic> diagnostics, int line, int column, string message) =>
        diagnostics.Add(new MarkupDiagnostic(line, column, message));

    private readonly record struct Level(int Indent, SpecNode Node);
}
