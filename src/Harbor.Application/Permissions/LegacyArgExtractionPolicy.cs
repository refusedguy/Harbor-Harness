namespace Harbor.Application.Permissions;

/// <summary>
///     Terminal <see cref="IPathExtractionPolicy" /> fallback preserving the
///     legacy raw (un-normalized) argument extraction: <c>path</c> for
///     read/write/edit/ls, <c>command</c> for bash, <c>pattern</c> for
///     glob/grep, and <c>"*"</c> for everything else. Always handles, so
///     policy dispatch is total.
/// </summary>
/// <remarks>
///     Stateless singleton — safe to share across threads. Moved verbatim out
///     of <see cref="PermissionService" /> (issue #178); behavior is 1-1 with
///     the former <c>default:</c> branch.
/// </remarks>
public sealed class LegacyArgExtractionPolicy : IPathExtractionPolicy
{
    /// <summary>Shared stateless instance.</summary>
    public static readonly LegacyArgExtractionPolicy Instance = new();

    /// <inheritdoc />
    public bool Handles(string toolName) => true;

    /// <inheritdoc />
    public PathExtraction Extract(string toolName, JsonElement args, string workspaceRoot) =>
        new(ExtractArgPath(toolName, args), false);

    /// <summary>Raw argument extraction (legacy, un-normalized). Kept for compatibility.</summary>
    internal static string ExtractArgPath(string toolName, JsonElement args)
    {
        try
        {
            return toolName switch
            {
                "read" or "write" or "edit" => args.TryGetProperty("path", out var p) ? p.GetString() ?? "*" : "*",
                "bash" => args.TryGetProperty("command", out var c) ? c.GetString() ?? "*" : "*",
                "glob" => args.TryGetProperty("pattern", out var p) ? p.GetString() ?? "*" : "*",
                "grep" => args.TryGetProperty("pattern", out var p) ? p.GetString() ?? "*" : "*",
                "ls" => args.TryGetProperty("path", out var p) ? p.GetString() ?? "*" : "*",
                _ => "*"
            };
        }
        catch
        {
            return "*";
        }
    }
}
