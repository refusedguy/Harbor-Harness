namespace Harbor.Abstractions.Models.Identifiers;
/// <summary>
///     Internal char-validation helpers for identifier normalization. Replaces per-call
///     <see cref="System.Text.RegularExpressions.Regex.IsMatch" /> on hot paths (every tool
///     call constructs a <see cref="ToolName" />; every provider lookup constructs a
///     <see cref="ProviderId" />).
/// </summary>
internal static class IdentifierValidation
{
    /// <summary>
    ///     Validate <c>^[a-z0-9][a-z0-9-]*$</c> for provider ids without allocating a Regex.
    /// </summary>
    public static bool IsValidProviderId(string value)
    {
        if (value.Length == 0) return false;
        if (!IsLowerOrDigit(value[0])) return false;
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (!IsLowerOrDigit(c) && c != '-') return false;
        }
        return true;
    }

    /// <summary>
    ///     Validate <c>^[a-z][a-z0-9_]*$</c> for tool names without allocating a Regex.
    /// </summary>
    public static bool IsValidToolName(string value)
    {
        if (value.Length == 0) return false;
        if (!IsLower(value[0])) return false;
        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (!IsLowerOrDigit(c) && c != '_') return false;
        }
        return true;
    }

    private static bool IsLower(char c) => (uint)(c - 'a') <= 'z' - 'a';
    private static bool IsLowerOrDigit(char c) => IsLower(c) || (uint)(c - '0') <= '9' - '0';
}

/// <summary>
///     Strongly-typed identifier for a session.
/// </summary>
/// <remarks>
///     Wraps a string value (typically a guid as N-string) to prevent accidental mixing with
///     other string-typed identifiers. Use <see cref="Create" /> for a throwing API or
///     <see cref="TryCreate" /> for a <see cref="Result" />-based API. Implicitly converts to
///     <see cref="string" /> for storage/serialization convenience.
/// </remarks>
public sealed class SessionId : ValueObject
{

    private SessionId(string value)
    {
        Value = value;
    }
    /// <summary>
    ///     The underlying string value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    ///     Construct a <see cref="SessionId" /> from a non-empty string. Throws if blank.
    /// </summary>
    /// <param name="value">The session id string.</param>
    /// <returns>A new <see cref="SessionId" />.</returns>
    public static SessionId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Session ID cannot be empty", nameof(value));

        return new SessionId(value);
    }

    /// <summary>
    ///     Generate a brand-new <see cref="SessionId" /> backed by a fresh guid.
    /// </summary>
    /// <returns>A new <see cref="SessionId" />.</returns>
    public static SessionId New() => Create(Guid.NewGuid().ToString("N"));

    /// <summary>
    ///     Try to construct a <see cref="SessionId" /> without throwing.
    /// </summary>
    /// <param name="value">The candidate string (may be null/blank).</param>
    /// <returns>Success with the new id, or failure with an error message.</returns>
    public static Result<SessionId> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<SessionId>("Session ID cannot be empty");

        return Result.Success(Create(value));
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    ///     Implicit conversion to <see cref="string" /> for storage/serialization convenience.
    /// </summary>
    /// <param name="id">The id to convert.</param>
    public static implicit operator string(SessionId id) => id.Value;
}

/// <summary>
///     Strongly-typed identifier for a message within a session.
/// </summary>
/// <remarks>
///     See <see cref="SessionId" /> for usage patterns.
/// </remarks>
public sealed class MessageId : ValueObject
{

    private MessageId(string value)
    {
        Value = value;
    }
    /// <summary>
    ///     The underlying string value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    ///     Construct a <see cref="MessageId" /> from a non-empty string. Throws if blank.
    /// </summary>
    /// <param name="value">The message id string.</param>
    /// <returns>A new <see cref="MessageId" />.</returns>
    public static MessageId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Message ID cannot be empty", nameof(value));

