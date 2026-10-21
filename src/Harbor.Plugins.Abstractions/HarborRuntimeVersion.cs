namespace Harbor.Plugins.Abstractions;

/// <summary>
///     The Harbor version that is running — the other operand of every
///     compatibility check.
/// </summary>
/// <remarks>
///     <para>
///         It lives HERE, next to <see cref="PluginCompatibility" />, because
///         the rule and the version it compares against are ONE fact split in
///         two: the rule says "the running Harbor must be at or above the
///         declared floor", and a floor is meaningless without a running
///         version to compare it to.
///     </para>
///     <para>
///         <b>Why it moved here (#1738).</b> The constant used to live in
///         <c>Harbor.Plugins.Instantiation</c>
///         (<c>PluginLifecycle.CurrentHarborVersion</c>), and the plugin host
///         takes the running version by INJECTION for exactly that reason — the
///         host sits above the layer that owns it and may not bind it. But two
///         layers need the running version and only one of them can own a
///         constant the other may not read: the lifecycle helper (which hands it
///         to a plugin) and the agent-facing plugin inventory (which tells an
///         agent whether the plugin it is about to install will even load
///         here). Moving the constant DOWN to the contract layer both of them
///         may bind is what lets them agree on one answer.
///     </para>
///     <para>
///         <c>PluginLifecycle.CurrentHarborVersion</c> still exists and still
///         says the same thing; it reads this constant rather than repeating
///         the literal, so there is one number to bump with a release.
///     </para>
/// </remarks>
public static class HarborRuntimeVersion
{
    /// <summary>
    ///     The version of the running Harbor. Bumped with each Harbor release.
    /// </summary>
    public static readonly Version Current = new(0, 4, 0);
}
