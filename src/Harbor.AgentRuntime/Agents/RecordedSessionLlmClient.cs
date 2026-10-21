// RecordedSessionLlmClient.cs — the replay side of a recorded session (#1740).
//
// WHAT THIS IS
// ------------
// An ILlmClient that serves a session log back to the agent loop instead of
// calling a provider. The recorded transcript IS the source of the model's
// turns: the N-th assistant message in the log is the N-th turn this client
// serves. No key, no network, no clock — the same log always produces the
// same run, which is what makes a committed session log a REPRODUCIBLE run
// and not just a transcript.
//
// WHY IT LIVES NEXT TO THE LOOP AND NOT NEXT TO THE STORE
// -------------------------------------------------------
// The projection is from the DOMAIN model (an assistant message is a list of
// content parts), not from the file format, so it belongs to the layer that
// consumes assistant messages. The store's own tier (#1740) decides what may
// be in that log; this client decides how a log is played back.
//
// THE EVENTS IT SERVES
// --------------------
// The turn runner consumes four event kinds and nothing else: a text delta, a
// thinking delta, a tool-call start, a tool-call argument delta, and a step
// finish. The client emits exactly those, in the order the parts were
// recorded, so the loop rebuilds the same assistant message it recorded.
//
// A compaction SUMMARY is not a turn: it is a derived artifact the compaction
// service wrote into the log, and replaying it as a model turn would put a
// message in the run that the model never produced. Summaries are skipped.

using System.Runtime.CompilerServices;

namespace Harbor.Application.Agents;

/// <summary>
///     An <see cref="ILlmClient" /> that replays a recorded session (#1740).
/// </summary>
/// <remarks>
///     <para>
///         Construct it with the messages of a session log — the list
///         <c>ISessionStore.GetMessagesAsync</c> returns — and it will serve
///         the assistant turns that log holds, in order, without a provider
///         key and without a network.
///     </para>
///     <para>
///         The client is DETERMINISTIC: the same log always yields the same
///         turns, in the same order, with the same tool calls. It is not a
///         simulator — it does not re-run the tools, and it does not re-decide
///         anything; the tool results in the log are the ones a replaying run
///         compares against.
///     </para>
/// </remarks>
public sealed class RecordedSessionLlmClient : ILlmClient
{
    private readonly LlmEvent[][] _turns;
    private readonly ModelInfo _model;
    private int _turn;

    /// <summary>
    ///     Build a replay client over a recorded transcript.
    /// </summary>
    /// <param name="recorded">
    ///     The messages of the recorded session, in write order. The assistant
    ///     messages among them are the turns this client serves.
    /// </param>
    /// <param name="model">The model the recording was made with.</param>
    public RecordedSessionLlmClient(IReadOnlyList<AgentMessage> recorded, ModelInfo model)
        : this(recorded, model, ProviderId.Create(model.ProviderId))
    {
    }

    /// <summary>
    ///     Build a replay client over a recorded transcript, naming the
    ///     provider the recording came from.
    /// </summary>
    /// <param name="recorded">
    ///     The messages of the recorded session, in write order.
    /// </param>
    /// <param name="model">The model the recording was made with.</param>
    /// <param name="providerId">The provider id this client answers as.</param>
    public RecordedSessionLlmClient(
        IReadOnlyList<AgentMessage> recorded,
        ModelInfo model,
        ProviderId providerId)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(model);

        _model = model;
        ProviderId = providerId;
        _turns = BuildTurns(recorded);
    }

    /// <inheritdoc />
    public ProviderId ProviderId { get; }

    /// <summary>
    ///     How many turns this client has served. A replay of an N-turn
    ///     recording ends with this at N.
    /// </summary>
    public int TurnsServed => Volatile.Read(ref _turn);

    /// <summary>
    ///     How many turns the recording holds.
    /// </summary>
    public int TurnCount => _turns.Length;

    /// <inheritdoc />
    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int index = Interlocked.Increment(ref _turn) - 1;
        if (index >= _turns.Length)
        {
            throw new InvalidOperationException(
                $"The recorded session has {_turns.Length} assistant turn(s); the replay asked for turn {index + 1}. "
                + "The recording and the run disagree — the log is shorter than the run that read it.");
        }

        foreach (LlmEvent evt in _turns[index])
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return evt;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<ModelInfo>>(new[] { _model }));

    /// <summary>
    ///     Project the recorded transcript into one event script per assistant
    ///     turn.
    /// </summary>
    private static LlmEvent[][] BuildTurns(IReadOnlyList<AgentMessage> recorded)
    {
        List<LlmEvent[]> turns = [];
        for (int i = 0; i < recorded.Count; i++)
        {
            if (recorded[i] is not AssistantMessage assistant)
            {
                continue;
            }

            // A compaction summary is written into the log by the compaction
            // service, not by the model; replaying it as a turn would put a
            // message in the run that no model turn produced.
            if (assistant.IsSummary)
            {
                continue;
            }

            turns.Add(ProjectTurn(assistant, turns.Count));
        }

        return [.. turns];
    }

    /// <summary>
    ///     One assistant message as the event stream the loop consumes.
    /// </summary>
    private static LlmEvent[] ProjectTurn(AssistantMessage assistant, int index)
    {
        List<LlmEvent> events = [];
        for (int i = 0; i < assistant.Parts.Count; i++)
        {
            switch (assistant.Parts[i])
            {
                case TextPart text:
                    events.Add(new TextDeltaEvent(assistant.Id, text.Text));
                    break;

                case ThinkingPart thinking:
                    events.Add(new ThinkingDeltaEvent(assistant.Id, thinking.Text));
                    break;

                case ToolCallPart call:
                    events.Add(new ToolCallStartEvent(call.Id, call.ToolName));
                    events.Add(new ToolCallDeltaEvent(call.Id, call.Args.GetRawText()));
                    break;

                case FilePart:
                    // A file part never reaches a provider — the wire format
                    // carries an image as an attachment on the USER side, not
                    // as an assistant-side part. There is nothing to replay.
                    break;
            }
        }

        events.Add(new StepFinishEvent(index, FinishReason(assistant.StopReason), assistant.Usage));
        return [.. events];
    }

    /// <summary>
    ///     The provider-shaped finish reason a stop reason maps to. The names
    ///     are the ones <c>StopReasonJsonConverter.Parse</c> reads back, so the
    ///     replayed turn carries the same stop reason the recording did.
    /// </summary>
    private static string FinishReason(StopReason reason) => reason switch
    {
        StopReason.Stop => "stop",
        StopReason.Length => "length",
        StopReason.ToolUse => "tool_use",
        StopReason.ContentFilter => "content_filter",
        StopReason.Error => "error",
        StopReason.Aborted => "aborted",
        _ => throw new InvalidOperationException(
            $"StopReason '{reason}' is an unnamed enum value and has no provider spelling: "
            + "name it explicitly with the lowered member name body (issue #1531)."),
    };
}