        return new MessageId(value);
    }

    /// <summary>
    ///     Generate a brand-new <see cref="MessageId" /> backed by a fresh guid.
    /// </summary>
    /// <returns>A new <see cref="MessageId" />.</returns>
    public static MessageId New() => Create(Guid.NewGuid().ToString("N"));

    /// <summary>
    ///     Try to construct a <see cref="MessageId" /> without throwing.
    /// </summary>
    /// <param name="value">The candidate string (may be null/blank).</param>
    /// <returns>Success with the new id, or failure with an error message.</returns>
    public static Result<MessageId> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<MessageId>("Message ID cannot be empty");

        return Result.Success(Create(value));
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    ///     Implicit conversion to <see cref="string" /> for storage/serialization convenience.
    /// </summary>
    /// <param name="id">The id to convert.</param>
    public static implicit operator string(MessageId id) => id.Value;
}

/// <summary>
///     Strongly-typed identifier for a tool call.
/// </summary>
/// <remarks>
///     Each <see cref="ToolCallPart" /> in an assistant message carries a <c>Id</c> that is later
///     matched against <see cref="ToolResultEntry.ToolCallId" /> in tool result messages.
/// </remarks>
public sealed class ToolCallId : ValueObject
{

    private ToolCallId(string value)
    {
        Value = value;
    }
    /// <summary>
    ///     The underlying string value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    ///     Construct a <see cref="ToolCallId" /> from a non-empty string. Throws if blank.
    /// </summary>
    /// <param name="value">The tool call id string.</param>
    /// <returns>A new <see cref="ToolCallId" />.</returns>
    public static ToolCallId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Tool call ID cannot be empty", nameof(value));

        return new ToolCallId(value);
    }

    /// <summary>
    ///     Generate a brand-new <see cref="ToolCallId" /> backed by a fresh guid.
    /// </summary>
    /// <returns>A new <see cref="ToolCallId" />.</returns>
    public static ToolCallId New() => Create(Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    ///     Implicit conversion to <see cref="string" /> for storage/serialization convenience.
    /// </summary>
    /// <param name="id">The id to convert.</param>
    public static implicit operator string(ToolCallId id) => id.Value;
}

/// <summary>
///     Strongly-typed identifier for an LLM provider.
/// </summary>
/// <remarks>
///     Provider ids are normalized to lowercase and must match <c>^[a-z0-9][a-z0-9-]*$</c>.
/// </remarks>
public sealed class ProviderId : ValueObject
{

    private ProviderId(string value)
    {
        Value = value;
    }
    /// <summary>
    ///     The normalized (lowercase) string value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    ///     Construct a <see cref="ProviderId" /> from a non-empty string. Normalizes to lowercase
    ///     and validates the format. Throws on invalid input.
    /// </summary>
    /// <param name="value">The provider id string (e.g. <c>anthropic</c>, <c>openai</c>).</param>
    /// <returns>A new <see cref="ProviderId" />.</returns>
    public static ProviderId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Provider ID cannot be empty", nameof(value));

        string normalized = value.ToLowerInvariant();
        if (!IdentifierValidation.IsValidProviderId(normalized))
            throw new ArgumentException($"Provider ID '{value}' contains invalid characters", nameof(value));

        return new ProviderId(normalized);
    }

    /// <summary>
    ///     Try to construct a <see cref="ProviderId" /> without throwing.
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

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    ///     Implicit conversion to <see cref="string" /> for storage/serialization convenience.
    /// </summary>
    /// <param name="id">The id to convert.</param>
    public static implicit operator string(ProviderId id) => id.Value;
}

/// <summary>
///     Strongly-typed reference to a model within a provider.
/// </summary>
/// <remarks>
///     A <see cref="ModelRef" /> is the canonical "provider/model" string (e.g.
///     <c>anthropic/claude-opus-4</c>). Use <see cref="TryParse" /> to parse from user input.
/// </remarks>
public sealed class ModelRef : ValueObject
{

