using Harbor.App.Avalonia.Services;
using Harbor.Ui.Framework.Navigation;
using Harbor.Ui.Framework.Overlays;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;
using TUnit.Core;

namespace Harbor.App.Avalonia.Tests;

public class AvaloniaShellChromeTests
{
    private sealed class FakeContentHost : IContentHost
    {
        public string? LastRoute { get; private set; }
        public object? ActiveView { get; private set; }
        public IReadOnlyList<string> AvailableRoutes => Array.Empty<string>();

        public bool TryNavigate(string route)
        {
            LastRoute = route;
            ActiveView = route;
            return true;
        }

        public void NavigateTo(string route) => TryNavigate(route);
    }

    private sealed class FakeThemeService : IThemeService
    {
        public string Current => "dark";
        public bool IsDark => true;
        public void Apply(string theme) { }
        public void ApplyDark() { }
        public void ApplyLight() { }
        public void Toggle() => ToggleCalled = true;
        public void ApplyHds(string theme) { }
        public void SetThemeVariant(bool isDark) { }
        public bool ToggleCalled { get; private set; }
        public event EventHandler<string>? ThemeJsonApplied;
        public CSharpFunctionalExtensions.Result<string> LoadJson(string path) => CSharpFunctionalExtensions.Result.Success<string>(string.Empty);
        public CSharpFunctionalExtensions.Result ApplyJson(string json) => CSharpFunctionalExtensions.Result.Success();
        public System.IDisposable Watch(string path) => new NoopDisposable();
        private sealed class NoopDisposable : System.IDisposable { public void Dispose() { } }
    }

    private sealed class FakeOverlayStack : IOverlayStack
    {
        public string? Current { get; private set; }
        public IReadOnlyList<string> Stack => new List<string>();
        public event Action<string?, IReadOnlyList<string>>? Changed;
        public event Action<string?>? Popped;

        public void Push(string id)
        {
            Current = id;
            PushCalled = true;
            PushId = id;
        }

        public string? PopTop()
        {
            var top = Current;
            Current = null;
            PopTopCalled = true;
            return top;
        }

        public bool PushCalled { get; private set; }
        public string? PushId { get; private set; }
        public bool PopTopCalled { get; private set; }
    }

    private sealed class FakeLogger<T> : ILogger<T>, ILogger
    {
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable BeginScope<TState>(TState state) => null!;
    }

    private static AvaloniaShellChrome CreateChrome(
        IContentHost? contentHost = null,
        OverlayController? overlayController = null,
        IThemeService? theme = null,
        ILogger<AvaloniaShellChrome>? logger = null)
    {
        return new AvaloniaShellChrome(
            contentHost ?? new FakeContentHost(),
            overlayController ?? new OverlayController(new FakeOverlayStack()),
            theme ?? new FakeThemeService(),
            logger ?? new FakeLogger<AvaloniaShellChrome>());
    }

    [Test]
    public async Task Navigate_DelegatesToContentHost_TryNavigate()
    {
        var contentHost = new FakeContentHost();
        var chrome = CreateChrome(contentHost: contentHost);

        chrome.Navigate("chat");

        await Assert.That(contentHost.LastRoute).IsEqualTo("chat");
    }

    [Test]
    public async Task OpenOverlay_DelegatesToOverlayController_Open()
    {
        var overlayStack = new FakeOverlayStack();
        var overlayController = new OverlayController(overlayStack);
        var chrome = CreateChrome(overlayController: overlayController);

        chrome.OpenOverlay("settings");

        await Assert.That(overlayStack.PushCalled).IsTrue();
        await Assert.That(overlayStack.PushId).IsEqualTo("settings");
    }

    [Test]
    public async Task CloseOverlay_DelegatesToOverlayController_Close()
    {
        var overlayStack = new FakeOverlayStack();
        var overlayController = new OverlayController(overlayStack);
        bool setterCalled = false;
        overlayController.Register("settings", v => setterCalled = true);

        var chrome = CreateChrome(overlayController: overlayController);

        chrome.CloseOverlay("settings");

        await Assert.That(setterCalled).IsTrue();
    }

    [Test]
    public async Task CloseTopOverlay_DelegatesToOverlayController_CloseTop()
    {
        var overlayStack = new FakeOverlayStack();
        var overlayController = new OverlayController(overlayStack);
        bool setterCalled = false;
        overlayController.Register("settings", v => setterCalled = true);
        overlayController.Open("settings");

        var chrome = CreateChrome(overlayController: overlayController);

        var result = chrome.CloseTopOverlay();

        await Assert.That(result).IsTrue();
        await Assert.That(overlayStack.PopTopCalled).IsTrue();
        await Assert.That(setterCalled).IsTrue();
    }

    [Test]
    public async Task ToggleTheme_DelegatesToThemeService_Toggle()
    {
        var theme = new FakeThemeService();
        var chrome = CreateChrome(theme: theme);

        chrome.ToggleTheme();

        await Assert.That(theme.ToggleCalled).IsTrue();
    }
}
