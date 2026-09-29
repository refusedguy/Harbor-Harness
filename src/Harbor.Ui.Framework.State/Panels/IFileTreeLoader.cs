using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Panels;

/// <summary>
///     Starts and owns directory listings on behalf of file-tree panels (#667).
/// </summary>
/// <remarks>
/// <para>
///     <b>Why an interface at all.</b> <c>FileTreeLoader</c> is a
///     Presentation service in <c>Harbor.Ui.Framework.Services</c>, and
///     <see cref="PanelServices" /> lives in
///     <c>Harbor.Ui.Framework.State</c> — which Services references, not the
///     other way round. Naming the concrete type here would invert that edge and
///     put a cycle in the project graph. The interface keeps the panel's
///     dependency pointing the way the layers point, and it is the narrower
///     contract anyway: a panel needs "ask", not "know how a walk is scheduled".
/// </para>
/// <para>
///     <b>The contract a panel may rely on.</b>
///     <see cref="Request" /> returns promptly even when it starts work, and
///     starting work is its only job. It is safe to call it from a render thread
///     on every frame, and safe to call on a store the caller owns. A panel must
///     treat a <see langword="null" /> loader as "this host cannot list
///     directories" and degrade — never as a reason to read the filesystem
///     itself, which is the defect this seam was introduced to end.
/// </para>
/// </remarks>
public interface IFileTreeLoader
{
    /// <summary>
    ///     Ensure a listing of <paramref name="directory" /> for
    ///     <paramref name="panelId" /> is on its way or already settled. Returns
    ///     immediately either way.
    /// </summary>
    /// <remarks>
    ///     A request for a directory already settled in
    ///     <paramref name="store" /> is a no-op, which is what makes calling this
    ///     once per frame affordable. A request for a DIFFERENT directory
    ///     supersedes whatever is in flight for that panel: the previous walk is
    ///     cancelled and its result is discarded rather than published.
    /// </remarks>
    /// <param name="panelId">The panel id that wants the listing.</param>
    /// <param name="directory">The resolved directory to list, never empty.</param>
    /// <param name="store">Where to publish the result. Null disables the request.</param>
    void Request(string panelId, string directory, UiStore? store);

    /// <summary>
    ///     Cancel any walk in flight for <paramref name="panelId" />, without
    ///     discarding an already-settled listing.
    /// </summary>
    /// <param name="panelId">The panel id whose load should stop.</param>
    void CancelPanelLoad(string panelId);
}
