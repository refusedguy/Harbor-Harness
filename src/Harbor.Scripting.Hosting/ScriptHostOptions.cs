// Hosting layer — orchestrator. The ONLY layer that depends on Engines + Storage + Compilation + Bridge.
//
// Layering rule (see docs/SCRIPTING.md §Architecture):
//   ScriptHost composes an IScriptEngine + IScriptStore + IScriptCompiler +
//   ScriptGlobals and orchestrates the load/evaluate pipeline. It does NOT
//   implement any of those concerns itself — it just wires them.
namespace Harbor.Scripting.Hosting;

/// <summary>
///     Options for the <see cref="ScriptHost" />.
/// </summary>
public sealed class ScriptHostOptions
{
    /// <summary>
    ///     Engine resource limits applied to each script evaluation. Defaults
    ///     to <see cref="ScriptEngineOptions.Default" />.
    /// </summary>
    public ScriptEngineOptions EngineOptions { get; init; } = ScriptEngineOptions.Default;

    /// <summary>
    ///     If <see langword="true" />, a failure in one script logs a warning
    ///     and the host continues to the next script. If <see langword="false" />,
    ///     the first failure aborts <see cref="ScriptHost.LoadAllAsync" />.
    ///     Default: <see langword="true" /> (continue on failure).
    /// </summary>
    public bool ContinueOnFailure { get; init; } = true;
}
