using Avalonia;
using Harbor.Desktop.Shared.Locators;

namespace Harbor.App.Avalonia.Hosting;

/// <summary>
///     Implemented by the shell window — the one view the desktop composition root
///     builds itself, through <c>ActivatorUtilities.CreateInstance&lt;MainWindow&gt;</c>,
///     and can therefore hand a real <see cref="IViewModelLocator" /> to.
/// </summary>
/// <remarks>
///     This is the door #779 needed and did not have. <see cref="IShellLocatorHost" />
///     is deliberately NOT a container: it exposes the named locator abstraction the
///     composition root already registers (<c>ViewModelRegistration.Register</c> calls
///     <c>AddViewModelLocator</c>), which is the shape
///     <c>ServiceLocatorBoundaryRules</c> already declares deliberate — "a NAMED
///     abstraction with its own interface, registered in the root and injected at the
///     use site … it is the target of #470, not a breach of it".
/// </remarks>
public interface IShellLocatorHost
{
    /// <summary>The locator every shell view under this window resolves through.</summary>
    IViewModelLocator Locator { get; }
}

/// <summary>
///     Carries the composition root's <see cref="IViewModelLocator" /> down to the
///     views declared in XAML, by walking the logical tree to the window that was
///     handed one.
/// </summary>
/// <remarks>
///     <para>
///         WHY NOT THE STATIC THAT WAS THERE. <c>App.Services</c> made every view's
///         dependency absent from its signature and reachable from anywhere at any
///         time, through a property initialised to <c>null!</c> — so a view that ran
///         before the handover failed as a bare <c>NullReferenceException</c> rather
///         than naming the step that was missed. The dependency now travels with the
///         visual tree: a view resolves a locator only if some ancestor was given one,
///         and says so by name when none was.
///     </para>
///     <para>
///         WHY NOT A CONSTRUCTOR PARAMETER. XAML instantiates these views itself and
///         needs a parameterless constructor; the #63 note on <c>ViewModelLocator</c>
///         already records that this is why the locator exists at all ("XAML creates
///         view-models by convention outside DI"). The tree walk is the Avalonia-idiomatic
///         equivalent of an injected parameter for a control the framework builds.
///     </para>
///     <para>
///         WHY THE LOGICAL TREE, NOT A STATIC CACHE. A static here would relocate the
///         very global this replaced. <see cref="StyledElement.Parent" /> is the
///         logical parent, so the value is scoped to the tree that was given the
///         dependency: a view constructed in a unit test, or by a host that never built
///         a window, has no ancestor and therefore no locator.
///     </para>
/// </remarks>
public static class ShellLocator
{
    /// <summary>
    ///     The locator for the shell window hosting <paramref name="element" />.
    /// </summary>
    /// <param name="element">The view asking. Resolved from itself upward.</param>
    /// <exception cref="InvalidOperationException">
    ///     No ancestor implements <see cref="IShellLocatorHost" /> — i.e. this view is
    ///     not under a window the composition root built.
    /// </exception>
    public static IViewModelLocator Of(StyledElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        for (StyledElement? node = element; node is not null; node = node.Parent)
        {
            if (node is IShellLocatorHost host)
            {
                return host.Locator;
            }
        }

        throw new InvalidOperationException(
            $"'{element.GetType().Name}' found no {nameof(IShellLocatorHost)} between itself and " +
            "the logical root, so there is no IViewModelLocator to resolve through. Shell views " +
            "are created by XAML and receive the locator from the shell window, which the desktop " +
            "composition root builds via ActivatorUtilities. A view constructed outside one — a " +
            "unit test, or a host that never showed a window — has no locator by design; there is " +
            "deliberately no ambient left to fall back on.");
    }
}