using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Models;

/// <summary>
///     An isolation scope: a named realm in which a service may have its own
///     implementation, without disturbing the realm every other caller sees.
/// </summary>
/// <remarks>
///     <para>
///         Issue #1736. Harbor resolves every service from ONE process-wide
///         registry: one <c>IToolRegistry</c>, one provider table, one
///         <c>ISessionStore</c>. A scope is the missing axis — a realm in which
///         the implementation of a service may differ from the default one,
///         while the default one keeps serving everybody else.
///     </para>
///     <para>
///         A scope is a LABEL, not an object graph. It carries no state of its
///         own; it names the realm a lookup happens in. Two callers in the
///         same scope see the same implementation; a caller in another scope
///         may see a different one.
///     </para>
///     <para>
///         The scope names are normalized to lowercase and must match
///         <c>^[a-z0-9][a-z0-9._-]*$</c> — the same shape the other identifiers
///         in this assembly use, so a scope can be written in a config file
///         without a second spelling of the same realm existing.
///     </para>
/// </remarks>
public sealed record ServiceScope
{
    private ServiceScope(string value)
    {
        Value = value;
    }

    /// <summary>
    ///     The normalized (lowercase) scope label.
    /// </summary>
    public string Value { get; }

    /// <summary>
    ///     The scope every caller sees unless a narrower scope declares its own
    ///     implementation of the service. It is the realm the process-wide
    ///     registries live in, so an empty table of scoped bindings changes
    ///     nothing: every lookup lands on the default implementation.
    /// </summary>
    public static ServiceScope Default { get; } = new("default");

    /// <summary>
    ///     Construct a <see cref="ServiceScope" /> from a non-empty string. Normalizes to
    ///     lowercase and validates the format. Throws on invalid input.
    /// </summary>
    /// <param name="value">The scope label (e.g. <c>ci</c>, <c>plan</c>, <c>dump</c>).</param>
    /// <returns>A new <see cref="ServiceScope" />.</returns>
    public static ServiceScope Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Service scope cannot be empty", nameof(value));

        string normalized = value.ToLowerInvariant();
        if (!IdentifierValidation.IsValidServiceScope(normalized))
            throw new ArgumentException($"Service scope '{value}' contains invalid characters", nameof(value));

        return new ServiceScope(normalized);
    }

    /// <summary>
    ///     Try to construct a <see cref="ServiceScope" /> without throwing.
    /// </summary>
    /// <param name="value">The candidate label (may be null/blank).</param>
    /// <returns>Success with the new scope, or failure with an error message.</returns>
    public static Result<ServiceScope> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<ServiceScope>("Service scope cannot be empty");

        try
        {
            return Result.Success(Create(value));
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<ServiceScope>(ex.Message);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    ///     Implicit conversion to <see cref="string" /> for storage/serialization convenience.
    /// </summary>
    /// <param name="scope">The scope to convert.</param>
    public static implicit operator string(ServiceScope scope) => scope.Value;
}
