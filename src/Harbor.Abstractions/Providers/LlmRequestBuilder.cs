using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Providers;

/// <summary>
///     Builder for <see cref="LlmRequest" /> (Builder pattern, GoF creational).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="LlmRequest" /> carries 4 required + 9 optional parameters.
///         Assembling it by hand at each call site forces every site to stay in
///         sync when a parameter (or a <see cref="CacheStrategy" /> case) is
///         added — the turn-request and the summary-request already drifted
///         apart on cache flags. The builder keeps one assembly process with
///         two preset representations (<see cref="ForTurn" /> /
///         <see cref="ForSummary" />), so a 14th parameter lands here once
///         instead of at every site.
///     </para>
///     <para>
///         Not thread-safe: configure on one thread, then call
///         <see cref="Build" />. The produced <see cref="LlmRequest" /> is
///         immutable and freely shareable.
///     </para>
/// </remarks>
public sealed class LlmRequestBuilder
{
    private ToolChoice? _toolChoice;
    private int? _maxOutputTokens;
    private decimal? _temperature;
    private decimal? _topP;
    private int? _topK;
    private ReasoningEffort? _reasoningEffort;
    private CacheStrategy _cacheStrategy = CacheStrategy.None;
    private IReadOnlyDictionary<string, string>? _extraHeaders;
    private string? _sessionAffinity;

    /// <summary>
    ///     Starts a request with the four required parameters.
    /// </summary>
    /// <param name="model">Model id (without provider prefix).</param>
    /// <param name="messages">Ordered list of messages forming the conversation.</param>
    /// <param name="systemPrompt">System prompt for the call.</param>
    /// <param name="tools">Tools available to the model.</param>
    public LlmRequestBuilder(
        string model,
        IReadOnlyList<LlmMessage> messages,
        string systemPrompt,
        IReadOnlyList<ToolDefinition> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(tools);
        Model = model;
        Messages = messages;
        SystemPrompt = systemPrompt;
        Tools = tools;
    }

    /// <summary>Model id (without provider prefix).</summary>
    public string Model { get; }

    /// <summary>Ordered list of messages forming the conversation.</summary>
    public IReadOnlyList<LlmMessage> Messages { get; }

    /// <summary>System prompt for the call.</summary>
    public string SystemPrompt { get; }

    /// <summary>Tools available to the model.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; }

    /// <summary>
    ///     Preset for the agent-loop turn request: model/temperature/reasoning
    ///     budget from the agent, output cap from the model, and an ephemeral
    ///     prefix-cache flag whenever the system prompt is non-empty (it is
    ///     stable across turns, so every request is a cache candidate).
    /// </summary>
    public static LlmRequestBuilder ForTurn(
        string model,
        IReadOnlyList<LlmMessage> messages,
        string systemPrompt,
        IReadOnlyList<ToolDefinition> tools,
        int? maxOutputTokens,
        decimal? temperature,
        ReasoningEffort? reasoningEffort) =>
        new LlmRequestBuilder(model, messages, systemPrompt, tools)
            .WithMaxOutputTokens(maxOutputTokens)
            .WithTemperature(temperature)
            .WithReasoningEffort(reasoningEffort)
            .WithCacheStrategy(
                systemPrompt.Length > 0 ? CacheStrategy.Ephemeral : CacheStrategy.None);

    /// <summary>
    ///     Preset for the compaction summary request: single user message with
    ///     the compaction prompt, fixed sampling temperature, fixed output cap,
    ///     and an ephemeral prefix-cache flag (the summarization system prompt
    ///     is a compile-time constant, so every request is a cache candidate).
    /// </summary>
    public static LlmRequestBuilder ForSummary(
        string model,
        string prompt,
        string systemPrompt,
        int maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return new LlmRequestBuilder(
                model,
                new[] { LlmUserMessage.Text(prompt) },
                systemPrompt,
                Array.Empty<ToolDefinition>())
            .WithTemperature(0.3m)
            .WithMaxOutputTokens(maxOutputTokens)
            .WithCacheStrategy(CacheStrategy.Ephemeral);
    }

    /// <summary>Sets the tool-choice strategy (<see langword="null" /> means model-default).</summary>
    public LlmRequestBuilder WithToolChoice(ToolChoice? toolChoice)
    {
        _toolChoice = toolChoice;
        return this;
    }

    /// <summary>Sets the cap on output tokens.</summary>
    public LlmRequestBuilder WithMaxOutputTokens(int? maxOutputTokens)
    {
        _maxOutputTokens = maxOutputTokens;
        return this;
    }

    /// <summary>Sets the sampling temperature.</summary>
    public LlmRequestBuilder WithTemperature(decimal? temperature)
    {
        _temperature = temperature;
        return this;
    }

    /// <summary>Sets the nucleus sampling probability.</summary>
    public LlmRequestBuilder WithTopP(decimal? topP)
    {
        _topP = topP;
        return this;
    }

    /// <summary>Sets the top-k sampling parameter.</summary>
    public LlmRequestBuilder WithTopK(int? topK)
    {
        _topK = topK;
        return this;
    }

    /// <summary>Sets the reasoning-effort hint for reasoning-aware models.</summary>
    public LlmRequestBuilder WithReasoningEffort(ReasoningEffort? reasoningEffort)
    {
        _reasoningEffort = reasoningEffort;
        return this;
    }

    /// <summary>Sets the prompt-caching strategy.</summary>
    public LlmRequestBuilder WithCacheStrategy(CacheStrategy cacheStrategy)
    {
        _cacheStrategy = cacheStrategy;
        return this;
    }

    /// <summary>Sets extra HTTP headers to send with the request.</summary>
    public LlmRequestBuilder WithExtraHeaders(IReadOnlyDictionary<string, string>? extraHeaders)
    {
        _extraHeaders = extraHeaders;
        return this;
    }

    /// <summary>Sets the session affinity hint for sticky routing on multi-region providers.</summary>
    public LlmRequestBuilder WithSessionAffinity(string? sessionAffinity)
    {
        _sessionAffinity = sessionAffinity;
        return this;
    }

    /// <summary>Builds the immutable request from the configured state.</summary>
    public LlmRequest Build() =>
        new(
            Model,
            Messages,
            SystemPrompt,
            Tools,
            _toolChoice,
            _maxOutputTokens,
            _temperature,
            _topP,
            _topK,
            _reasoningEffort,
            _cacheStrategy,
            _extraHeaders,
            _sessionAffinity);
}
