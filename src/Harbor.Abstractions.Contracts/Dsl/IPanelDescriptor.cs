using System;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     One rule in the ordered panel-descriptor chain: it declares the state
///     type it was written for, decides whether it applies to a state, and
///     builds the panel for a matched state.
/// </summary>
/// <remarks>
///     The shape follows Avalonia's <c>IDataTemplate</c>: <see cref="Match" />
///     picks a rule, <see cref="Build" /> produces the content, and an ordered
///     list of rules replaces a type switch over the state. The generator in
///     <c>Harbor.Tui.CellForge.CodeGen</c> rejects a class that implements this
///     interface without declaring <see cref="StateType" />, the way Avalonia's
///     template collection rejects a typed template without a data type.
///     <para>
///         The contract uses only BCL types. <see cref="StateType" /> is meant to
///         be a <c>typeof</c> expression, so matching needs no dynamic code and
///         the interface is safe in NativeAOT builds.
///     </para>
/// </remarks>
public interface IPanelDescriptor
{
    /// <summary>The state type this descriptor declares.</summary>
    Type StateType { get; }

    /// <summary>Whether this descriptor handles <paramref name="state"/>.</summary>
    /// <param name="state">State to test.</param>
    /// <returns><see langword="true"/> when this rule applies.</returns>
    bool Match(object state);

    /// <summary>Builds the panel content for a matched state.</summary>
    /// <param name="state">State this descriptor matched.</param>
    /// <returns>
    ///     Content in the form the host renders. The CellForge host accepts a
    ///     <see cref="string"/> or a list of strings; that is the same return
    ///     shape as <c>IPanelProvider.Build</c>.
    /// </returns>
    object? Build(object state);
}
