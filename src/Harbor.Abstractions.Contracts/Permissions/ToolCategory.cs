using System.Collections.Frozen;

namespace Harbor.Abstractions.Permissions;

/// <summary>
///     Tool categories for granular approvals (sprint 6 C2): a permission
///     rule may name a CATEGORY instead of a single tool — e.g.
///     <c>new("exec", "*", Ask)</c> gates every execution-class tool at once,
///     <c>new("write", "*", Allow)</c> whitelists all mutation-class tools.
/// </summary>
/// <remarks>
///     Categories are resolved against the builtin tool vocabulary; unknown
///     tool names belong to NO category, so a category rule never matches a
///     plugin tool by accident.
/// </remarks>
public enum ToolCategory
{
    /// <summary>Read-only inspection tools.</summary>
    Read,

    /// <summary>Workspace-mutating tools.</summary>
    Write,

    /// <summary>Network-reaching tools.</summary>
    Network,

    /// <summary>Shell / code-execution tools.</summary>
    Exec,

    /// <summary>MCP bridge calls.</summary>
    Mcp
}

/// <summary>
///     Classification of builtin tool names into <see cref="ToolCategory"/>,
///     plus lookup of category names used inside <see cref="PermissionRule"/>
///     permission fields.
/// </summary>
public static class ToolCategories
{
    /// <summary>Builtin tool name → its approval category (exact names, case-insensitive lookups).</summary>
    private static readonly FrozenDictionary<string, ToolCategory> ByTool = new Dictionary<string, ToolCategory>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = ToolCategory.Read,
        ["glob"] = ToolCategory.Read,
        ["grep"] = ToolCategory.Read,
        ["ls"] = ToolCategory.Read,
        ["tree"] = ToolCategory.Read,
        ["ripgrep"] = ToolCategory.Read,
        ["write"] = ToolCategory.Write,
        ["edit"] = ToolCategory.Write,
        ["patch"] = ToolCategory.Write,
        ["notebook"] = ToolCategory.Write,
        ["webfetch"] = ToolCategory.Network,
        ["bash"] = ToolCategory.Exec,
        ["mcp"] = ToolCategory.Mcp
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Category name → category (for rule permission fields).</summary>
    private static readonly FrozenDictionary<string, ToolCategory> ByName =
        Enum.GetValues<ToolCategory>()
            .ToFrozenDictionary(c => c.ToString(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The category of a builtin tool name; <see langword="false"/> for
    ///     unknown (plugin) tools.
    /// </summary>
    public static bool TryClassify(string toolName, out ToolCategory category)
        => ByTool.TryGetValue(toolName, out category);

    /// <summary>
    ///     True when <paramref name="rulePermission"/> names a category that
    ///     contains <paramref name="toolName"/>.
    /// </summary>
    public static bool CategoryMatches(string rulePermission, string toolName)
    {
        if (!ByName.TryGetValue(rulePermission, out ToolCategory category)) return false;
        return ByTool.TryGetValue(toolName, out ToolCategory toolCategory) && toolCategory == category;
    }
}
