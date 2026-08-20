namespace Harbor.Tools.Builtin;

/// <summary>
///     Marker type that lives in Harbor.Tools.Builtin.dll so the assembly has
///     at least one type visible to reflection-based architecture tests.
///     Harbor.Tools.Builtin is a consolidated facade (S2 merge of 14 leaf tool
///     projects), and without this marker the assembly would be empty and
///     consumers/tests that load it by name would never force it into the
///     AppDomain.
/// </summary>
public static class FacadeMarker
{
    /// <summary>Singleton sentinel used by Harbor.Architecture.Tests to force-load this assembly.</summary>
    public static readonly Type Type = typeof(FacadeMarker);
}
