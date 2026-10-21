using Microsoft.CodeAnalysis;

namespace Harbor.CodeGen;

/// <summary>
///     The diagnostics the <c>[Observed]</c> generator reports.
/// </summary>
/// <remarks>
///     <para>
///         The errors are the ones where the generator would otherwise have to
///         GUESS: a statement that asks for two different things at once, two
///         policies that disagree, a seam shape the generator cannot express.
///         Each of those fails the build loudly instead of emitting something
///         that compiles into the wrong behaviour.
///     </para>
///     <para>
///         The warnings are the ones where the generator knows what it is
///         doing and says so: a member it forwards WITHOUT instrumenting, and a
///         class seam this slice does not cover. A seam that is silently
///         uninstrumented is the failure mode the whole mechanism exists to
///         remove, so the generator never drops one without saying so.
///     </para>
/// </remarks>
internal static class ObservedDiagnostics
{
    /// <summary>The category every observed diagnostic carries.</summary>
    private const string Category = "Harbor.Observed";

    /// <summary>
    ///     A statement asks for nothing AND for a named span or instrument at
    ///     the same time.
    /// </summary>
    internal static readonly DiagnosticDescriptor ScopeContradiction = new(
        id: "HARBOROBS001",
        title: "The observed scope contradicts a named instrument",
        messageFormat: "'{0}' declares Scope = ObservedScope.None together with '{1}'; the two ask for opposite things, so the generator refuses to choose",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>Two policy markers in one namespace disagree.</summary>
    internal static readonly DiagnosticDescriptor PolicyConflict = new(
        id: "HARBOROBS002",
        title: "Two observed policies disagree in the same namespace",
        messageFormat: "the namespace '{0}' carries two observed policies that say different things ('{1}' and '{2}'); the generator refuses to pick one",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The seam uses a shape the generator does not emit.</summary>
    internal static readonly DiagnosticDescriptor UnsupportedShape = new(
        id: "HARBOROBS003",
        title: "The observed generator does not support this seam shape",
        messageFormat: "'{0}' cannot be observed: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>A member is forwarded without being instrumented.</summary>
    internal static readonly DiagnosticDescriptor NotInstrumented = new(
        id: "HARBOROBS004",
        title: "This member is forwarded without instrumentation",
        messageFormat: "'{0}' is forwarded as it is: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>A class carries the marker, and this slice decorates interfaces only.</summary>
    internal static readonly DiagnosticDescriptor ClassSeamNotInstrumented = new(
        id: "HARBOROBS005",
        title: "A class seam is not instrumented by this slice",
        messageFormat: "'{0}' carries [Observed], and the generator decorates INTERFACES only; the class seam is not instrumented",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>The generated decorator would collide with a type that already exists.</summary>
    internal static readonly DiagnosticDescriptor NameCollision = new(
        id: "HARBOROBS006",
        title: "The generated decorator collides with a type that already exists",
        messageFormat: "'{0}' already exists in '{1}', so the generator cannot emit the observed decorator for '{2}' under that name",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
