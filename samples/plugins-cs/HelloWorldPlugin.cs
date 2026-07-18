// Example CS-source plugin for Harbor.
//
// Drop this file into ~/.harbor/plugins/ (or <project>/.harbor/plugins/) and Harbor will
// compile it at startup via Roslyn and register the `hello` tool. No .csproj, no DLL —
// just C# source. See docs/PLUGIN_SYSTEM.md for the full reference.

using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

namespace Harbor.Sample.HelloWorld;

/// <summary>
///     Sample CS-source plugin that contributes a single <c>hello</c> tool.
/// </summary>
public sealed class HelloWorldPlugin : IToolPlugin
{
    /// <inheritdoc />
    public string Name => "hello-world";

    /// <inheritdoc />
    public Version Version => new(1, 0, 0);

    /// <inheritdoc />
    public Version RequiredHarborVersion => new(0, 4, 0);

    /// <inheritdoc />
    public string Description => "Sample CS-source plugin that contributes a `hello` tool.";

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        context.CreateLogger<HelloWorldPlugin>().LogInformation("HelloWorld plugin initialized");
    }

    /// <inheritdoc />
    public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<HelloTool>();

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
///     A trivial tool that returns a greeting. Demonstrates the minimal <see cref="ITool" />
///     surface area: name, schema, validation, execution.
/// </summary>
public sealed class HelloTool : ITool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Name to greet (default: 'world')" }
          },
          "required": []
        }
        """);

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("hello");

    /// <inheritdoc />
    public string DisplayName => "Hello";

    /// <inheritdoc />
    public string Description => "Returns a friendly greeting. Useful for smoke-testing the plugin system.";

    /// <inheritdoc />
    public JsonDocument ParameterSchema => Schema;

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    /// <inheritdoc />
    public string? PromptSnippet => "hello: Returns a greeting";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (args.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
        {
            string name = nameProp.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return Result.Failure("'name' cannot be empty when provided.");
        }
        return Result.Success();
    }

    /// <inheritdoc />
    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string name = "world";
        if (args.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
        {
            string? parsed = nameProp.GetString();
            if (!string.IsNullOrWhiteSpace(parsed))
                name = parsed;
        }

        string greeting = $"Hello, {name}! — from the Harbor CS plugin system.";
        return Task.FromResult(ToolResult.Success(greeting, new { name }));
    }
}
