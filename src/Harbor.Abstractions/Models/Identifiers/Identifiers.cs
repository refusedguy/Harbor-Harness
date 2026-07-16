using CSharpFunctionalExtensions;

namespace Harbor.Abstractions.Models.Identifiers;

/// <summary>
/// Strongly-typed identifier for a session.
/// </summary>
/// <remarks>
/// Wraps a string value (typically a guid as N-string) to prevent accidental mixing with
/// other string-typed identifiers. Use <see cref="Create"/> for a throwing API or
/// <see cref="TryCreate"/> for a <see cref="Result"/>-based API. Implicitly converts to
/// <see cref="string"/> for storage/serialization convenience.
/// </remarks>
public sealed class SessionId : ValueObject
{
    /// <summary>
    /// The underlying string value.
    /// </summary>
    public string Value { get; }

    private SessionId(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Construct a <see cref="SessionId"/> from a non-empty string. Throws if blank.
    /// </summary>
    /// <param name="value">The session id string.</param>
    /// <returns>A new <see cref="SessionId"/>.</returns>
    public static SessionId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Session ID cannot be empty", nameof(value));

        return new SessionId(value);
    }

    /// <summary>
    /// Generate a brand-new <see cref="SessionId"/> backed by a fresh guid.
    /// </summary>
    /// <returns>A new <see cref="SessionId"/>.</returns>
    public static SessionId New() => Create(Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Try to construct a <see cref="SessionId"/> without throwing.
    /// </summary>
    /// <param name="value">The candidate string (may be null/blank).</param>
    /// <returns>Success with the new id, or failure with an error message.</returns>
    public static Result<SessionId> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<SessionId>("Session ID cannot be empty");

        return Result.Success(Create(value));
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <inheritdoc/>
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    /// Implicit conversion to <see cref="string"/> for storage/serialization convenience.
    /// </summary>
    /// <param name="id">The id to convert.</param>
    public static implicit operator string(SessionId id) => id.Value;
}

/// <summary>
/// Strongly-typed identifier for a message within a session.
/// </summary>
/// <remarks>
/// See <see cref="SessionId"/> for usage patterns.
/// </remarks>
public sealed class MessageId : ValueObject
{
    /// <summary>
    /// The underlying string value.
    /// </summary>
    public string Value { get; }

    private MessageId(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Construct a <see cref="MessageId"/> from a non-empty string. Throws if blank.
    /// </summary>
    /// <param name="value">The message id string.</param>
    /// <returns>A new <see cref="MessageId"/>.</returns>
    public static MessageId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Message ID cannot be empty", nameof(value));

        return new MessageId(value);
    }

    /// <summary>
    /// Generate a brand-new <see cref="MessageId"/> backed by a fresh guid.
    /// </summary>
    /// <returns>A new <see cref="MessageId"/>.</returns>
    public static MessageId New() => Create(Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Try to construct a <see cref="MessageId"/> without throwing.
    /// </summary>
    /// <param name="value">The candidate string (may be null/blank).</param>
    /// <returns>Success with the new id, or failure with an error message.</returns>
    public static Result<MessageId> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<MessageId>("Message ID cannot be empty");

        return Result.Success(Create(value));
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <inheritdoc/>
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    /// Implicit conversion to <see cref="string"/> for storage/serialization convenience.
    /// </summary>
    /// <param name="id">The id to convert.</param>
    public static implicit operator string(MessageId id) => id.Value;
}

/// <summary>
/// Strongly-typed identifier for a tool call.
/// </summary>
/// <remarks>
/// Each <see cref="ToolCallPart"/> in an assistant message carries a <c>Id</c> that is later
/// matched against <see cref="ToolResultEntry.ToolCallId"/> in tool result messages.
/// </remarks>
public sealed class ToolCallId : ValueObject
{
    /// <summary>
    /// The underlying string value.
    /// </summary>
    public string Value { get; }

