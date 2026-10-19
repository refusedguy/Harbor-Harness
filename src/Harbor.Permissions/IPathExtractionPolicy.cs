namespace Harbor.Application.Permissions;

/// <summary>
///     Rule-matching argument for one tool call: the string evaluated against
///     the agent's <see cref="PermissionRuleset" /> plus whether the underlying
///     path resolves outside the workspace root (an <see cref="PermissionAction.Allow" />
///     verdict for such a path is downgraded to <see cref="PermissionAction.Ask" />).
/// </summary>
/// <param name="ArgPath">The rule-matching string (normalized path, command, pattern, or <c>"*"</c>).</param>
/// <param name="IsOutsideWorkspace">Whether the path resolves outside the workspace root.</param>
public readonly record struct PathExtraction(string ArgPath, bool IsOutsideWorkspace);

/// <summary>
///     Strategy for extracting the rule-matching argument of a tool call
///     (Strategy pattern, GoF). Replaces the tool-name <c>switch</c> formerly
///     hardcoded in <see cref="PermissionService" /> (issue #178): a new
///     file-based tool is onboarded by registering an implementation — e.g.
///     <c>new PathArgExtractionPolicy("myfilesync")</c> — instead of editing
///     the permission core.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="PermissionService" /> consults its policies in order and
///         uses the first whose <see cref="Handles" /> returns <see langword="true" />
///         for the tool name. <see cref="LegacyArgExtractionPolicy" /> is always
///         the terminal fallback, so the dispatch is total.
///     </para>
///     <para>
///         Implementations MUST be stateless and thread-safe; extraction runs on
///         the permission-check hot path.
///     </para>
/// </remarks>
public interface IPathExtractionPolicy
{
    /// <summary>
    ///     Returns <see langword="true" /> when this policy extracts arguments
    ///     for the given tool name (ordinal, case-sensitive — matching the
    ///     legacy <c>switch</c> semantics exactly).
    /// </summary>
    /// <param name="toolName">The tool name from the model request.</param>
    bool Handles(string toolName);

    /// <summary>
    ///     Extracts the rule-matching argument for a call to a handled tool.
    /// </summary>
    /// <param name="toolName">The tool name from the model request (already accepted by <see cref="Handles" />).</param>
    /// <param name="args">The raw JSON arguments of the tool call.</param>
    /// <param name="workspaceRoot">The workspace root paths resolve against.</param>
    PathExtraction Extract(string toolName, JsonElement args, string workspaceRoot);
}

/// <summary>
///     The builtin <see cref="IPathExtractionPolicy" /> composition used when
///     no explicit policy list is supplied (OCP: extend via
///     <see cref="WithExtra" />, never by editing the permission core).
/// </summary>
public static class DefaultPathExtractionPolicies
{
    /// <summary>
    ///     The default policy order: path-argument normalization first, legacy
    ///     raw-argument extraction as the terminal fallback.
    /// </summary>
    public static IPathExtractionPolicy[] Defaults { get; } =
    [
        PathArgExtractionPolicy.Instance,
        LegacyArgExtractionPolicy.Instance,
    ];

    /// <summary>
    ///     Composes a policy list with custom policies taking precedence over
    ///     the builtins (e.g. <c>WithExtra(new PathArgExtractionPolicy("myfilesync"))</c>
    ///     onboards a new file-based tool without touching the permission core).
    /// </summary>
    /// <param name="extra">Custom policies, consulted before the builtins.</param>
    public static IPathExtractionPolicy[] WithExtra(params IPathExtractionPolicy[] extra)
    {
        var composed = new IPathExtractionPolicy[extra.Length + Defaults.Length];
        Array.Copy(extra, composed, extra.Length);
        Array.Copy(Defaults, 0, composed, extra.Length, Defaults.Length);
        return composed;
    }
}
