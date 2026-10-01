using Avalonia.Controls;
using Avalonia.Headless;
using Harbor.App.Avalonia.Hosting;
using Harbor.Desktop.Shared.Locators;
using Microsoft.Extensions.DependencyInjection;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Pins the hand-off #779 introduced: how a shell view declared in XAML — which
///     Avalonia instantiates with a parameterless constructor, so no injected
///     parameter is available — reaches the composition root's locator.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately minimal, for the reason <c>DiffViewBindingTests</c> states: no
///         <c>Window.Show</c>, no layout pass, no render timer. Only the logical tree
///         is built, which is all <c>ShellLocator.Of</c> reads. That keeps clear of
///         the known Avalonia 12 headless flakes catalogued in
///         <c>ViewInflationTests</c>.
///     </para>
///     <para>
///         What this file deliberately does NOT test is
///         <c>App.Services</c> throwing by name when the handover has not happened.
///         That is covered deterministically, and at source level, by
///         <c>ServiceLocatorBoundaryRules.DesktopAmbientContainer_IsNotNullForgiving</c>.
///         A unit test for it here would have to observe whether the handover had
///         already occurred in this shared process — i.e. it would race on exactly
///         the global state this issue removes. The two tests here therefore assert
///         only what is true regardless of test order.
///     </para>
/// </remarks>
[NotInParallel("avalonia-headless")]
public sealed class ShellLocatorTests
{
    /// <summary>
    ///     All three tree assertions share ONE headless session: Avalonia permits a
    ///     single <c>Application</c> per process, and opening a session per test would
    ///     make the suite the thing that flakes.
    /// </summary>
    [Test]
    [Retry(3)]
    public async Task Of_ResolvesThroughTheAncestorHost()
    {
        // The tree is built on the UI thread (Avalonia controls), and every
        // ASSERTION is made outside the dispatch. `Dispatch(async () => …)` binds to
        // `Dispatch(Action)` — HeadlessUnitTestSession declares no `Func<Task>`
        // overload — so the body ran as `async void`: it detached at its first
        // `await Assert`, and none of the three assertions below could fail the
        // test (#972, #766; see AvaloniaDispatchAsyncVoidRule). Each one captured a
        // value instead, and the value is checked here where a failure lands.
        IViewModelLocator? foundOnView = null;
        IViewModelLocator? composedLocator = null;
        SharedShellVm? resolvedFromView = null;
        SharedShellVm? resolvedFromRoot = null;
        InvalidOperationException? orphanFailure = null;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            // ── the whole product composition, at its smallest ──────────────────
            var services = new ServiceCollection();
            services.AddSingleton<SharedShellVm>();
            services.AddViewModelLocator();
            using var provider = services.BuildServiceProvider();
            var locator = provider.GetRequiredService<IViewModelLocator>();
            composedLocator = locator;

            // ── the shell tree, shaped the way MainWindow.axaml shapes it ────────
            var host = new LocatorHost(locator);
            var panel = new StackPanel();
            var view = new UserControl();
            panel.Children.Add(view);
            host.Content = panel;

            // 1. The locator is found by walking up — not by a global.
            foundOnView = ShellLocator.Of(view);

            // 2. And it resolves real registrations, so a view's dependency is the
            //    same instance the root would hand the constructor.
            resolvedFromView = ShellLocator.Of(view).Get<SharedShellVm>();
            resolvedFromRoot = provider.GetRequiredService<SharedShellVm>();

            // 3. A view with no host fails BY NAME. This is the assertion that keeps
            //    #779 from returning: the removed `App.Services` was `= null!`, so the
            //    failure here used to be a bare NullReferenceException naming nothing.
            var orphan = new UserControl();
            orphanFailure =
                Assert.Throws<InvalidOperationException>(() => ShellLocator.Of(orphan));
        }), CancellationToken.None);

        // 1. Walked up to the host's locator — the very instance the root composed.
        await Assert.That(foundOnView).IsSameReferenceAs(composedLocator);
        // 2. Resolved the root's registration, not a fresh instance.
        await Assert.That(resolvedFromView).IsSameReferenceAs(resolvedFromRoot);
        // 3. Names the missing host.
        await Assert.That(orphanFailure!.Message.Contains(nameof(IShellLocatorHost), StringComparison.Ordinal))
            .IsTrue();
    }

    /// <summary>
    ///     The production host for that hand-off is <c>MainWindow</c>, because it is
    ///     the one shell view the composition root builds itself
    ///     (<c>ActivatorUtilities.CreateInstance&lt;MainWindow&gt;</c>). If that link is
    ///     ever cut, every shell view's <c>ShellLocator.Of</c> starts throwing — and
    ///     nothing else in the tree would notice. Reflection only, no Avalonia session.
    /// </summary>
    [Test]
    public async Task MainWindow_IsTheProductionShellLocatorHost()
    {
        await Assert.That(typeof(IShellLocatorHost).IsAssignableFrom(typeof(Views.MainWindow)))
            .IsTrue();
    }

    /// <summary>The stand-in for <c>MainWindow</c>: a tree node that carries a locator.</summary>
    private sealed class LocatorHost : ContentControl, IShellLocatorHost
    {
        public LocatorHost(IViewModelLocator locator) => Locator = locator;

        public IViewModelLocator Locator { get; }
    }
}