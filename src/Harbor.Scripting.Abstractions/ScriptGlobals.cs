// Bridge layer — the `Harbor` global object exposed to scripts.
//
// Layering rule (see docs/SCRIPTING.md §Architecture):
//   This layer is what scripts SEE. It depends ONLY on Harbor.Abstractions
//   (registries + logger). It knows nothing about engines, storage, or
//   compilation. Engines consume this type to wire their script-side bridge.
namespace Harbor.Scripting.Abstractions;

/// <summary>
///     The .NET-side representation of the <c>Harbor</c> global object that
///     scripts see at runtime.
/// </summary>
/// <remarks>
///     <para>
///         Each engine implementation is responsible for surfacing these
///         globals to the script environment as a <c>Harbor</c> object with
///         the methods documented in <c>docs/SCRIPTING.md</c>:
///         <list type="bullet">
///             <item><c>Harbor.registerTool(def)</c> — registers an <see cref="ITool" /> built from a script definition.</item>
///             <item><c>Harbor.log(msg)</c> — routes a string to <see cref="Logger" />.</item>
///             <item><c>Harbor.tools.get(name)</c> / <c>Harbor.tools.list()</c> — read-only access to the tool registry.</item>
///             <item><c>Harbor.providers.list()</c> / <c>Harbor.agents.list()</c> — read-only registry introspection.</item>
///         </list>
///     </para>
///     <para>
///         <b>Layering:</b> this type is deliberately a leaf — it references
///         only <c>Harbor.Abstractions</c> types. Engines take it as an input;
///         the Hosting layer wires it together; the Storage and Compilation
///         layers never see it.
///     </para>
/// </remarks>
public sealed class ScriptGlobals
{
    /// <summary>
    ///     Tool registry — exposed to scripts as <c>Harbor.tools</c> and used
    ///     as the destination for <c>Harbor.registerTool</c> calls.
    /// </summary>
    public required IToolRegistry Tools { get; init; }

    /// <summary>
    ///     Provider registry — exposed to scripts as <c>Harbor.providers</c>
    ///     when non-null. Optional to support lightweight test contexts.
    /// </summary>
    public IProviderRegistry? Providers { get; init; }

    /// <summary>
    ///     Agent registry — exposed to scripts as <c>Harbor.agents</c> when
    ///     non-null. Optional.
    /// </summary>
    public IAgentRegistry? Agents { get; init; }

    /// <summary>
    ///     Logger — exposed to scripts as <c>Harbor.log</c>.
    /// </summary>
    public required ILogger Logger { get; init; }
}