    private ModelRef(ProviderId providerId, string modelId)
    {
        ProviderId = providerId;
        ModelId = modelId;
    }
    /// <summary>
    ///     The provider component of the reference.
    /// </summary>
    public ProviderId ProviderId { get; }

    /// <summary>
    ///     The model id component of the reference (without provider prefix).
    /// </summary>
    public string ModelId { get; }

    /// <summary>
    ///     Construct a <see cref="ModelRef" /> from a provider and model id.
    /// </summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="modelId">The model id (must be non-empty).</param>
    /// <returns>A new <see cref="ModelRef" />.</returns>
    public static ModelRef Create(ProviderId providerId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model ID cannot be empty", nameof(modelId));

        return new ModelRef(providerId, modelId);
    }

    /// <summary>
    ///     Parse from "provider/model" string (e.g. "anthropic/claude-opus-4").
    /// </summary>
    /// <param name="value">The candidate string (may be null/blank).</param>
    /// <returns>Success with the parsed <see cref="ModelRef" />, or failure with an error message.</returns>
    public static Result<ModelRef> TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<ModelRef>("Model reference cannot be empty");

        string[] parts = value.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return Result.Failure<ModelRef>($"Invalid model reference '{value}'. Expected 'provider/model'.");

        var providerResult = ProviderId.TryCreate(parts[0]);
        if (providerResult.IsFailure)
            return providerResult.ConvertFailure<ModelRef>();

        // A Try-method must never throw: "provider/ " survives the split
        // (whitespace is not empty) but Create rejects blank model ids.
        if (string.IsNullOrWhiteSpace(parts[1]))
            return Result.Failure<ModelRef>($"Invalid model reference '{value}'. Expected 'provider/model'.");

        return Result.Success(Create(providerResult.Value, parts[1].Trim()));
    }

    /// <summary>
    ///     Resolve a model selection against a provider the caller ALREADY knows —
    ///     the provider it picked, or the one it read out of config. The model half
    ///     may be a bare model id or carry a redundant <c>provider/</c> prefix, and
    ///     this is the one function allowed to tell the two apart.
    /// </summary>
    /// <param name="providerId">The provider the model belongs to. Normalized and validated.</param>
    /// <param name="modelId">
    ///     The model half: a bare id (<c>"gpt-4o"</c>), a multi-segment bare id
    ///     (<c>"tencent/hy3:free"</c>), or a redundant prefix for this same provider
    ///     (<c>"kilocode/tencent/hy3:free"</c> — what <c>HARBOR_MODEL</c> and the
    ///     settings screen write). Whitespace is trimmed.
    /// </param>
    /// <returns>
    ///     Success with the reference, or failure when the provider id is invalid or
    ///     the model half is blank.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         #678: two call sites re-derived this by hand — building
    ///         <c>prefix = provider + "/"</c> and stripping it with
    ///         <c>StartsWith</c> (<c>SessionFactory</c> and
    ///         <c>ToolsCatalog</c>) — and they did not agree with
    ///         <see cref="TryParse" />: the hand-rolled version left the provider
    ///         string RAW and unvalidated, so <c>"KiloCode"</c> reached a running
    ///         session unnormalized while this normalizes it. Both also had to be
    ///         taught about multi-segment model ids by hand, which is the mistake
    ///         #599 was filed for.
    ///     </para>
    ///     <para>
    ///         <b>Why the prefix is dropped only when it matches THIS provider</b>,
    ///         and why that is not a special case to be folded into
    ///         <see cref="TryParse" />: <c>"tencent/hy3:free"</c> is kilocode's own
    ///         DEFAULT model id, and a model id may contain slashes. So the slash
    ///         alone never means "already qualified" — only a prefix naming the
    ///         provider the caller already chose is redundant. Reading the first
    ///         segment of a bare multi-segment id as its provider is the exact bug
    ///         that would turn the default install into a nonexistent "tencent"
    ///         provider.
    ///     </para>
    ///     <para>
    ///         The sibling rule — <b>a value that is already a reference wins over
    ///         the provider the caller had in mind</b> — is deliberately NOT this
    ///         method. That is the free-text rule (the <c>/model</c> command, the
    ///         onboarding wizard): someone typing <c>otherprov/some-model</c> named a
    ///         provider on purpose, so callers there run
    ///         <see cref="TryParse" /> first and fall back to this method. Keeping
    ///         the two apart here is what stops one of them from being "unified" into
    ///         the other.
    ///     </para>
    /// </remarks>
    public static Result<ModelRef> Qualify(string? providerId, string? modelId)
        => ProviderId.TryCreate(providerId).Bind(provider => QualifyUnder(provider, modelId));

    /// <summary>
    ///     <see cref="Qualify" /> once the provider id is already typed. Private so
    ///     the composition stays a single expression and the public surface has one
    ///     entry point.
    /// </summary>
    private static Result<ModelRef> QualifyUnder(ProviderId provider, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return Result.Failure<ModelRef>("Model reference cannot be empty");

        string candidate = modelId.Trim();

        // A property pattern, not `parsed.Value`: CFE0001 does not model the
        // early-return guard shape, and this file must stay free of call-site
        // pragmas.
        if (TryParse(candidate) is { IsSuccess: true, Value: var reference }
            && reference.ProviderId.Value == provider.Value)
        {
            return Result.Success(reference);
        }

        return Result.Success(Create(provider, candidate));
    }

    /// <inheritdoc />
    public override string ToString() => $"{ProviderId}/{ModelId}";

    /// <inheritdoc />
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return ProviderId.Value;
        yield return ModelId;
    }

    /// <summary>
    ///     Implicit conversion to <see cref="string" /> producing the <c>provider/model</c> form.
    /// </summary>
    /// <param name="id">The reference to convert.</param>
    public static implicit operator string(ModelRef id) => id.ToString();
}

