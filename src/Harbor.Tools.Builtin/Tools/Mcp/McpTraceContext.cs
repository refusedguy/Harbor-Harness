using System.Diagnostics;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Reads the agent's W3C trace parent at the MCP boundary (#2678 item 113).
///     An MCP server runs out of process and cannot see
///     <see cref="Activity.Current"/>; the traceparent has to travel in the
///     request so the server can continue the same trace.
/// </summary>
internal static class McpTraceContext
{
    /// <summary>
    ///     The source name of the agent's own activities (ToolDispatcher and the
    ///     LLM client). An ambient span from some other instrumenter, such as a
    ///     host web request or a test runner's activity, is NOT the agent trace
    ///     and must not be written to the MCP wire.
    /// </summary>
    private const string AgentTraceSource = "Harbor";

    /// <summary>
    ///     The W3C traceparent of the current agent activity, or
    ///     <see langword="null"/> when there is no agent activity, the activity
    ///     uses the legacy hierarchical id format (which is not a traceparent
    ///     and must not be presented as one), or it belongs to another source.
    /// </summary>
    internal static string? CurrentTraceparent =>
        Activity.Current is
        {
            IdFormat: ActivityIdFormat.W3C,
            Source.Name: AgentTraceSource,
            Id: { Length: > 0 } traceparent,
        }
            ? traceparent
            : null;
}
