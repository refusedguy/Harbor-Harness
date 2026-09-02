namespace Harbor.Abstractions.Tools;
/// <summary>
///     Registry of Model Context Protocol (MCP) servers reachable from the agent runtime.
/// </summary>
/// <remarks>
///     <para>
///         MCP servers are external processes (typically stdio JSON-RPC) that expose tools,
///         resources, and prompts to the agent. The <c>mcp</c> builtin tool is a thin bridge:
///         it looks up a server by name in the registry and forwards a method call.
///     </para>
///     <para>
///         Implementations MUST be thread-safe. The default <c>InMemoryMcpRegistry</c> lives in
///         <c>Harbor.Core</c> and returns <see cref="Result{T}.Failure" /> for every call —
///         production hosts replace it with a real MCP client implementation.
///     </para>
/// </remarks>
public interface IMcpRegistry
{
    /// <summary>
    ///     Register an MCP server by name with a stdio command line that launches it.
    /// </summary>
    /// <param name="name">Stable lowercase server name (e.g. <c>filesystem</c>).</param>
    /// <param name="stdioCommand">Shell command that launches the server in stdio mode.</param>
    /// <returns>Success, or failure with an error message.</returns>
    public Result Register(string name, string stdioCommand);

    /// <summary>
    ///     Unregister an MCP server by name.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <returns>Success, or failure if the server is not registered.</returns>
    public Result Unregister(string name);

    /// <summary>
    ///     Returns the list of registered server names.
    /// </summary>
    /// <returns>A read-only list of server names.</returns>
    public IReadOnlyList<string> GetServerNames();

    /// <summary>
    ///     Aggregated snapshot of the instructions reported by registered MCP
    ///     servers (ROP-D Z3). Sources: a static <c>instructions</c> hint in
    ///     <c>mcp.json</c>, and the <c>instructions</c> field of an
    ///     <c>initialize</c> response observed through <see cref="InvokeAsync" />.
    /// </summary>
    /// <remarks>
    ///     Existence-tolerant by contract: servers that have not reported any
    ///     instructions are simply absent from the snapshot — never a failure.
    ///     Implementations MUST be thread-safe and return a stable snapshot.
    /// </remarks>
    /// <returns>A read-only list, empty when no server reported instructions.</returns>
    public IReadOnlyList<McpServerInstructions> GetInstructions();

    /// <summary>
    ///     Invoke a method on a registered MCP server and return the JSON response.
    /// </summary>
    /// <param name="server">Server name.</param>
    /// <param name="method">JSON-RPC method name (e.g. <c>tools/list</c>, <c>tools/call</c>).</param>
    /// <param name="args">Arguments as a raw JSON element (object or array).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     Success with the response payload serialized to a JSON string, or failure with an
    ///     error message (e.g. <c>"MCP server 'X' is not registered"</c>).
    /// </returns>
    public Task<Result<string>> InvokeAsync(
        string server,
        string method,
        JsonElement args,
        CancellationToken cancellationToken = default);
}
