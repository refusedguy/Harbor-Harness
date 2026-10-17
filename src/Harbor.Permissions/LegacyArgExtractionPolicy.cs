namespace Harbor.Application.Permissions;

/// <summary>
///     Terminal <see cref="IPathExtractionPolicy" /> fallback preserving the
///     legacy raw (un-normalized) argument extraction: the rule-matched argument
///     of a declared tool, and <c>"*"</c> for everything else. Always handles, so
///     policy dispatch is total.
/// </summary>
/// <remarks>
///     <para>
///         Stateless singleton — safe to share across threads. Moved verbatim out
///         of <see cref="PermissionService" /> (issue #178); behavior is 1-1 with
///         the former <c>default:</c> branch.
///     </para>
///     <para>
///         The tool→argument mapping is DERIVED, not switched (#595). It used to be a
///         hand-written <c>toolName switch</c> that re-stated what
///         <see cref="ToolSafetyProfile.ArgumentName" /> already declares one
///         assembly over, and had drifted: <c>glob</c> and <c>grep</c> were mapped to
///         <c>pattern</c> while both declare a <c>path</c> argument. The switch is
///         gone; the declaration answers.
///     </para>
///     <para>
///         This policy only sees a tool that <see cref="PathArgExtractionPolicy" />
///         did not claim, so in the composed host it handles the
///         <see cref="ToolArgKind.Opaque" /> tools and returns <c>"*"</c> for them —
///         which is what the old switch's <c>_ =&gt; "*"</c> arm did. It remains
///         total for a hand-composed policy list, and it is the reason the extraction
///         can never be undefined.
///     </para>
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

    /// <summary>
    ///     Raw argument extraction (legacy, un-normalized). Kept for compatibility.
    ///     The argument NAME comes from the tool's declaration; a tool with no
    ///     declaration — a plugin, or a name the model invented — yields <c>"*"</c>,
    ///     which matches no anchored rule and so cannot be authorised by one.
    /// </summary>
    internal static string ExtractArgPath(string toolName, JsonElement args)
    {
        if (!BuiltinToolSafetyProfiles.TryGet(toolName, out ToolSafetyProfile profile)
            || profile.ArgumentName is not { } argumentName)
        {
            return "*";
        }

        try
        {
            return args.TryGetProperty(argumentName, out JsonElement value)
                ? value.GetString() ?? "*"
                : "*";
        }
        catch
        {
            return "*";
        }
    }
}
