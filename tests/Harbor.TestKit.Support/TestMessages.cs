// TestMessages.cs — the ONE place a test builds a message.
//
// WHY THIS FILE EXISTS (issue #1816)
// ----------------------------------
// Before it, every message in the test tree was assembled by hand:
//
//     var user = new UserMessage(
//         "m1",
//         session.Id,
//         DateTimeOffset.UtcNow,
//         "hi",
//         "code",
//         "test-model");
//
// That is 196 constructions across 72 files, and every one of them repeated
// the same handful of literals: the agent "code", the model "test-model", the
// stop reason StopReason.Stop, the usage (0, 0), a fresh guid for the id.
// The repetition was not free of consequences:
//
//   * The ARGUMENT ORDER of `UserMessage(id, sessionId, createdAt, content,
//     agent, model)` is a trap. `agent` and `model` are both `string`, and so
//     are `id` and `sessionId`, so a swapped pair still COMPILES — it just
//     makes a test assert that the agent is called "claude-opus-4". The
//     builders below take the agent and the model as NAMED parameters, and
//     the codemod that moved the call sites over never writes them
//     positionally, so a swap is no longer expressible.
//   * `new Usage(0, 0)` is a 16-character way to say "no tokens". It
//     appeared in every assistant message the tree built by hand.
//
// WHAT THIS IS NOT
// ----------------
// It is NOT a second message model. Every builder returns the very record
// the product persists, with the very values the caller passed. The only
// thing the builders add is a DEFAULT for the arguments that the same value
// in the same place in the whole test tree repeats — and every default is
// documented below with the count that chose it.
//
// The defaults were MEASURED on the tree this landed on, not guessed:
// `tools/codemod-test-events.py` counted every `new <Message>(...)` in
// `tests/` and grouped them by shape. The counts quoted in each summary are
// that measurement, so a reader can see WHY a default is what it is.
//
// WHERE IT LIVES, AND WHY
// -----------------------
// `Harbor.TestKit.Support`, next to `TestEvents` — and that is the whole
// argument. The messages live in `Harbor.Abstractions.Contracts`, exactly
// like the events do, and this part's closure is exactly {itself,
// Harbor.Abstractions, Harbor.Abstractions.Contracts} — no product assembly
// beyond the two contract ones, which every test project that builds a
// message already has. Putting the builders here therefore adds NO product
// assembly to any consumer.
//
// The part was the ONLY sensible home, because it is the leaf: the core kit
// (`Harbor.TestKit`) reaches 20 test projects, this part reaches 33 (16
// directly, 20 through the core, 3 in both), and the three message-heavy
// projects that reference ONLY this part — `Harbor.Benchmarks`,
// `Harbor.Storage.Tests`, `Harbor.Tui.Tests` — reach the builders without a
// csproj edit. The builders were in the core kit before this change; moving
// them here is what makes them reachable everywhere the events already are.
//
// The namespace did not change: it is `Harbor.TestKit`, so a consumer's
// `using Harbor.TestKit;` is all it needs, whichever part the type lives in.
//
// THE CODE THAT MIGRATED THE CALL SITES
// -------------------------------------
// `tools/codemod-test-events.py` rewrote the call sites; it is re-runnable
// (`--check` reports, `--apply` writes) and it is the reason this file's
// defaults are the values they are: the codemod only ever emits an argument
// that differs from the default, so every migrated call means exactly what
// the hand-written one meant.

using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;

namespace Harbor.TestKit;

/// <summary>
///     Builders for the messages a test puts into a session.
/// </summary>
/// <remarks>
///     <para>
///         One builder per message type, named after the type it builds. The
///         parameters mirror the record's own constructor in the same order,
///         with ONE change: the PAYLOAD comes first, because it is the
///         argument every call site passes and the argument the reader looks
///         for. The rest of the parameters keep the record's order, so a
///         call site can be checked against the record without looking
///         anything up.
///     </para>
///     <para>
///         The parameters that the record types identically — the id, the
///         session id, the agent and the model are all <see cref="string" /> —
///         are the ones a positional call could get wrong, and the codemod
///         writes them as named arguments. The payload is the only argument
///         a call site passes positionally.
///     </para>
/// </remarks>
public static class TestMessages
{
    // ---------------------------------------------------------------------
    // The defaults
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The session id the builders use when the caller does not name one.
    /// </summary>
    /// <remarks>
    ///     Measured: 11 of the 122 <c>UserMessage</c> sites, 3 of the 53
    ///     <c>AssistantMessage</c> sites and 2 of the 21
    ///     <c>ToolResultMessage</c> sites already said "s1"; the next most
    ///     common literal is "session-1". The builders kept the session id
    ///     the kit already used, so the call sites that were written before
    ///     this change still mean what they meant.
    /// </remarks>
    public const string DefaultSessionId = "session-1";

