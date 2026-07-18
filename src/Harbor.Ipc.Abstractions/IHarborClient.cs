using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;

namespace Harbor.Ipc;

/// <summary>
///     Client-facing Harbor API. Every UI layer (CLI, Avalonia, WPF, Blazor,
///     MAUI, mobile, web, out-of-process Python/JS/Rust script) talks to
///     Harbor exclusively through this interface.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two implementations:</b>
///     </para>
///     <list type="bullet">
///         <item>
///             <c>InProcessHarborClient</c> (in <c>Harbor.Ipc.InProcess</c>) —
///             default. Calls <see cref="Harbor.Abstractions.Agents.IAgent" />,
///             <see cref="Harbor.Abstractions.Sessions.ISessionStore" />, etc.
///             directly. Zero serialization overhead.
///         </item>
///         <item>
///             <c>IpcHarborClient</c> (in <c>Harbor.Ipc.Client</c>) — talks to a
///             remote <c>HarborIpcServer</c> over MessagePack-on-named-pipe
///             (Windows) or MessagePack-on-Unix-domain-socket (Linux/Mac).
///         </item>
///     </list>
///     <para>
///         Both implementations are registered through the same DI surface —
///         the UI layer never knows which one it has. Switching is a single
///         <c>HARBOR_MODE</c> env var change at startup.
///     </para>
///     <para>
///         <b>Streaming events:</b> use <see cref="SubscribeToEventsAsync" /> to
///         receive <see cref="HarborEvent" /> deltas (token streaming, tool
///         execution progress, compaction, errors). The in-process client
///         bridges these from <see cref="IEventBus" />; the IPC client receives
///         them as a server-pushed stream.
///     </para>
/// </remarks>
public interface IHarborClient : IAsyncDisposable
{
    // ── Agent control ──────────────────────────────────────────────────────

    /// <summary>
    ///     Bind the agent loop to a session and prepare it for prompts. Must be
    ///     called once per session before <see cref="SendPromptAsync" />.
    /// </summary>
    /// <param name="sessionId">The session id to bind.</param>
    /// <param name="agentName">The agent definition name (e.g. <c>code</c>, <c>plan</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    Task<Result> StartAgentAsync(string sessionId, string agentName, CancellationToken ct = default);

    /// <summary>
    ///     Abort the in-flight run (if any) at the next safe boundary.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, or failure with an error message.</returns>
    Task<Result> AbortAgentAsync(CancellationToken ct = default);

    /// <summary>
    ///     Submit a user prompt and run the agent loop to completion.
    ///     Subscribe to <see cref="SubscribeToEventsAsync" /> to receive
    ///     streaming deltas while the run is in flight.
    /// </summary>
    /// <param name="prompt">The user's prompt text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success on normal completion, or failure with an error message.</returns>
    Task<Result> SendPromptAsync(string prompt, CancellationToken ct = default);

    // ── Sessions ───────────────────────────────────────────────────────────

    /// <summary>Create a new session and persist it.</summary>
    /// <returns>The newly-created <see cref="Session" />.</returns>
    Task<Result<Session>> CreateSessionAsync(string dir, string agent, string provider, string model, CancellationToken ct = default);

    /// <summary>List all sessions, optionally filtered by project id.</summary>
    Task<Result<IReadOnlyList<Session>>> ListSessionsAsync(CancellationToken ct = default);

    /// <summary>Get a session by id.</summary>
    Task<Result<Session>> GetSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Delete a session and all of its messages.</summary>
    Task<Result> DeleteSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Get all messages for a session in chronological order.</summary>
    Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default);

    // ── Providers ──────────────────────────────────────────────────────────

    /// <summary>List all registered provider ids.</summary>
    Task<Result<IReadOnlyList<ProviderId>>> ListProvidersAsync(CancellationToken ct = default);

    /// <summary>List models, optionally filtered by provider id.</summary>
    Task<Result<IReadOnlyList<ModelInfo>>> ListModelsAsync(string? providerId = null, CancellationToken ct = default);

    // ── Tools ──────────────────────────────────────────────────────────────

    /// <summary>List all registered tools.</summary>
    Task<Result<IReadOnlyList<ToolDescriptor>>> ListToolsAsync(CancellationToken ct = default);

    // ── Streaming events ───────────────────────────────────────────────────

    /// <summary>
    ///     Subscribe to the live stream of <see cref="HarborEvent" />s. The
    ///     enumerable completes when <paramref name="ct" /> is cancelled or
    ///     the client is disposed.
    /// </summary>
    /// <param name="ct">Cancellation token that ends the subscription.</param>
    /// <returns>An async stream of events.</returns>
    IAsyncEnumerable<HarborEvent> SubscribeToEventsAsync(CancellationToken ct = default);

    // ── Connection management ──────────────────────────────────────────────

    /// <summary>
    ///     True when the client is connected to a backing service. The
    ///     in-process client is always connected after construction. The IPC
    ///     client must call <see cref="ConnectAsync" /> first.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>Establish the connection. No-op for in-process clients.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Close the connection. No-op for in-process clients.</summary>
    Task DisconnectAsync(CancellationToken ct = default);
}
