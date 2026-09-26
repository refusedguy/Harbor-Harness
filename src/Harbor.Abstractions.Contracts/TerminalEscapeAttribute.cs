using System;

namespace Harbor.Abstractions.Contracts;

/// <summary>
///     Marks an enum as a source of terminal escape-code constants.
///     <see cref="Harbor.CodeGen.EscapeCodeGenerator" /> scans for enums annotated with this
///     attribute and emits a static <c>EscapeCodes</c> class containing
///     precomputed <see cref="ReadOnlySpan{Byte}" /> values — zero heap
///     allocations on the hot path, AOT-safe.
/// </summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class TerminalEscapeAttribute : Attribute
{
    /// <summary>
    ///     Gets or sets the name of the generated static helper class.
    ///     Defaults to <c>"EscapeCodes"</c> if not specified.
    /// </summary>
    public string ClassName { get; set; } = "EscapeCodes";

    /// <summary>
    ///     Gets or sets the namespace for the generated class.
    ///     Defaults to the annotated enum's namespace if not specified.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    ///     Gets or sets the enum member that represents the reset code.
    ///     Used to wrap styled output. Defaults to <c>"Reset"</c>.
    /// </summary>
    public string ResetMember { get; set; } = "Reset";
}
