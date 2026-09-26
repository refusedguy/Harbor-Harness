using Harbor.Abstractions.Results;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Loads a <c>SKILL.md</c> skill body by name. Skills are discovered from
///     the project-local <c>.harbor/skills/</c> directory (wins on collisions)
///     and the global <c>~/.harbor/skills/</c> directory — the same roots the
///     system prompt's <c>&lt;available_skills&gt;</c> block is built from.
///     Accepts a skill NAME only (never a path): the resolved file must live
///     under a known skills root, so a crafted name can never escape them.
/// </summary>
public sealed class SkillTool : ITool
{
    /// <summary>Skills directory name under the project and harbor-home roots.</summary>
    internal const string SkillsDirName = "skills";

    /// <summary>Upper bound on returned skill body chars; longer bodies are truncated with a note.</summary>
    public const int MaxContentChars = 12_000;

    /// <summary>Refuse to read skill files larger than this (likely a mistake, not a skill).</summary>
    internal const long MaxFileBytes = 1024 * 1024;

    private readonly ILogger<SkillTool> _logger;
    private readonly ISessionStore? _store;
    private readonly string? _projectSkillsDir;
    private readonly string? _globalSkillsDir;

    /// <summary>
    ///     Construct a <see cref="SkillTool" /> that resolves the session store
    ///     from <see cref="ToolContext.Services" /> on each call (preferred for DI).
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public SkillTool(ILogger<SkillTool> logger)
        : this(null, logger, null, null)
    {
    }

    /// <summary>
    ///     Construct a <see cref="SkillTool" /> with a fixed session store
    ///     (used in tests where the DI container is not configured).
    /// </summary>
    /// <param name="store">The session store to resolve working directories from.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public SkillTool(ISessionStore? store, ILogger<SkillTool> logger)
        : this(store, logger, null, null)
    {
    }

    /// <summary>
    ///     Construct a <see cref="SkillTool" /> with pinned skills roots,
    ///     bypassing session/store resolution (used in tests and by hosts
    ///     with a fixed skills layout).
    /// </summary>
    /// <param name="store">The session store to resolve working directories from (unused when roots are pinned).</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="projectSkillsDir">Project <c>.harbor/skills</c> directory (preferred scope).</param>
    /// <param name="globalSkillsDir">Global <c>~/.harbor/skills</c> directory (fallback scope).</param>
    public SkillTool(
        ISessionStore? store,
        ILogger<SkillTool> logger,
        string? projectSkillsDir,
        string? globalSkillsDir)
    {
        _store = store;
        _logger = logger;
        _projectSkillsDir = projectSkillsDir;
        _globalSkillsDir = globalSkillsDir;
    }

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("skill");

    /// <inheritdoc />
    public string DisplayName => "Skill";

