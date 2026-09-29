namespace Harbor.Abstractions.Permissions;

/// <summary>
///     The declared safety profile of every BUILTIN tool — the fallback policy set
///     <see cref="PermissionRuleset.DefaultSafetyPolicies" /> is built from when a
///     caller evaluates a ruleset without a tool registry in hand.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is not the enforcement point.</b> The live
///         <c>IPermissionService</c> composes its policies from the tools that
///         actually registered (<see cref="ToolSafetyPolicies.Build" /> over
///         <c>IToolRegistry</c>), so a tool added to the registry is guarded the
///         moment it exists whether or not a row appears here. This table only
///         serves the two places that have no registry: a direct
///         <c>PermissionRuleset.Evaluate</c> call, and
///         <c>ToolRegistry.ResolveTools</c> (which passes <c>"*"</c> as the
///         argument, so the guard is inert there by construction).
///     </para>
///     <para>
///         The table cannot rot silently: <c>BuiltinToolSafetyDeclarationsTests</c>
///         (tests/Harbor.Hosting.Tests) cross-checks every row against the profile
///         the corresponding <c>ITool</c> actually declares in
///         <c>ToolsCatalog.CreateToolRegistry</c>, and fails when a registered tool
///         has no row here or a row names a tool that no longer registers. That test
///         is the guard; the runtime derivation is the fix.
///     </para>
/// </remarks>
public static class BuiltinToolSafetyProfiles
{
    private static readonly ToolSafetyDeclaration[] Declarations =
    [
        // Path-taking: a glob Allow rule (e.g. new("write", "src/*", Allow)) must not
        // authorise a traversal escape or an absolute path for any of these.
        new("read", ToolSafetyProfile.Path()) { Category = ToolCategory.Read },
        new("write", ToolSafetyProfile.Path()) { Category = ToolCategory.Write },
        new("edit", ToolSafetyProfile.Path()) { Category = ToolCategory.Write },
        new("patch", ToolSafetyProfile.Path()) { Category = ToolCategory.Write },
        new("ls", ToolSafetyProfile.Path()) { Category = ToolCategory.Read },
        new("tree", ToolSafetyProfile.Path()) { Category = ToolCategory.Read },
        new("lsp", ToolSafetyProfile.Path()) { Category = ToolCategory.Read },
        new("glob", ToolSafetyProfile.Path()) { Category = ToolCategory.Read },
        new("grep", ToolSafetyProfile.Path()) { Category = ToolCategory.Read },
        new("ripgrep", ToolSafetyProfile.Path()) { Category = ToolCategory.Read },
        new("notebook", ToolSafetyProfile.Path()) { Category = ToolCategory.Write },
        new("mcp", ToolSafetyProfile.Path()) { Category = ToolCategory.Mcp },

        // Shell: destructive-command deny + token-wise allow prefix, no path guard.
        new("bash", ToolSafetyProfile.Command()) { Category = ToolCategory.Exec },

        // Explicit opt-outs — a name, a URL or a free-form blob, never a path.
        // The category is stated here (#595) even though the ARGUMENT is not a path:
        // the two are independent questions. `webfetch` is Opaque because its
        // argument is a URL, and Network because reaching the network is what its
        // approval class is about.
        new("webfetch", ToolSafetyProfile.Opaque) { Category = ToolCategory.Network },
        new("task", ToolSafetyProfile.Opaque) { Category = ToolCategory.Exec },
        new("skill", ToolSafetyProfile.Opaque) { Category = ToolCategory.Read },
        new("mcp_prompt", ToolSafetyProfile.Opaque) { Category = ToolCategory.Mcp },
        new("read_mcp_resource", ToolSafetyProfile.Opaque) { Category = ToolCategory.Mcp },
        new("session_read", ToolSafetyProfile.Opaque) { Category = ToolCategory.Read },
        new("session_steer", ToolSafetyProfile.Opaque) { Category = ToolCategory.Write },

        // Plugin vocabulary: declared so a plugin that loads later still finds its
        // row, never registered by the builtin host. See
        // `BuiltinToolSafetyDeclarationsTests`, which is what keeps that honest.
        new("session_broadcast", ToolSafetyProfile.Opaque) { Category = ToolCategory.Mcp },
        new("session_inbox", ToolSafetyProfile.Opaque) { Category = ToolCategory.Read },
    ];

    /// <summary>
    ///     Every builtin declaration, in report order. Immutable — callers may not
    ///     mutate the returned array.
    /// </summary>
    public static IReadOnlyList<ToolSafetyDeclaration> All => Declarations;

    /// <summary>
    ///     The safety profile a builtin tool declares; <see langword="false" /> for
    ///     a name with no row (a plugin tool — it declares its own profile on the
    ///     <c>ITool</c>).
    /// </summary>
    public static bool TryGet(string toolName, out ToolSafetyProfile profile)
    {
        for (int i = 0; i < Declarations.Length; i++)
        {
            if (!string.Equals(Declarations[i].ToolName, toolName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            profile = Declarations[i].Profile;
            return true;
        }

        profile = ToolSafetyProfile.Opaque;
        return false;
    }
}