/// <summary>
///     Strongly-typed tool name.
/// </summary>
/// <remarks>
///     Tool names are normalized to lowercase and must match <c>^[a-z][a-z0-9_]*$</c>.
/// </remarks>
public sealed class ToolName : ValueObject
{

    private ToolName(string value)
    {
        Value = value;
    }
    /// <summary>
    ///     The normalized (lowercase) string value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    ///     Construct a <see cref="ToolName" /> from a non-empty string. Normalizes to lowercase
    ///     and validates the format. Throws on invalid input.
    /// </summary>
    /// <param name="value">The tool name string (e.g. <c>read</c>, <c>bash</c>).</param>
    /// <returns>A new <see cref="ToolName" />.</returns>
    public static ToolName Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Tool name cannot be empty", nameof(value));

        string normalized = value.ToLowerInvariant();
        if (!IdentifierValidation.IsValidToolName(normalized))
            throw new ArgumentException($"Tool name '{value}' must match ^[a-z][a-z0-9_]*$", nameof(value));

        return new ToolName(normalized);
    }

    /// <summary>
    ///     Try to construct a <see cref="ToolName" /> without throwing.
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

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    ///     Implicit conversion to <see cref="string" /> for storage/serialization convenience.
    /// </summary>
    /// <param name="name">The name to convert.</param>
    public static implicit operator string(ToolName name) => name.Value;
}