    private ToolCallId(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Construct a <see cref="ToolCallId"/> from a non-empty string. Throws if blank.
    /// </summary>
    /// <param name="value">The tool call id string.</param>
    /// <returns>A new <see cref="ToolCallId"/>.</returns>
    public static ToolCallId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Tool call ID cannot be empty", nameof(value));

        return new ToolCallId(value);
    }

    /// <summary>
    /// Generate a brand-new <see cref="ToolCallId"/> backed by a fresh guid.
    /// </summary>
    /// <returns>A new <see cref="ToolCallId"/>.</returns>
    public static ToolCallId New() => Create(Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <inheritdoc/>
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    /// Implicit conversion to <see cref="string"/> for storage/serialization convenience.
    /// </summary>
    /// <param name="id">The id to convert.</param>
    public static implicit operator string(ToolCallId id) => id.Value;
}

/// <summary>
/// Strongly-typed identifier for an LLM provider.
/// </summary>
/// <remarks>
/// Provider ids are normalized to lowercase and must match <c>^[a-z0-9][a-z0-9-]*$</c>.
/// </remarks>
public sealed class ProviderId : ValueObject
{
    /// <summary>
    /// The normalized (lowercase) string value.
    /// </summary>
    public string Value { get; }

    private ProviderId(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Construct a <see cref="ProviderId"/> from a non-empty string. Normalizes to lowercase
    /// and validates the format. Throws on invalid input.
    /// </summary>
    /// <param name="value">The provider id string (e.g. <c>anthropic</c>, <c>openai</c>).</param>
    /// <returns>A new <see cref="ProviderId"/>.</returns>
    public static ProviderId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Provider ID cannot be empty", nameof(value));

        var normalized = value.ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^[a-z0-9][a-z0-9-]*$"))
            throw new ArgumentException($"Provider ID '{value}' contains invalid characters", nameof(value));

        return new ProviderId(normalized);
    }

    /// <summary>
    /// Try to construct a <see cref="ProviderId"/> without throwing.
    /// </summary>
    /// <param name="value">The candidate string (may be null/blank).</param>
    /// <returns>Success with the new id, or failure with an error message.</returns>
    public static Result<ProviderId> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<ProviderId>("Provider ID cannot be empty");

        try
        {
            return Result.Success(Create(value));
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<ProviderId>(ex.Message);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <inheritdoc/>
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    /// Implicit conversion to <see cref="string"/> for storage/serialization convenience.
    /// </summary>
    /// <param name="id">The id to convert.</param>
    public static implicit operator string(ProviderId id) => id.Value;
}

/// <summary>
/// Strongly-typed reference to a model within a provider.
/// </summary>
/// <remarks>
/// A <see cref="ModelRef"/> is the canonical "provider/model" string (e.g.
/// <c>anthropic/claude-opus-4</c>). Use <see cref="TryParse"/> to parse from user input.
/// </remarks>
public sealed class ModelRef : ValueObject
{
    /// <summary>
    /// The provider component of the reference.
    /// </summary>
    public ProviderId ProviderId { get; }

    /// <summary>
    /// The model id component of the reference (without provider prefix).
    /// </summary>
    public string ModelId { get; }

    private ModelRef(ProviderId providerId, string modelId)
    {
        ProviderId = providerId;
        ModelId = modelId;
    }

    /// <summary>
    /// Construct a <see cref="ModelRef"/> from a provider and model id.
    /// </summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="modelId">The model id (must be non-empty).</param>
    /// <returns>A new <see cref="ModelRef"/>.</returns>
    public static ModelRef Create(ProviderId providerId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model ID cannot be empty", nameof(modelId));

        return new ModelRef(providerId, modelId);
    }

    /// <summary>
    /// Parse from "provider/model" string (e.g. "anthropic/claude-opus-4").
    /// </summary>
    /// <param name="value">The candidate string (may be null/blank).</param>
    /// <returns>Success with the parsed <see cref="ModelRef"/>, or failure with an error message.</returns>
    public static Result<ModelRef> TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<ModelRef>("Model reference cannot be empty");