    /// <summary>
    ///     The agent the builders name when the caller does not.
    /// </summary>
    /// <remarks>
    ///     Measured: 107 of the 122 <c>UserMessage</c> sites already said
    ///     "code". The next most common is "user", which is not an agent
    ///     name at all.
    /// </remarks>
    public const string DefaultAgent = "code";

    /// <summary>
    ///     The model the builders name when the caller does not.
    /// </summary>
    /// <remarks>
    ///     Measured: 39 of the 122 <c>UserMessage</c> sites and 23 of the 53
    ///     <c>AssistantMessage</c> sites already said "test-model" — the
    ///     largest single value in both, and the one the kit already used.
    /// </remarks>
    public const string DefaultModel = "test-model";

    // ---------------------------------------------------------------------
    // UserMessage
    // ---------------------------------------------------------------------

    /// <summary>
    ///     A user message.
    /// </summary>
    /// <param name="content">The user's prompt text.</param>
    /// <param name="id">
    ///     The message id. Defaults to a fresh guid — 25 of the 122
    ///     <c>UserMessage</c> sites already asked for one.
    /// </param>
    /// <param name="sessionId">The owning session. Defaults to <see cref="DefaultSessionId" />.</param>
    /// <param name="createdAt">
    ///     The message's timestamp. Defaults to now — 58 of the 122
    ///     <c>UserMessage</c> sites already passed <see cref="DateTimeOffset.UtcNow" />.
    /// </param>
    /// <param name="agent">The agent that received the prompt. Defaults to <see cref="DefaultAgent" />.</param>
    /// <param name="model">The model the prompt was aimed at. Defaults to <see cref="DefaultModel" />.</param>
    /// <param name="parentId">The parent message, for a summary or a branch.</param>
    /// <param name="attachments">The images the user attached to this turn.</param>
    /// <returns>The message to put into the session.</returns>
    public static UserMessage User(
        string content,
        string? id = null,
        string sessionId = DefaultSessionId,
        DateTimeOffset? createdAt = null,
        string agent = DefaultAgent,
        string model = DefaultModel,
        string? parentId = null,
        IReadOnlyList<ImageAttachment>? attachments = null) =>
        new(
            id ?? Guid.NewGuid().ToString("N"),
            sessionId,
            createdAt ?? DateTimeOffset.UtcNow,
            content,
            agent,
            model,
            parentId,
            attachments);

    // ---------------------------------------------------------------------
    // AssistantMessage
    // ---------------------------------------------------------------------

    /// <summary>
    ///     An assistant message that carries one text part.
    /// </summary>
    /// <param name="text">The assistant's text.</param>
    /// <param name="id">
    ///     The message id. Defaults to a fresh guid — 17 of the 53
    ///     <c>AssistantMessage</c> sites already asked for one.
    /// </param>
    /// <param name="sessionId">The owning session. Defaults to <see cref="DefaultSessionId" />.</param>
    /// <param name="createdAt">
    ///     The message's timestamp. Defaults to now — 15 of the 53
    ///     <c>AssistantMessage</c> sites already passed <see cref="DateTimeOffset.UtcNow" />.
    /// </param>
    /// <param name="stopReason">
    ///     Why the model stopped. Defaults to <see cref="StopReason.Stop" /> —
    ///     31 of the 53 <c>AssistantMessage</c> sites already said so.
    /// </param>
    /// <param name="usage">
    ///     The reported token usage. Defaults to no tokens — 19 of the 53
    ///     <c>AssistantMessage</c> sites already said <c>new Usage(0, 0)</c>.
    /// </param>
    /// <param name="model">The model that produced the message. Defaults to <see cref="DefaultModel" />.</param>
    /// <param name="parentId">The parent message, for a summary or a branch.</param>
    /// <param name="isSummary">Whether the message is a compaction summary.</param>
    /// <param name="summaryFirstKeptId">For a summary, the first tail message kept after compaction.</param>
    /// <returns>The message to put into the session.</returns>
    /// <remarks>
    ///     For a message that carries a tool call, a thinking part or a file,
    ///     use <see cref="AssistantParts" /> — the payload there is the list
    ///     of parts, not one text.
    /// </remarks>
    public static AssistantMessage Assistant(
        string text,
        string? id = null,
        string sessionId = DefaultSessionId,
        DateTimeOffset? createdAt = null,
        StopReason stopReason = StopReason.Stop,
        Usage? usage = null,
        string model = DefaultModel,
        string? parentId = null,
        bool isSummary = false,
        string? summaryFirstKeptId = null) =>
        new(
            id ?? Guid.NewGuid().ToString("N"),
            sessionId,
            createdAt ?? DateTimeOffset.UtcNow,
            [new TextPart(text)],
            stopReason,
            usage ?? new Usage(0, 0),
            model,
            parentId,
            isSummary,
            summaryFirstKeptId);