/// <summary>
///     Strongly-typed agent name.
/// </summary>
/// <remarks>
///     Agent names are normalized to lowercase but otherwise unrestricted.
/// </remarks>
public sealed class AgentName : ValueObject
{
    /// <summary>
    ///     The agent a session gets when nobody named one — the one place in the
    ///     repository where that answer is spelled.
    /// </summary>
    /// <remarks>
    ///     #683: this answer was written in three places inside
    ///     <c>Harbor.Ui.Framework.Sessions</c>, and one of the three
    ///     (<c>SessionFactory.CreateDefaultAsync</c>) did not apply it at all — it took
    ///     <c>GetAllAgents().FirstOrDefault()</c>, so the default agent was whichever
    ///     entry a <c>ConcurrentDictionary</c> happened to yield. The two others spelled
    ///     the literal <c>"code"</c> next to their lookup, and agreed with the first only
    ///     by coincidence.
    ///     <para>
    ///         The constant lives HERE, in the innermost layer, rather than in
    ///         <c>IdentityConfig</c> (<c>Harbor.Application</c>) where the other copy
    ///         sits: <c>Harbor.Ui.Framework.Sessions</c> is an outer layer and may not
    ///         reference <c>Harbor.Application</c> (docs/ARCHITECTURE_LAYERS.md), so a
    ///         constant declared there is unreachable from the code that has to read it.
    ///         <see cref="IdentityConfig.FallbackAgent" /> is now derived from this.
    ///     </para>
    /// </remarks>
    public const string Fallback = "code";

    private AgentName(string value)
    {
        Value = value;
    }
    /// <summary>
    ///     The normalized (lowercase) string value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    ///     Construct an <see cref="AgentName" /> from a non-empty string. Normalizes to lowercase.
    /// </summary>
    /// <param name="value">The agent name string (e.g. <c>code</c>, <c>plan</c>).</param>
    /// <returns>A new <see cref="AgentName" />.</returns>
    public static AgentName Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Agent name cannot be empty", nameof(value));

        return new AgentName(value.ToLowerInvariant());
    }

    /// <summary>
    ///     Try to construct an <see cref="AgentName" /> without throwing.
    /// </summary>
    /// <param name="value">The candidate string (may be null/blank).</param>
    /// <returns>Success with the new name, or failure with an error message.</returns>
    public static Result<AgentName> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<AgentName>("Agent name cannot be empty");

        return Result.Success(Create(value));
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    ///     Implicit conversion to <see cref="string" /> for storage/serialization convenience.
    /// </summary>
    /// <param name="name">The name to convert.</param>
    public static implicit operator string(AgentName name) => name.Value;
}

/// <summary>
///     Strongly-typed identifier for a single agent run (epic #41, slice B1).
/// </summary>
/// <remarks>
///     <para>
///         One run = one workspace + one agent + one <see cref="RunId" />, from
///         <c>AgentStartEvent</c> through turns (<c>TurnStartEvent</c> /
///         <c>TurnEndEvent</c>) to the terminal <c>AgentEndEvent</c> (or
///         <c>AgentErrorEvent</c>). The id is minted at run start and travels
///         with the run's messages so any finished run can be reconstructed
///         from the session store (see <see cref="Harbor.Abstractions.Models.RunOutcome" />).
///     </para>
///     <para>See <see cref="SessionId" /> for usage patterns.</para>
/// </remarks>
public sealed class RunId : ValueObject
{

    private RunId(string value)
    {
        Value = value;
    }
    /// <summary>
    ///     The underlying string value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    ///     Construct a <see cref="RunId" /> from a non-empty string. Throws if blank.
    /// </summary>
    /// <param name="value">The run id string.</param>
    /// <returns>A new <see cref="RunId" />.</returns>
    public static RunId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Run ID cannot be empty", nameof(value));

        return new RunId(value);
    }

    /// <summary>
    ///     Generate a brand-new <see cref="RunId" /> backed by a fresh guid.
    /// </summary>
    /// <returns>A new <see cref="RunId" />.</returns>
    public static RunId New() => Create(Guid.NewGuid().ToString("N"));

    /// <summary>
    ///     Try to construct a <see cref="RunId" /> without throwing.
    /// </summary>
    /// <param name="value">The candidate string (may be null/blank).</param>
    /// <returns>Success with the new id, or failure with an error message.</returns>
    public static Result<RunId> TryCreate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<RunId>("Run ID cannot be empty");

        return Result.Success(Create(value));
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <summary>
    ///     Implicit conversion to <see cref="string" /> for storage/serialization convenience.
    /// </summary>
    /// <param name="id">The id to convert.</param>
    public static implicit operator string(RunId id) => id.Value;
}
