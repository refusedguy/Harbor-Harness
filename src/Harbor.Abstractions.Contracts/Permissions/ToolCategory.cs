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
/// <remarks>
///     <para>
///         The classification is DERIVED from the tools' own declarations
///         (<see cref="ToolSafetyDeclaration.Category" />, issue #595) rather than
///         held in a private dictionary. That dictionary held 13 of the 20 tools
///         that register, so seven — <c>skill</c>, <c>task</c>, <c>lsp</c>,
///         <c>mcp_prompt</c>, <c>read_mcp_resource</c>, <c>session_read</c>,
///         <c>session_steer</c> — matched no category rule at all. Nothing about
///         that was exploitable while none of them was a write-class tool, but the
///         class documented the promise "category rules gate whole classes" and the
///         promise was unbacked for seven registered tools: add a write-class tool
///         without a dictionary row and its users' category rules silently stop
///         applying to it.
///     </para>
///     <para>
///         A tool that declares no category still matches no category rule, which is
///         the documented behaviour and is fail-closed: an unclassified tool cannot
///         be authorised by a rule the user wrote for a class it was never in.
///     </para>
/// </remarks>
public static class ToolCategories
{
    /// <summary>
    ///     Builtin tool name → its approval category, read off the declarations
    ///     (#595). A tool that declares no category is simply absent, exactly as a
    ///     plugin tool is.
    /// </summary>
    private static readonly FrozenDictionary<string, ToolCategory> ByTool =
        BuiltinToolSafetyProfiles.All
            .Where(d => d.Category is { } category)
            .ToFrozenDictionary(d => d.ToolName, d => d.Category!.Value, StringComparer.OrdinalIgnoreCase);

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
