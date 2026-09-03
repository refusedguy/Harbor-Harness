using System.Text.Json;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

public sealed class SkillTool : ITool
{
    private readonly ILogger<SkillTool> _logger;

    public SkillTool(ILogger<SkillTool> logger)
    {
        _logger = logger;
    }

    public ToolName Name => ToolName.Create("skill");
    public string DisplayName => "Skill";
    public string Description =>
        "Load a skill by name and return its full Markdown content. " +
        "Use this when the agent needs to read the instructions inside a discovered skill.";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "skill: Load a skill's full content by name";
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `skill` after `list` to read a specific skill's instructions",
        "Skill names are file stems under .harbor/skills/ (no .md extension)"
    ];

    public JsonDocument ParameterSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Skill name (file stem under .harbor/skills/)" }
          },
          "required": ["name"]
        }
        """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("name", out var nameEl)
            || nameEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameEl.GetString()))
            return Result.Failure("Missing or empty 'name'.");

        return Result.Success();
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string name = args.GetProperty("name").GetString()!;
        string workingDirectory = Directory.GetCurrentDirectory();

        var provider = context.Services.GetService(typeof(ISkillProvider)) as ISkillProvider;
        if (provider is null)
        {
            return Task.FromResult(ToolResult.Error(
                "Skill provider is not available in this session."));
        }

        string? content = provider.ReadSkill(workingDirectory, name);
        if (content is null)
        {
            return Task.FromResult(ToolResult.Error(
                $"Skill '{name}' not found. Use `skill list` to see available skills."));
        }

        return Task.FromResult(ToolResult.Success(
            content,
            new { name, path = Path.Combine(workingDirectory, ".harbor", "skills", name + ".md") }));
    }
}