    /// <inheritdoc />
    public string Description =>
        "Load a SKILL.md skill body by name. Skills come from .harbor/skills/ (project) "
        + "and ~/.harbor/skills/ (global); the available names are listed in the system prompt. "
        + "Use this to follow a skill's workflow before doing the task it describes.";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    /// <inheritdoc />
    public string? PromptSnippet => "skill: Load a SKILL.md skill body by name";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `skill` to load the full workflow for a named skill from <available_skills>",
        "Project skills shadow same-named global skills",
        "The result is Markdown — follow its steps, do not paste it back verbatim",
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "name":  { "type": "string", "description": "Skill name as listed in <available_skills> (e.g. 'code-review')" },
                                                                          "scope": { "type": "string", "description": "Where to look: 'project', 'global', or 'any' (default)" }
                                                                        },
                                                                        "required": ["name"]
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("name", out var nEl)
            || nEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nEl.GetString()))
            return Result.Failure("Missing or empty 'name'.");

        if (args.TryGetProperty("scope", out var sEl)
            && sEl.ValueKind == JsonValueKind.String
            && !IsKnownScope(sEl.GetString()))
            return Result.Failure("'scope' must be 'project', 'global' or 'any'.");

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string name = args.GetProperty("name").GetString()!;
        string scope = args.TryGetProperty("scope", out var sEl) && sEl.ValueKind == JsonValueKind.String
            ? sEl.GetString()!.ToLowerInvariant()
            : "any";

        if (!IsSafeName(name))
            return ToolResult.Error(
                $"Invalid skill name '{name}': use the plain skill name (letters, digits, '-', '_', '.').",
                new { name });

        var roots = await ResolveRootsAsync(context, cancellationToken).ConfigureAwait(false);
        string? body = null;
        string? foundScope = null;
        string? foundPath = null;
        if ((scope is "any" or "project") && roots.Project is not null
            && TryLoadFromRoot(roots.Project, name, out body, out foundPath))
            foundScope = "project";
        if (body is null && (scope is "any" or "global") && roots.Global is not null
            && TryLoadFromRoot(roots.Global, name, out body, out foundPath))
            foundScope = "global";

        if (body is null)
        {
            _logger.LogDebug("Skill not found: name={Name} scope={Scope}", name, scope);
            return ToolResult.Error(
                $"Skill '{name}' not found (scope='{scope}'). " +
                "Available skills are listed in the system prompt's <available_skills> block.",
                new { name, scope });
        }

        bool truncated = body.Length > MaxContentChars;
        string content = truncated
            ? body[..MaxContentChars] + $"\n\n…(truncated to {MaxContentChars} chars; read the skill file directly for the rest: {foundPath})"
            : body;
        _logger.LogDebug("Skill loaded: name={Name} scope={Scope} chars={Chars}", name, foundScope, body.Length);
        return ToolResult.Success(content, new { name, scope = foundScope, chars = body.Length, truncated });
    }

    private static bool IsKnownScope(string? scope) =>
        scope is not null && (scope.Equals("project", StringComparison.OrdinalIgnoreCase)
            || scope.Equals("global", StringComparison.OrdinalIgnoreCase)
            || scope.Equals("any", StringComparison.OrdinalIgnoreCase));

    /// <summary>Plain file/dir names only — no separators, no parent traversal.</summary>
    internal static bool IsSafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            return false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool ok = char.IsLetterOrDigit(c) || c is '-' or '_' or '.';
            if (!ok)
                return false;
        }

        return !name.Contains("..", StringComparison.Ordinal);
    }

    private async Task<(string? Project, string? Global)> ResolveRootsAsync(
        ToolContext context, CancellationToken cancellationToken)
    {
        if (_projectSkillsDir is not null || _globalSkillsDir is not null)
            return (_projectSkillsDir, _globalSkillsDir);

        string? projectDir = null;
        var store = _store;
        if (store is null && context.Services is not null)
            store = context.Services.GetService<ISessionStore>();
        if (store is not null)
        {
            var session = await store.GetAsync(context.SessionId, cancellationToken).ConfigureAwait(false);
            if (session.IsSuccess && !string.IsNullOrWhiteSpace(session.Value.Directory))
                projectDir = session.Value.Directory;
        }

        projectDir ??= Environment.CurrentDirectory;
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? globalDir = string.IsNullOrEmpty(userProfile) ? null : userProfile;
        return (
            Path.Combine(projectDir, ".harbor", SkillsDirName),
            globalDir is null ? null : Path.Combine(globalDir, ".harbor", SkillsDirName));
    }

    /// <summary>
    ///     Load <c>&lt;root&gt;/&lt;name&gt;/SKILL.md</c> (preferred) or the legacy
    ///     flat <c>&lt;root&gt;/&lt;name&gt;.md</c>. Returns false when neither exists.
    /// </summary>
    internal static bool TryLoadFromRoot(string root, string name, out string? body, out string? path)
    {
        path = Path.Combine(root, name, "SKILL.md");
        if (File.Exists(path))
        {
            return TryReadCapped(path, out body);
        }

        path = Path.Combine(root, name + ".md");
        if (File.Exists(path))
        {
            return TryReadCapped(path, out body);
        }

        body = null;
        path = null;
        return false;
    }

    private static bool TryReadCapped(string path, out string? body)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                body = null;
                return false;
            }

            body = File.ReadAllText(path);
            return true;
        }
        catch (IOException)
        {
            body = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            body = null;
            return false;
        }
    }
}
