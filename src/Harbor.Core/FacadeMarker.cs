namespace Harbor.Core;
/// <summary>
///     Marker type that lives in Harbor.Core.dll so the assembly has at least
///     one type. Harbor.Core is a thin backward-compat facade after the S1
///     split — all its former types moved to Harbor.Application /
///     Harbor.Registries (kept in their original <c>Harbor.Core.*</c>
///     namespaces for source-compat). Without this marker, the assembly would
///     be empty and consumers/tests that load it by name (e.g. via
///     <c>typeof()</c> probes in Harbor.Architecture.Tests) would never force
///     it into the AppDomain, leaving it invisible to reflection-based layer
///     rules.
/// </summary>
/// <remarks>
///     This type intentionally has no members. It exists only to give the
///     Harbor.Core assembly a type identity. Do not add real types here — add
///     them to Harbor.Application or Harbor.Registries instead, in the
///     appropriate <c>Harbor.Core.*</c> namespace for backward compatibility.
/// </remarks>
public static class FacadeMarker
{
    /// <summary>Singleton sentinel used by Harbor.Architecture.Tests to force-load this assembly.</summary>
    public static readonly Type Type = typeof(FacadeMarker);
}
