using System.Runtime.CompilerServices;

namespace Harbor.Abstractions.Extensions;

/// <summary>
///     Argument guard helpers (#1553): thin wrappers over the BCL throw helpers
///     with a single call shape. Behavior-identical by construction — each method
///     forwards to the BCL helper it replaces, preserving the exception type,
///     message, and parameter name via <see cref="CallerArgumentExpressionAttribute" />.
/// </summary>
public static class Ensure
{
    /// <summary>
    ///     Replaces <see cref="ArgumentNullException.ThrowIfNull(object?, string?)" />.
    ///     Returns the value so guards can inline into assignments.
    /// </summary>
    public static T NotNull<T>(T? value, [CallerArgumentExpression("value")] string? name = null)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        return value!;
    }

    /// <summary>
    ///     Replaces <see cref="ArgumentException.ThrowIfNullOrWhiteSpace(string?, string?)" />.
    ///     Returns the value so guards can inline into assignments.
    /// </summary>
    public static string NotBlank(string? value, [CallerArgumentExpression("value")] string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value!;
    }

    /// <summary>
    ///     Replaces <see cref="ArgumentException.ThrowIfNullOrEmpty(string?, string?)" />.
    ///     Returns the value so guards can inline into assignments.
    /// </summary>
    public static string NotEmpty(string? value, [CallerArgumentExpression("value")] string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        return value!;
    }
}
