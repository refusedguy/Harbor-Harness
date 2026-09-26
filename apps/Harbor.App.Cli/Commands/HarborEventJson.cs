using System.Buffers;
using System.Text;
using System.Text.Json;
using Harbor.Ipc;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Human-readable JSON rendering for <see cref="HarborEvent" />s.
///     Used by <c>harbor events --watch</c> and <c>harbor tui</c> attach mode:
///     one compact JSON object per line on stdout, so the stream stays
///     pipe-friendly (<c>harbor events --watch | jq</c>) for debugging.
/// </summary>
/// <remarks>
///     Debug-surface only: this never touches the MessagePack wire encoding.
///     Written with <see cref="Utf8JsonWriter" /> (no reflection) so the
///     formatter stays AOT-safe.
/// </remarks>
public static class HarborEventJson
{
    /// <summary>
    ///     Render one event as a single-line compact JSON object.
    ///     Always includes a <c>kind</c> discriminator plus the event's fields.
    /// </summary>
    /// <param name="evt">The event to render.</param>
    /// <returns>One line of JSON (no trailing newline).</returns>
    public static string Format(HarborEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            switch (evt)
            {
                case HarborEvent.AgentStarted e:
                    writer.WriteString("kind", "agent_started");
                    writer.WriteString("sessionId", e.SessionId);
                    break;
                case HarborEvent.MessageUpdate e:
                    writer.WriteString("kind", "message_update");
                    writer.WriteString("sessionId", e.Partial.SessionId);
                    writer.WriteString("delta", e.Delta);
                    break;
                case HarborEvent.MessageEnd e:
                    writer.WriteString("kind", "message_end");
                    writer.WriteString("sessionId", e.Final.SessionId);
                    writer.WriteString("model", e.Final.Model);
                    writer.WriteNumber("parts", e.Final.Parts.Count);
                    break;
                case HarborEvent.ToolStart e:
                    writer.WriteString("kind", "tool_start");
                    writer.WriteString("toolCallId", e.ToolCallId);
                    writer.WriteString("toolName", e.ToolName);
                    break;
                case HarborEvent.ToolEnd e:
                    writer.WriteString("kind", "tool_end");
                    writer.WriteString("toolCallId", e.ToolCallId);
                    writer.WriteString("output", e.Result.Output);
                    writer.WriteBoolean("isError", e.Result.IsError);
                    break;
                case HarborEvent.TurnStart e:
                    writer.WriteString("kind", "turn_start");
                    writer.WriteNumber("turn", e.Turn);
                    break;
                case HarborEvent.TurnEnd e:
                    writer.WriteString("kind", "turn_end");
                    writer.WriteNumber("turn", e.Turn);
                    break;
                case HarborEvent.AgentEnded e:
                    writer.WriteString("kind", "agent_ended");
                    writer.WriteString("sessionId", e.SessionId);
                    break;
                case HarborEvent.AgentError e:
                    writer.WriteString("kind", "agent_error");
                    writer.WriteString("message", e.Message);
                    break;
                case HarborEvent.CompactionStarted e:
                    writer.WriteString("kind", "compaction_started");
                    writer.WriteString("sessionId", e.SessionId);
                    break;
                case HarborEvent.CompactionCompleted e:
                    writer.WriteString("kind", "compaction_completed");
                    writer.WriteString("sessionId", e.SessionId);
                    writer.WriteNumber("pruned", e.Pruned);
                    writer.WriteNumber("saved", e.Saved);
                    break;
                default:
                    writer.WriteString("kind", "unknown");
                    writer.WriteString("type", evt.GetType().Name);
                    break;
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     Print an event stream as JSON lines. Stops after
    ///     <paramref name="maxEvents" /> lines when set, or when
    ///     <paramref name="ct" /> fires (Ctrl-C) — the cancellation path is a
    ///     clean exit, not an error.
    /// </summary>
    /// <param name="events">The live event stream.</param>
    /// <param name="output">Where JSON lines go (stdout).</param>
    /// <param name="maxEvents">Stop after this many lines; null = endless.</param>
    /// <param name="ct">Cancellation token (Ctrl-C).</param>
    /// <returns>How many lines were written.</returns>
    public static async Task<int> WriteEventLinesAsync(
        IAsyncEnumerable<HarborEvent> events,
        TextWriter output,
        int? maxEvents,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(output);
        int written = 0;
        try
        {
            await foreach (HarborEvent evt in events.WithCancellation(ct).ConfigureAwait(false))
            {
                output.WriteLine(Format(evt));
                written++;
                if (maxEvents.HasValue && written >= maxEvents.Value)
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean Ctrl-C exit — the count so far is the result.
        }

        return written;
    }
}
