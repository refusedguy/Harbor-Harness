using System.Collections;
using System.Collections.Frozen;

namespace Harbor.Application.Permissions;

/// <summary>
///     Builtin <see cref="IPathExtractionPolicy" /> for tools whose primary
///     argument is a workspace-relative file path in <c>path</c>: the value is
///     normalized via <see cref="PermissionPathNormalizer" /> before rule
///     matching so traversal sequences cannot smuggle a path past anchored
///     Allow rules (A1).
/// </summary>
/// <remarks>
///     Stateless (the tool set is fixed at construction) — safe to share across
///     threads. Construct with a custom tool set to onboard a new file-based
///     tool without editing the permission core (issue #178).
/// </remarks>
public sealed class PathArgExtractionPolicy : IPathExtractionPolicy
{
    private static readonly FrozenSet<string> DefaultTools = new[]
    {
        "read", "write", "edit", "ls", "patch", "tree", "ripgrep", "notebook", "mcp",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Shared stateless instance (default tool set).</summary>
    public static readonly PathArgExtractionPolicy Instance = new();

    private readonly FrozenSet<string> _tools;

    /// <summary>Construct a policy for the default file-based tool set.</summary>
    public PathArgExtractionPolicy()
        : this(DefaultTools)
    {
    }

    /// <summary>Construct a policy for a custom file-based tool set (ordinal, case-sensitive).</summary>
    /// <param name="tools">Tool names whose <c>path</c> argument is normalized.</param>
    public PathArgExtractionPolicy(IEnumerable<string> tools)
    {
        _tools = tools.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool Handles(string toolName) => _tools.Contains(toolName);

    /// <inheritdoc />
    public PathExtraction Extract(string toolName, JsonElement args, string workspaceRoot)
    {
        try
        {
            return PermissionPathNormalizer.Normalize(
                args.TryGetProperty("path", out var p) ? p.GetString() : null,
                workspaceRoot);
        }
        catch
        {
            return new PathExtraction("*", true);
        }
    }
}
