namespace Harbor.Tools.Builtin;
/// <summary>
///     Marker type that lives in Harbor.Tools.Builtin.dll so the assembly has
///     at least one type. Harbor.Tools.Builtin is a thin backward-compat facade
///     after the S2 split — all 14 tool implementations (Read, Write, Edit,
///     Bash, Grep, Glob, Ls, Task, WebFetch, Patch, Notebook, RipGrep, Tree,
///     Mcp) moved to individual Harbor.Tools.&lt;Name&gt; leaf projects (kept in
///     their original <c>Harbor.Tools.Builtin</c> namespace for source-compat).
///     Without this marker, the assembly would be empty and consumers/tests
///     that load it by name would never force it into the AppDomain.
/// </summary>
/// <remarks>
///     This type intentionally has no real members. Do not add real types
///     here — add them to the appropriate Harbor.Tools.&lt;Name&gt; leaf project.
/// </remarks>
public static class FacadeMarker
{
    /// <summary>Singleton sentinel used by Harbor.Architecture.Tests to force-load this assembly.</summary>
    public static readonly Type Type = typeof(FacadeMarker);
}
