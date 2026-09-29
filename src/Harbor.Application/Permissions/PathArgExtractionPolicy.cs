using System.Collections.Frozen;

namespace Harbor.Application.Permissions;

/// <summary>
///     Builtin <see cref="IPathExtractionPolicy" /> for tools whose
///     rule-matched argument is a workspace-relative file path: the value is
///     normalized via <see cref="PermissionPathNormalizer" /> before rule
///     matching so traversal sequences cannot smuggle a path past anchored
///     Allow rules (A1).
/// </summary>
/// <remarks>
///     <para>
///         Stateless (the tool set is fixed at construction) — safe to share across
///         threads. Construct with a custom tool set to onboard a new file-based
///         tool without editing the permission core (issue #178).
///     </para>
///     <para>
///         The DEFAULT tool set is derived, not written down (#595). It used to be a
///         private <c>new[] { "read", "write", … }</c> that had already fallen behind
///         the declarations beside it: <c>glob</c>, <c>grep</c> and <c>lsp</c> all
///         declare <see cref="ToolArgKind.Path" />, and all three were missing, so
///         they fell through to <see cref="LegacyArgExtractionPolicy" /> and their
///         <c>path</c> argument was matched RAW. That is not only an un-normalized
///         match: the legacy extraction reports <c>IsOutsideWorkspace == false</c>, so
///         the A1/A2 downgrade in <see cref="PermissionService" /> that turns an
///         Allow into an Ask for a path outside the workspace never ran for them.
///         The omission was a permission hole with no test, no warning and no
///         compile error — the exact shape of issue #557, in a second file.
///     </para>
///     <para>
///         Reading the same <see cref="BuiltinToolSafetyProfiles" /> rows the safety
///         policies are built from is what makes the hole structural rather than
///         accidental: a tool that declares <see cref="ToolArgKind.Path" /> is
///         normalized here on the day it is declared, with no edit to this file.
///     </para>
/// </remarks>
public sealed class PathArgExtractionPolicy : IPathExtractionPolicy
{
    /// <summary>
    ///     The path-taking builtin tools, read out of the safety declarations. A
    ///     separate frozen set would be a list, and a list is what #595 is about.
    /// </summary>
    private static readonly FrozenSet<string> DefaultTools = FrozenSet.ToFrozenSet(
        BuiltinToolSafetyProfiles.All
            .Where(d => d.Profile.ArgKind == ToolArgKind.Path)
            .Select(d => d.ToolName),
        StringComparer.Ordinal);

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
