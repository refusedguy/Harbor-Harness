namespace Harbor.Plugins.Abstractions;

/// <summary>
///     Verdict returned by an <see cref="IPluginTrustPolicy" /> for a single plugin script.
/// </summary>
public enum PluginTrustDecision
{
    /// <summary>The plugin may be compiled and executed in-process.</summary>
    Trusted,

    /// <summary>
    ///     The plugin must not be run. The loader skips it with a warning — fail-closed.
    /// </summary>
    Untrusted,
}

/// <summary>
///     Decides whether a CS-source plugin may be loaded. Trust is evaluated per
///     <see cref="PluginScript" /> (absolute path + content hash) before compilation —
///     plugins execute in-process with full trust, so the decision MUST happen before
///     any of the script's code runs.
/// </summary>
/// <remarks>
///     <para>
///         Implementations are consulted once per discovered script on each load pass.
///         A policy may persist decisions (e.g. by path+hash) and may prompt the user;
///         when no interactive channel exists it must fail closed
///         (<see cref="PluginTrustDecision.Untrusted" />).
///     </para>
/// </remarks>
public interface IPluginTrustPolicy
{
    /// <summary>
    ///     Decide whether <paramref name="script" /> may be loaded.
    /// </summary>
    /// <param name="script">The discovered plugin script (path, source, SHA-256 hash).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see cref="PluginTrustDecision.Trusted" /> to allow loading.</returns>
    Task<PluginTrustDecision> DecideAsync(PluginScript script, CancellationToken ct = default);
}
