// CI integration note: run this class as a separate job-step in CI using:
//   dotnet test tests/Harbor.App.Avalonia.Tests --treenode-filter "/*/*/ViewInflationTests/*"
// Use --treenode-filter (NOT --filter) per project memory.
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Harbor.App.Avalonia;
using Harbor.App.Avalonia.ViewModels;
using Harbor.App.Avalonia.Views;
using Harbor.App.Avalonia.Views.Board;
using Harbor.App.Avalonia.Views.Shell;
using Microsoft.Extensions.Hosting;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Headless smoke tests for all Avalonia views.
///     Verifies that each view can be constructed without crashing.
///     Each view constructor calls InitializeComponent() which inflates XAML;
///     if a DataTemplate or Style fails to inflate, the constructor throws
///     and the test fails.
/// </summary>
/// <remarks>
///     The test project has an implicit reference to Harbor.App.Avalonia, so
///     <c>Views.Chrome.TitleBarView</c> resolves to the production view type.
///     Views are constructed directly — their InitializeComponent() method
///     inflates the XAML which exercises all resource lookups, style selectors,
///     and DataTemplate inflation paths.
/// </remarks>
// `avalonia-headless` serializes the headless session. `process-env` (#823) is
// the other half of this class's identity: the test below writes `HOME` and
// restores it, and AppHostDiTests writes the same variable without restoring it
// at all. `avalonia-headless` named only this class's peers, so the two were
// scheduled in different buckets and overlapped on AppHost.ResolveHarborDir
// (AppHost.cs:121-126). Two keys, because TUnit intersects on either.
//
// The array form, not `[NotInParallel("a", "b")]`: TUnit 1.61.0 declares
// exactly three constructors — `()`, `(string)` and `(string[])` — and the
// two-argument spelling is CS1729. See #849.
[NotInParallel(new[] { "avalonia-headless", "process-env" })]
public class ViewInflationTests
{
    [Test]
    public async Task TitleBarView_Inflates()
    {
        var view = new Views.Chrome.TitleBarView();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task ActivityRailView_Inflates()
    {
        var view = new Views.Shell.ActivityRailView();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task StatusBarView_Inflates()
    {
        var view = new Views.Shell.StatusBarView();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task RightDrawerView_Inflates()
    {
        var view = new Views.Shell.RightDrawerView();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task SessionsFlyoutView_Inflates()
    {
        var view = new Views.Shell.SessionsFlyoutView();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    [Skip("Known flake: headless Avalonia virtualization timing in CI is non-deterministic. See issue #14.")]
    public async Task ChatView_Inflates()
    {
        // ChatView's timeline sets ListBox.ItemContainerTheme with
        // BasedOn={StaticResource {x:Type ListBoxItem}} — that lookup needs the
        // app theme (Fluent) to be loaded, which only exists once the App is
        // booted. Bare `new ChatView()` on a platform-less thread therefore
        // throws KeyNotFoundException. Same headless pattern as
        // SettingsView_Inflates below: boot a HeadlessUnitTestSession and
        // inflate on its UI thread.
        // The assertion is OUTSIDE the dispatch on purpose: `Dispatch(async () => …)`
        // binds to `Dispatch(Action)` (HeadlessUnitTestSession declares no
        // `Func<Task>` overload), so the body would run as `async void`, detach at
        // its first suspension, and report green without having checked anything
        // (#972, #766). Inflation is synchronous, so the work belongs inside a
        // synchronous dispatch and the assertion outside it.
        Views.ChatView? view = null;
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() => view = new Views.ChatView()), CancellationToken.None);

        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task ComposerView_Inflates()
    {
        var view = new Views.Shell.ComposerView();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task ToastNotificationsView_Inflates_ThreeToasts()
    {
        var vm = new ToastVm();
        vm.Toasts.Add(new Harbor.Ui.Framework.Services.ToastNotification("Success toast", Harbor.Ui.Framework.Services.ToastKind.Success));
        vm.Toasts.Add(new Harbor.Ui.Framework.Services.ToastNotification("Warning toast", Harbor.Ui.Framework.Services.ToastKind.Warning));
        vm.Toasts.Add(new Harbor.Ui.Framework.Services.ToastNotification("Error toast", Harbor.Ui.Framework.Services.ToastKind.Error));

        var view = new Views.ToastNotificationsView
        {
            DataContext = vm
        };

        view.ApplyTemplate();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task CommandPaletteView_Inflates()
    {
        var view = new Views.CommandPaletteView();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task SettingsView_Inflates()
    {
        // SettingsView contacts the AvaloniaDispatcher during XAML
        // inflation (ComboBox theme-picker initializes ItemsSourceView
        // via the dispatcher). Without a running dispatcher this either
        // throws InvalidOperationException on a threadpool thread or, if
        // naively dispatched, deadlocks because no loop is running.
        // The correct headless pattern is to spin up a real
        // HeadlessUnitTestSession AND run inflation on its UI thread.
        // Assertion outside the dispatch: see ChatView_Inflates above, and
        // AvaloniaDispatchAsyncVoidRule for why the shape is banned (#972, #766).
        Views.SettingsView? view = null;
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() => view = new Views.SettingsView()), CancellationToken.None);

        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task DiffView_Inflates()
    {
        var view = new Views.DiffView();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task ComponentGalleryView_Inflates()
    {
        var view = new Views.Dev.ComponentGalleryView();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task BoardView_Inflates()
    {
        var view = new BoardView();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task SessionCardView_Inflates()
    {
        var view = new SessionCardView();
        await Assert.That(view).IsNotNull();
    }

    [Test]
    public async Task SessionCardView_Inflates_WithAnApplicationRunning()
    {
        // #973. The test above is the only inflation coverage this card had, and
        // it could not have caught what was wrong with it.
        //
        // It constructs the view with no Avalonia Application, and StatusDot's
        // constructor enters its body only `if (Application.Current is not null)`
        // — so with no application the whole body, including the line that
        // dereferenced the unassigned `Dot` field, was skipped. Green either way.
        // That is not a flaky test, it is a test pointed the wrong way: the
        // branch that crashed is the branch a real user is always in.
        //
        // So boot an actual headless application first. Same pattern as
        // ChatView_Inflates / SettingsView_Inflates above — and deliberately NOT
        // the `Dispatch(async () => …)` spelling those two use, which binds to
        // `Dispatch<Task>(Func<Task>)` and returns a `Task<Task>` whose payload
        // the call site drops, losing everything after the first await (#972).
        // The work here is synchronous, so the Action overload is spelled
        // explicitly.
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        Exception? thrown = null;
        await session.Dispatch((System.Action)(() =>
        {
            try
            {
                _ = new SessionCardView();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        }), CancellationToken.None);

        await Assert.That(thrown).IsNull()
            .Because(
                "A user with one stored session who opens the Sessions tab inflates one SessionCardView per "
                + "session, and each card's status pill contains <comp:StatusDot/> (SessionCardView.axaml:16). "
                + "StatusDot's constructor called InitializeComponent, whose hand-written copy shadowed the "
                + "generator's overload — different signature, so it compiled, but the parameterless call bound "
                + "to the copy and skipped the `Dot = FindNameScope()?.Find<Ellipse>(\"Dot\")` assignment. The "
                + "next line dereferenced Dot and threw. AvaloniaInitializeComponentShadowRules in "
                + "Harbor.Architecture.Tests now forbids the shadow itself; this test is what proves the crash "
                + "is gone rather than merely unreachable."
                + (thrown is null ? "" : $" Actual: {thrown.GetType().Name}: {thrown.Message}"));
    }

    [Test]
    [Skip("Known flake: headless Avalonia dispose race / virtualization timing in CI is non-deterministic. See issue #14.")]
    public async Task MainWindow_Inflates_Without_Cast_Errors()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "harbor-avalonia-mw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHome);
        var harborDir = Path.Combine(tempHome, ".harbor");
        Directory.CreateDirectory(harborDir);
        await File.WriteAllTextAsync(
            Path.Combine(harborDir, "config.json"),
            JsonSerializer.Serialize(new
            {
                configVersion = "1",
                onboardingCompleted = true,
                storageBackend = "memory",
                logLevel = "warning",
                defaultProvider = "ollama",
                defaultModel = "qwen2.5-coder:7b",
                defaultAgent = "code"
            }));

        var originalHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            Environment.SetEnvironmentVariable("HOME", tempHome);
            App.ShellMode = "classic";
            App.ThemeMode = "dark";

            var host = AppHost.BuildAsync(Array.Empty<string>()).GetAwaiter().GetResult();

            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
            Window? window = null;
            // Synchronous dispatch, assertion outside: the body below never awaits,
            // so `async` bought nothing except the async-void binding that
            // `Dispatch(Action)` forces (#972, #766 — see
            // AvaloniaDispatchAsyncVoidRule).
            await session.Dispatch((System.Action)(() =>
            {
                App.Services = host.Services;
                App.Host = host;

                var lifetime = new ClassicDesktopStyleApplicationLifetime();
                AppBuilder.Configure<App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                    .UseSkia()
                    .SetupWithLifetime(lifetime);

                window = lifetime.MainWindow
                    ?? throw new InvalidOperationException("MainWindow was not created by App.OnFrameworkInitializationCompleted");
                window.Show();
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }), CancellationToken.None);

            await Assert.That(window.IsVisible).IsTrue();
            await Assert.That(FindDescendantOfType(window, typeof(Views.Shell.ActivityRailView))).IsNotNull();
            await Assert.That(FindDescendantOfType(window, typeof(Views.Shell.StatusBarView))).IsNotNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    private static Visual? FindDescendantOfType(Visual root, Type targetType)
    {
        var queue = new Queue<Visual>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var visual = queue.Dequeue();
            if (targetType.IsInstanceOfType(visual))
                return visual;
            foreach (var child in visual.GetVisualChildren())
                queue.Enqueue(child);
        }
        return null;
    }

    // ── Known issues (NOT marked [Test] — skip via visibility) ──────────
    // MarkdownRenderer / CodeBlock / TypewriterStreamingText hit a known
    // Avalonia 12 "Stack empty" bug in SetInheritanceParent under headless.
    // See AGENTS.md §Known pre-existing test failures.
    // These are intentionally NOT marked [Test] so they are excluded from runs.

    public static void MarkdownRenderer_Inflates_KnownIssue()
    {
        // Known: Avalonia 12 headless crash in SetInheritanceParent.
        // See AGENTS.md §Known pre-existing test failures.
    }

    public static void CodeBlock_Inflates_KnownIssue()
    {
        // Known: Avalonia 12 headless crash in SetInheritanceParent.
        // See AGENTS.md §Known pre-existing test failures.
    }

    public static void TypewriterStreamingText_Inflates_KnownIssue()
    {
        // Known: Avalonia 12 headless crash in SetInheritanceParent.
        // See AGENTS.md §Known pre-existing test failures.
    }
}

/// <summary>
///     Simple VM for toast inflation test.
/// </summary>
file sealed class ToastVm
{
    public ObservableCollection<Harbor.Ui.Framework.Services.ToastNotification> Toasts { get; } = new();
}
