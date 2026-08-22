using Harbor.Abstractions.Extensions;
namespace Harbor.Core.Agents;
/// <summary>
///     A tool call whose accumulated args JSON failed to parse. The call is
///     never materialized as an executable <see cref="ToolCallPart" />; the
///     agent loop converts it into an error tool_result so the model can
///     retry with well-formed arguments next turn.
/// </summary>
/// <param name="Id">The provider-side tool call id.</param>
/// <param name="ToolName">The requested tool name.</param>
/// <param name="RawArgsTail">Tail of the raw accumulated args JSON, for diagnostics.</param>
internal sealed record MalformedToolCall(string Id, string ToolName, string RawArgsTail);

/// <summary>
///     Coalesces streaming LLM events (text deltas, thinking deltas,
///     tool-call start/delta fragments) into buffer state that the
///     <see cref="AgentLoop" /> can flush into a single
///     <see cref="AssistantMessage" /> per turn. Extracted from
///     <see cref="AgentLoop" /> (Task R32 god-object decomposition) so the
///     loop can focus on orchestration while this class owns the
///     buffer-management + flush semantics.
/// </summary>
/// <remarks>
///     <para>
///         <b>Performance:</b> uses pooled <c>StringBuilder</c>s for the
///         text + thinking buffers (sized 4 KB / 1 KB respectively) and a
///         per-tool-call pooled <c>StringBuilder</c> for accumulating
///         <c>args</c> JSON deltas. The previous per-delta
///         <c>partial.AppendText(...)</c> approach was O(n²) in array
///         allocations per text run.
///     </para>
///     <para>
///         <b>Lifecycle:</b> one <see cref="StreamingCoalescer" /> per turn.
///         The caller (<c>AgentLoop</c>) flushes pending buffers via
///         <see cref="FlushText" /> / <see cref="FlushThinking" /> before
///         transitioning between text/thinking/tool-call runs and before
///         materializing tool calls. <see cref="Dispose" /> on a coalescer
///         with pending buffers (e.g. on stream cancellation) is safe.
///     </para>
/// </remarks>
internal sealed class StreamingCoalescer : IDisposable
{
    private readonly Dictionary<string, (string Name, StringBuilderPool.PooledStringBuilder Args)> _pendingToolCalls = new(capacity: 4);
    private readonly StringBuilderPool.PooledStringBuilder _textBuffer = StringBuilderPool.Rent(4096);
    private readonly StringBuilderPool.PooledStringBuilder _thinkingBuffer = StringBuilderPool.Rent(1024);
    private bool _disposed;

    /// <summary>True when there's accumulated text delta not yet flushed to the partial.</summary>
    public bool HasPendingText
    {
        get;
        private set;
    }

    /// <summary>True when there's accumulated thinking delta not yet flushed.</summary>
    public bool HasPendingThinking
    {
        get;
        private set;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _textBuffer.Dispose();
        _thinkingBuffer.Dispose();
        DiscardPendingToolCalls();
    }

    /// <summary>Track a text delta — append to the text buffer.</summary>
    public void AppendTextDelta(string delta)
    {
        _textBuffer.Builder.Append(delta);
        HasPendingText = true;
    }

    /// <summary>Track a thinking delta — append to the thinking buffer.</summary>
    public void AppendThinkingDelta(string delta)
    {
        _thinkingBuffer.Builder.Append(delta);
        HasPendingThinking = true;
    }

    /// <summary>Begin accumulating args for a tool call.</summary>
    public void StartToolCall(string id, string toolName) => _pendingToolCalls[id] = (toolName, StringBuilderPool.Rent());

    /// <summary>Append a tool-call args delta.</summary>
    public void AppendToolCallDelta(string id, string argsDelta)
    {
        if (_pendingToolCalls.TryGetValue(id, out var acc))
        {
            acc.Args.Builder.Append(argsDelta);
            _pendingToolCalls[id] = acc;
        }
    }

    /// <summary>
    ///     Flush any pending text into the partial message and return the
    ///     flushed text (empty if nothing pending). Clears the text buffer.
    /// </summary>
    public string FlushText()
    {
        if (!HasPendingText) return string.Empty;
        string text = _textBuffer.ToString();
        _textBuffer.Builder.Clear();
        HasPendingText = false;
        return text;
    }

    /// <summary>
    ///     Flush any pending thinking into the partial message and return
    ///     the flushed thinking text. Clears the thinking buffer.
    /// </summary>
    public string FlushThinking()
    {
        if (!HasPendingThinking) return string.Empty;
        string thinking = _thinkingBuffer.ToString();
        _thinkingBuffer.Builder.Clear();
        HasPendingThinking = false;
        return thinking;
    }

    /// <summary>
    ///     Maximum number of trailing characters of the raw args JSON preserved
    ///     in <see cref="MalformedToolCall.RawArgsTail" />.
    /// </summary>
    internal const int RawArgsTailLength = 200;

    /// <summary>
    ///     Materialize all accumulated tool calls into <see cref="ToolCallPart" />
    ///     list. Parses each tool's args JSON, returning pooled StringBuilders
    ///     to the pool. Interns tool names via <see cref="StringPool.Shared" />.
    ///     <para>
    ///         Tool calls whose args JSON fails to parse are NOT materialized
    ///         (they would otherwise execute with silently-replaced
    ///         <c>{}</c> arguments); they are reported via
    ///         <paramref name="malformedSink" /> instead so the caller can
    ///         surface an error tool_result to the model.
    ///     </para>
    /// </summary>
    /// <param name="malformedSink">
    ///     Optional list receiving one <see cref="MalformedToolCall" /> per
    ///     un-parseable tool call. Calls added here are excluded from the
    ///     returned executable list.
    /// </param>
    public List<ToolCallPart> MaterializeToolCalls(List<MalformedToolCall>? malformedSink = null)
    {
        var result = new List<ToolCallPart>(_pendingToolCalls.Count);
        foreach ((string id, (string name, var args)) in _pendingToolCalls)
        {
            string jsonText = args.Builder.Length == 0 ? "{}" : args.ToString();
            JsonElement parsedArgs;
            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                parsedArgs = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Do not execute the tool with fabricated empty args — report the
                // call as malformed so the loop can return an error tool_result.
                malformedSink?.Add(new MalformedToolCall(id, name, RawTail(jsonText)));
                continue;
            }
            finally
            {
                args.Dispose();
            }

            string internedName = name; // StringPool interning removed — was using CommunityToolkit.HighPerformance.StringPool
            result.Add(new ToolCallPart(id, internedName, parsedArgs));
        }
        _pendingToolCalls.Clear();
        return result;
    }

    private static string RawTail(string jsonText)
    {
        if (jsonText.Length <= RawArgsTailLength)
        {
            return jsonText;
        }

        return jsonText.Substring(jsonText.Length - RawArgsTailLength);
    }

    /// <summary>
    ///     Discard all pending tool calls (e.g. on stream error / cancellation).
    ///     Returns each per-tool-call pooled StringBuilder to the pool.
    /// </summary>
    public void DiscardPendingToolCalls()
    {
        foreach (var (_, entry) in _pendingToolCalls)
        {
            entry.Args.Dispose();
        }
        _pendingToolCalls.Clear();
    }

    /// <summary>Reset all buffers for a new turn.</summary>
    public void Reset()
    {
        _textBuffer.Builder.Clear();
        _thinkingBuffer.Builder.Clear();
        DiscardPendingToolCalls();
        HasPendingText = false;
        HasPendingThinking = false;
    }
}
