using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugin.Echo;

/// <summary>
///     Showcase slice for #1434: a minimal, dependency-free tool plugin. It
///     contributes one <c>echo</c> tool on the EXISTING tool axis only, with
///     no new extension axis (#555 freeze). Install in one command with install.sh;
///     the .cs drop-in is compiled by Harbor at startup via Roslyn.
/// </summary>
public sealed class EchoPlugin : IToolPlugin
{
    public string Name => "echo";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 4, 0);
    public string Description => "Exemplary no-dependency plugin that adds an echo tool.";

    public void Initialize(PluginContext context)
        => context.CreateLogger<EchoPlugin>().LogInformation("Echo plugin initialized");

    public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<EchoTool>();

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
///     A trivial tool that echoes its input back. Demonstrates the minimal
///     <see cref="ITool" /> surface: schema, argument validation, execution.
/// </summary>
public sealed class EchoTool : ITool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "Text to echo back" },
            "upper": { "type": "boolean", "description": "Uppercase the echoed text (default: false)" }
          },
          "required": ["text"]
        }
        """);

    public ToolName Name => ToolName.Create("echo");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string DisplayName => "Echo";
    public string Description => "Echoes the supplied text back. A minimal, dependency-free showcase tool.";
    public JsonDocument ParameterSchema => Schema;
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "echo: Echo text back (optionally uppercased)";
    public IReadOnlyList<string> PromptGuidelines { get; } = Array.Empty<string>();

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(text.GetString()))
            return Result.Failure("'text' must be a non-empty string.");
        return Result.Success();
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string text = args.GetProperty("text").GetString()!;
        bool upper = args.TryGetProperty("upper", out var u) && u.ValueKind == JsonValueKind.True;
        string echoed = upper ? text.ToUpperInvariant() : text;
        return Task.FromResult(ToolResult.Success($"Echo: {echoed}", new { text = echoed }));
    }
}
