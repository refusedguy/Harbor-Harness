namespace Harbor.Tools.Builtin;

/// <summary>
///     One section of the read-only runtime catalog.
/// </summary>
/// <remarks>
///     <para>
///         The tool owns the section vocabulary and dispatches a request to the
///         inspector registered for that section. Adding a new inspectable entity
///         adds an inspector and one row in the tool's inspector table, instead
///         of another branch in the tool's own rendering switch (SRP/OCP).
///     </para>
///     <para>
///         An inspector is a projection, never a second registry: it reads the
///         registry or the fact snapshot that owns the fact, and it only reads.
///     </para>
/// </remarks>
internal interface ISectionInspector
{
    /// <summary>The section this inspector answers for.</summary>
    InspectTool.Section Section { get; }

    /// <summary>
    ///     Build the answer for one section. A failure is the "no such entry"
    ///     answer, and it names what IS there.
    /// </summary>
    Result<string> Describe(string? name, ToolContext context);
}
