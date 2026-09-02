using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Lsp;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Agent-facing surface over the builtin language servers (see
///     <see cref="ILspService" />): published diagnostics, go-to-definition,
///     and find-references for supported files (TypeScript, Python, Go, Rust,
///     C#). Read-only — never mutates anything, so it is permission-allowed
///     like <c>read</c>/<c>grep</c>.
/// </summary>
/// <remarks>
///     The language server for a file's language spawns lazily on the first
///     <c>open</c> (or when an editor opened the file first). A missing server
///     binary degrades to an explanatory ToolResult — never an exception.
/// </remarks>
public sealed class LspTool : ITool
{
    private readonly ILogger<LspTool> _logger;
    private readonly ILspService? _lsp;

    /// <summary>Construct with a fixed service (composition-root wiring).</summary>
    public LspTool(ILspService lsp, ILogger<LspTool> logger)
    {
        _lsp = lsp;
        _logger = logger;
    }

    /// <summary>Construct resolving <see cref="ILspService"/> from DI per call (tests).</summary>
    public LspTool(ILogger<LspTool> logger) => _logger = logger;

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("lsp");

    /// <inheritdoc />
    public string DisplayName => "LSP";

    /// <inheritdoc />
    public string Description =>
        "Language-server intelligence for TypeScript, Python, Go, Rust and C# files. " +
        "Actions: 'diagnostics' (errors/warnings for a file), 'definition' (go-to-definition), " +
        "'references' (find all usages). Lines are 1-based (matching read tool output), columns 0-based. " +
        "A file should be opened (read) first so the language server sees its content.";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

    /// <inheritdoc />
    public string? PromptSnippet => "lsp: diagnostics / definition / references via language servers";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `lsp` with action 'diagnostics' after editing a file to see compiler-level errors",
        "Use action 'definition' to jump to where a symbol is declared, 'references' to find usages",
        "Pass the file path plus 1-based line and 0-based column of the symbol",
        "Results degrade gracefully: an absent language server binary returns an explanatory message"
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "action":  { "type": "string", "enum": ["diagnostics", "definition", "references"],
                         "description": "Which LSP query to run" },
            "path":    { "type": "string", "description": "File path (absolute or workspace-relative)" },
            "line":    { "type": "integer", "description": "1-based line of the symbol (required for definition/references)" },
            "column":  { "type": "integer", "description": "0-based column of the symbol (default 0)" }
          },
          "required": ["action", "path"]
        }
        """);

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("action", out JsonElement actionEl)
            || actionEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(actionEl.GetString()))
            return Result.Failure("Missing or empty 'action' (diagnostics | definition | references).");

        string action = actionEl.GetString()!;
        if (action is not ("diagnostics" or "definition" or "references"))
            return Result.Failure($"Unknown action '{action}' — expected diagnostics, definition or references.");

        if (!args.TryGetProperty("path", out JsonElement pathEl)
            || pathEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathEl.GetString()))
            return Result.Failure("Missing or empty 'path'.");

        if (action is not "diagnostics")
        {
            if (!args.TryGetProperty("line", out JsonElement lineEl) || lineEl.ValueKind != JsonValueKind.Number)
                return Result.Failure($"'line' (1-based) is required for action '{action}'.");
        }

        if (args.TryGetProperty("column", out JsonElement columnEl) && columnEl.ValueKind != JsonValueKind.Number)
            return Result.Failure("'column' must be an integer.");

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string action = args.GetProperty("action").GetString()!;
        string path = args.GetProperty("path").GetString()!;
        ILspService? lsp = _lsp;
        if (lsp is null && context.Services is not null)
        {
            lsp = context.Services.GetService<ILspService>();
        }

        if (lsp is null)
        {
            return ToolResult.Error(
                "No ILspService is registered in the DI container. " +
                "Register one with services.AddSingleton<ILspService>(new LspManager(logger)).");
        }

        if (!lsp.SupportsFile(path))
        {
            return ToolResult.Error(
                $"No builtin language server handles '{Path.GetExtension(path)}'. " +
                "Supported: .ts/.tsx/.js/.jsx, .py, .go, .rs, .cs.");
        }

        _logger.LogDebug("LSP {Action}: {Path}", action, path);
        return action switch
        {
            "diagnostics" => await DiagnosticsAsync(lsp, path, cancellationToken).ConfigureAwait(false),
            "definition" => await DefinitionAsync(lsp, args, path, cancellationToken).ConfigureAwait(false),
            _ => await ReferencesAsync(lsp, args, path, cancellationToken).ConfigureAwait(false),
        };
    }

    private static async Task<ToolResult> DiagnosticsAsync(ILspService lsp, string path, CancellationToken ct)
    {
        IReadOnlyList<LspDiagnostic> diagnostics = await lsp.GetDiagnosticsAsync(path, ct).ConfigureAwait(false);
        if (diagnostics.Count == 0)
        {
            return ToolResult.Success(
                $"No diagnostics published for {path} " +
                "(the file may not be open yet — read it first, or the language server found no issues).",
                new { path, count = 0 });
        }

        var lines = new string[diagnostics.Count];
        for (int i = 0; i < diagnostics.Count; i++)
        {
            LspDiagnostic d = diagnostics[i];
            // +1 on line/column for the 1-based human form.
            lines[i] = $"[{d.Severity.ToString().ToLowerInvariant()}] {d.FilePath}:{d.Line + 1}:{d.Column + 1}: {d.Message} ({d.Source})";
        }

        return ToolResult.Success(
            $"{diagnostics.Count} diagnostic(s) for {path}:\n{string.Join('\n', lines)}",
            new { path, count = diagnostics.Count });
    }

    private static async Task<ToolResult> DefinitionAsync(ILspService lsp, JsonElement args, string path, CancellationToken ct)
    {
        (int line, int column) = PositionOf(args);
        LspLocation? location = await lsp.FindDefinitionAsync(path, line, column, ct).ConfigureAwait(false);
        if (location is null)
        {
            return ToolResult.Success($"No definition found for symbol at {path}:{line + 1}:{column}.", new { path });
        }

        return ToolResult.Success(
            $"Definition: {location.FilePath}:{location.Line + 1}:{location.Column}",
            new { path, filePath = location.FilePath, line = location.Line, column = location.Column });
    }

    private static async Task<ToolResult> ReferencesAsync(ILspService lsp, JsonElement args, string path, CancellationToken ct)
    {
        (int line, int column) = PositionOf(args);
        IReadOnlyList<LspLocation> references = await lsp.FindReferencesAsync(path, line, column, ct).ConfigureAwait(false);
        if (references.Count == 0)
        {
            return ToolResult.Success($"No references found for symbol at {path}:{line + 1}:{column}.", new { path, count = 0 });
        }

        var lines = new string[references.Count];
        for (int i = 0; i < references.Count; i++)
        {
            lines[i] = $"{references[i].FilePath}:{references[i].Line + 1}:{references[i].Column}";
        }

        return ToolResult.Success(
            $"{references.Count} reference(s):\n{string.Join('\n', lines)}",
            new { path, count = references.Count });
    }

    /// <summary>Reads the 1-based line + 0-based column (defaults: column 0).</summary>
    private static (int Line, int Column) PositionOf(JsonElement args)
    {
        int line = args.TryGetProperty("line", out JsonElement l) && l.ValueKind == JsonValueKind.Number
            ? Math.Max(0, l.GetInt32() - 1)
            : 0;
        int column = args.TryGetProperty("column", out JsonElement c) && c.ValueKind == JsonValueKind.Number
            ? Math.Max(0, c.GetInt32())
            : 0;
        return (line, column);
    }
}
