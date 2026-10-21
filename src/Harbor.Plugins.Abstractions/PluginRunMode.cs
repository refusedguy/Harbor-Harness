namespace Harbor.Plugins.Abstractions;

/// <summary>
///     How a run came to be (#1737).
/// </summary>
/// <remarks>
///     <para>
///         The two modes are the whole difference between "start what is there" and
///         "replace what is running". An update STOPS the running version before the
///         new one starts, so the two never hold the same name at the same time —
///         which is what makes a reload a reload and not a second registration that
///         collides with the first.
///     </para>
/// </remarks>
public enum PluginRunMode
{
    /// <summary>
    ///     The current definition was started. Nothing was running under that name.
    /// </summary>
    Run,

    /// <summary>
    ///     A new definition REPLACED the running one: the previous run was stopped
    ///     first, and the new one started in its place.
    /// </summary>
    Update,
}