        var parts = value.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return Result.Failure<ModelRef>($"Invalid model reference '{value}'. Expected 'provider/model'.");

        var providerResult = ProviderId.TryCreate(parts[0]);
        if (providerResult.IsFailure)
            return Result.Failure<ModelRef>(providerResult.Error);

        return Result.Success(Create(providerResult.Value, parts[1].Trim()));
    }

    /// <inheritdoc/>
    public override string ToString() => $"{ProviderId}/{ModelId}";

    /// <inheritdoc/>
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return ProviderId.Value;
        yield return ModelId;
    }

    /// <summary>
    /// Implicit conversion to <see cref="string"/> producing the <c>provider/model</c> form.
    /// </summary>
    /// <param name="id">The reference to convert.</param>
    public static implicit operator string(ModelRef id) => id.ToString();
}

/// <summary>
/// Strongly-typed tool name.
/// </summary>
/// <remarks>
/// Tool names are normalized to lowercase and must match <c>^[a-z][a-z0-9_]*$</c>.
/// </remarks>
public sealed class ToolName : ValueObject
{
    /// <summary>
    /// The normalized (lowercase) string value.
    /// </summary>
    public string Value { get; }

    private ToolName(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Construct a <see cref="ToolName"/> from a non-empty string. Normalizes to lowercase
    /// and validates the format. Throws on invalid input.
    /// </summary>
    /// <param name="value">The tool name string (e.g. <c>read</c>, <c>bash</c>).</param>
    /// <returns>A new <see cref="ToolName"/>.</returns>
    public static ToolName Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Tool name cannot be empty", nameof(value));

        var normalized = value.ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^[a-z][a-z0-9_]*$"))
            throw new ArgumentException($"Tool name '{value}' must match ^[a-z][a-z0-9_]*$", nameof(value));

        return new ToolName(normalized);
    }

    /// <summary>
    /// Try to construct a <see cref="ToolName"/> without throwing.
    /// </summary>
    /// <param name="value">The candidate string (may be null/blank).</param>
    /// <returns>Success with the new name, or failure with an error message.</returns>
    public static Result<ToolName> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<ToolName>("Tool name cannot be empty");

        try
        {
            return Result.Success(Create(value));
        }
        catch (ArgumentException ex)
        {
            return Result.Failure<ToolName>(ex.Message);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <inheritdoc/>
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    /// Implicit conversion to <see cref="string"/> for storage/serialization convenience.
    /// </summary>
    /// <param name="name">The name to convert.</param>
    public static implicit operator string(ToolName name) => name.Value;
}

/// <summary>
/// Strongly-typed agent name.
/// </summary>
/// <remarks>
/// Agent names are normalized to lowercase but otherwise unrestricted.
/// </remarks>
public sealed class AgentName : ValueObject
{
    /// <summary>
    /// The normalized (lowercase) string value.
    /// </summary>
    public string Value { get; }

    private AgentName(string value)
    {
        Value = value;
    }

    /// <summary>
    /// Construct an <see cref="AgentName"/> from a non-empty string. Normalizes to lowercase.
    /// </summary>
    /// <param name="value">The agent name string (e.g. <c>code</c>, <c>plan</c>).</param>
    /// <returns>A new <see cref="AgentName"/>.</returns>
    public static AgentName Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Agent name cannot be empty", nameof(value));

        return new AgentName(value.ToLowerInvariant());
    }

    /// <summary>
    /// Try to construct an <see cref="AgentName"/> without throwing.
    /// </summary>
    /// <param name="value">The candidate string (may be null/blank).</param>
    /// <returns>Success with the new name, or failure with an error message.</returns>
    public static Result<AgentName> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<AgentName>("Agent name cannot be empty");

        return Result.Success(Create(value));
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <inheritdoc/>
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    /// Implicit conversion to <see cref="string"/> for storage/serialization convenience.
    /// </summary>
    /// <param name="name">The name to convert.</param>
    public static implicit operator string(AgentName name) => name.Value;
}
