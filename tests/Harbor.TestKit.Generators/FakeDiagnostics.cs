using Microsoft.CodeAnalysis;

namespace Harbor.TestKit.Generators;

/// <summary>
///     Diagnostics the test-double generator reports. Every one of them is an
///     <see cref="DiagnosticSeverity.Error" />: a shape the generator cannot
///     express must fail the build loudly, never emit half a double that
///     compiles into something silently wrong.
/// </summary>
internal static class FakeDiagnostics
{
    /// <summary>The opt-in list named a type that is not an interface.</summary>
    internal static readonly DiagnosticDescriptor NotAnInterface = new(
        id: "HARBORFAKE001",
        title: "The test-double generator can only fake interfaces",
        messageFormat: "'{0}' is not an interface, so no test double can be generated for it",
        category: "Harbor.TestKit.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The interface uses a shape the generator does not implement yet.</summary>
    internal static readonly DiagnosticDescriptor UnsupportedShape = new(
        id: "HARBORFAKE002",
        title: "The test-double generator does not support this interface shape",
        messageFormat: "'{0}' cannot be generated: {1}",
        category: "Harbor.TestKit.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>The same interface was listed twice.</summary>
    internal static readonly DiagnosticDescriptor DuplicateOptIn = new(
        id: "HARBORFAKE003",
        title: "The test-double generator was asked for the same interface twice",
        messageFormat: "'{0}' is listed more than once in the [GenerateFakes] opt-in list",
        category: "Harbor.TestKit.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>Two members of the interface would produce the same generated name.</summary>
    internal static readonly DiagnosticDescriptor DuplicateMember = new(
        id: "HARBORFAKE004",
        title: "The test-double generator would emit two members with the same name",
        messageFormat: "'{0}' has two members that both generate '{1}'; rename one of them",
        category: "Harbor.TestKit.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