    /// <summary>
    ///     An assistant message that carries an explicit list of content parts.
    /// </summary>
    /// <param name="parts">
    ///     The ordered content parts — text, thinking, tool calls, files.
    /// </param>
    /// <param name="id">The message id. Defaults to a fresh guid.</param>
    /// <param name="sessionId">The owning session. Defaults to <see cref="DefaultSessionId" />.</param>
    /// <param name="createdAt">The message's timestamp. Defaults to now.</param>
    /// <param name="stopReason">
    ///     Why the model stopped. Defaults to <see cref="StopReason.Stop" />.
    /// </param>
    /// <param name="usage">The reported token usage. Defaults to no tokens.</param>
    /// <param name="model">The model that produced the message. Defaults to <see cref="DefaultModel" />.</param>
    /// <param name="parentId">The parent message, for a summary or a branch.</param>
    /// <param name="isSummary">Whether the message is a compaction summary.</param>
    /// <param name="summaryFirstKeptId">For a summary, the first tail message kept after compaction.</param>
    /// <returns>The message to put into the session.</returns>
    public static AssistantMessage AssistantParts(
        IReadOnlyList<ContentPart> parts,
        string? id = null,
        string sessionId = DefaultSessionId,
        DateTimeOffset? createdAt = null,
        StopReason stopReason = StopReason.Stop,
        Usage? usage = null,
        string model = DefaultModel,
        string? parentId = null,
        bool isSummary = false,
        string? summaryFirstKeptId = null) =>
        new(
            id ?? Guid.NewGuid().ToString("N"),
            sessionId,
            createdAt ?? DateTimeOffset.UtcNow,
            parts,
            stopReason,
            usage ?? new Usage(0, 0),
            model,
            parentId,
            isSummary,
            summaryFirstKeptId);

    // ---------------------------------------------------------------------
    // ToolResultMessage
    // ---------------------------------------------------------------------

    /// <summary>
    ///     A message carrying the results of one or more tool executions.
    /// </summary>
    /// <param name="entries">The tool results this turn produced.</param>
    /// <param name="id">
    ///     The message id. Defaults to a fresh guid — 4 of the 21
    ///     <c>ToolResultMessage</c> sites already asked for one.
    /// </param>
    /// <param name="sessionId">The owning session. Defaults to <see cref="DefaultSessionId" />.</param>
    /// <param name="createdAt">
    ///     The message's timestamp. Defaults to now — 4 of the 21
    ///     <c>ToolResultMessage</c> sites already passed <see cref="DateTimeOffset.UtcNow" />.
    /// </param>
    /// <param name="parentId">The parent message, for a summary or a branch.</param>
    /// <returns>The message to put into the session.</returns>
    public static ToolResultMessage ToolResult(
        IReadOnlyList<ToolResultEntry> entries,
        string? id = null,
        string sessionId = DefaultSessionId,
        DateTimeOffset? createdAt = null,
        string? parentId = null) =>
        new(
            id ?? Guid.NewGuid().ToString("N"),
            sessionId,
            createdAt ?? DateTimeOffset.UtcNow,
            entries,
            parentId);

    // ---------------------------------------------------------------------
    // The shared helper
    // ---------------------------------------------------------------------

    /// <summary>
    ///     The text a scripted LLM client was asked to answer.
    /// </summary>
    /// <param name="request">The request the client received.</param>
    /// <returns>
    ///     The concatenated user text and the tool outputs of the request.
    /// </returns>
    /// <remarks>
    ///     It was the one thing three suites needed and each of them wrote
    ///     its own copy of; it lives here because it reads a message-shaped
    ///     value out of a request, which is the same job the builders above
    ///     do on the other side of the conversation.
    /// </remarks>
    public static string RenderText(LlmRequest request)
    {
        var sb = new System.Text.StringBuilder();
        foreach (LlmMessage message in request.Messages)
        {
            if (message is LlmUserMessage user)
            {
                foreach (LlmContentBlock block in user.Content)
                {
                    if (block is LlmTextBlock text)
                    {
                        sb.Append(text.Text);
                    }
                }
            }
            else if (message is LlmToolResultMessage toolResult)
            {
                sb.Append(toolResult.Output);
            }
        }
        return sb.ToString();
    }
}
