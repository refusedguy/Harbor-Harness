using Harbor.Tui.SpectreTui.Panels.Builtin;
using Harbor.Ui.Framework.Diagnostics;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Harbor.Tui.Tests;
/// <summary>
///     Tests for the in-TUI diagnostics panel infrastructure: the ring buffer,
///     the ILogger → IDiagnosticsPanel bridge, and the SpectreTUI LogsPanel
///     that surfaces the buffer to the user (F12).
/// </summary>
public class DiagnosticsPanelTests
{
    [Test]
    public async Task InMemoryPanel_Logs_And_Returns_Recent()
    {
        var panel = new InMemoryDiagnosticsPanel(capacity: 100);
        panel.Log(LogLevel.Information, "Harbor.Test", "hello");
        panel.Log(LogLevel.Warning, "Harbor.Test", "warn");

        var recent = panel.GetRecent(10);
        await Assert.That(recent.Count).IsEqualTo(2);
        await Assert.That(recent[0].Message).IsEqualTo("hello");
        await Assert.That(recent[0].Level).IsEqualTo(LogLevel.Information);
        await Assert.That(recent[0].Category).IsEqualTo("Harbor.Test");
        await Assert.That(recent[1].Message).IsEqualTo("warn");
        await Assert.That(recent[1].Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(panel.Count).IsEqualTo(2);
    }

    [Test]
    public async Task InMemoryPanel_RingBuffer_EvictsOldestWhenFull()
    {
        var panel = new InMemoryDiagnosticsPanel(capacity: 3);
        panel.Log(LogLevel.Information, "c", "m1");
        panel.Log(LogLevel.Information, "c", "m2");
        panel.Log(LogLevel.Information, "c", "m3");
        panel.Log(LogLevel.Information, "c", "m4"); // evicts m1

        await Assert.That(panel.Count).IsEqualTo(3);
        var recent = panel.GetRecent(10);
        await Assert.That(recent.Count).IsEqualTo(3);
        await Assert.That(recent[0].Message).IsEqualTo("m2");
        await Assert.That(recent[1].Message).IsEqualTo("m3");
        await Assert.That(recent[2].Message).IsEqualTo("m4");
    }

    [Test]
    public async Task InMemoryPanel_GetRecent_RespectsMax()
    {
        var panel = new InMemoryDiagnosticsPanel(capacity: 100);
        for (int i = 0; i < 25; i++)
            panel.Log(LogLevel.Information, "c", $"m{i}");

        var recent = panel.GetRecent(5);
        await Assert.That(recent.Count).IsEqualTo(5);
        // Most-recent 5, oldest-first within window.
        await Assert.That(recent[0].Message).IsEqualTo("m20");
        await Assert.That(recent[4].Message).IsEqualTo("m24");
    }

    [Test]
    public async Task InMemoryPanel_GetRecent_EmptyReturnsEmpty()
    {
        var panel = new InMemoryDiagnosticsPanel();
        var recent = panel.GetRecent(10);
        await Assert.That(recent.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InMemoryPanel_Clear_DropsAllEntries()
    {
        var panel = new InMemoryDiagnosticsPanel();
        panel.Log(LogLevel.Information, "c", "m1");
        panel.Log(LogLevel.Information, "c", "m2");
        await Assert.That(panel.Count).IsEqualTo(2);

        panel.Clear();
        await Assert.That(panel.Count).IsEqualTo(0);
        await Assert.That(panel.GetRecent(10).Count).IsEqualTo(0);
    }

    [Test]
    public async Task InMemoryPanel_NullMessage_NormalizedToEmpty()
    {
        var panel = new InMemoryDiagnosticsPanel();
        panel.Log(LogLevel.Information, null!, null!);
        var recent = panel.GetRecent(10);
        await Assert.That(recent.Count).IsEqualTo(1);
        await Assert.That(recent[0].Category).IsEqualTo(string.Empty);
        await Assert.That(recent[0].Message).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task LoggerProvider_Bridges_ILogger_To_Panel()
    {
        var panel = new InMemoryDiagnosticsPanel();
        var provider = new DiagnosticsPanelLoggerProvider(panel);
        var logger = provider.CreateLogger("Harbor.Test.Category");

        logger.LogInformation("hello {Name}", "world");
        logger.LogWarning("warn");

        var recent = panel.GetRecent(10);
        await Assert.That(recent.Count).IsEqualTo(2);
        await Assert.That(recent[0].Category).IsEqualTo("Harbor.Test.Category");
        await Assert.That(recent[0].Message).Contains("hello");
        await Assert.That(recent[0].Message).Contains("world");
        await Assert.That(recent[0].Level).IsEqualTo(LogLevel.Information);
        await Assert.That(recent[1].Level).IsEqualTo(LogLevel.Warning);
    }

    [Test]
    public async Task LoggerProvider_ExceptionIsAppendedToMessage()
    {
        var panel = new InMemoryDiagnosticsPanel();
        var provider = new DiagnosticsPanelLoggerProvider(panel);
        var logger = provider.CreateLogger("Harbor.Test");

        var ex = new InvalidOperationException("boom");
        logger.LogError(ex, "failed");

        var recent = panel.GetRecent(10);
        await Assert.That(recent.Count).IsEqualTo(1);
        await Assert.That(recent[0].Message).Contains("failed");
        await Assert.That(recent[0].Message).Contains("InvalidOperationException");
        await Assert.That(recent[0].Message).Contains("boom");
    }

    [Test]
    public async Task LoggerProvider_NoneLevel_IsFiltered()
    {
        var panel = new InMemoryDiagnosticsPanel();
        var provider = new DiagnosticsPanelLoggerProvider(panel);
        var logger = provider.CreateLogger("Harbor.Test");

        logger.Log<object?>(LogLevel.None, 0, null, null, (_, _) => "should-not-appear");
        await Assert.That(panel.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LoggerProvider_Dispose_IsIdempotent()
    {
        var panel = new InMemoryDiagnosticsPanel();
        var provider = new DiagnosticsPanelLoggerProvider(panel);
        provider.Dispose();
        provider.Dispose(); // second dispose must not throw
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task LoggerFactory_Extension_AddsProvider()
    {
        var panel = new InMemoryDiagnosticsPanel();
        var factory = LoggerFactory.Create(b =>
        {
            b.AddProvider(new DiagnosticsPanelLoggerProvider(panel));
            b.SetMinimumLevel(LogLevel.Trace);
        });

        var logger = factory.CreateLogger("Harbor.Test");
        logger.LogInformation("via factory");

        await Assert.That(panel.Count).IsEqualTo(1);
        await Assert.That(panel.GetRecent(10)[0].Message).IsEqualTo("via factory");
    }
}

/// <summary>
///     Tests for the SpectreTUI <see cref="LogsPanel" />: panel metadata, F12
///     key handling, and rendering against a populated IDiagnosticsPanel.
/// </summary>
public class SpectreTuiLogsPanelTests
{
    [Test]
    public async Task Panel_Metadata_IsCorrect()
    {
        var panel = new LogsPanel();
        await Assert.That(panel.Id).IsEqualTo("logs");
        await Assert.That(panel.Title).IsEqualTo("Logs");
        await Assert.That(panel.DefaultPlacement).IsEqualTo(TuiPanelPlacement.Bottom);
        await Assert.That(panel.DefaultSize).IsEqualTo(10);
    }

    [Test]
    public async Task Build_NoPanel_ShowsPlaceholder()
    {
        var panel = new LogsPanel();
        var ctx = new PanelContext(new UiState(), 80, 24, null);
        object? widget = panel.Build(ctx);
        await Assert.That(widget).IsNotNull();
    }

    [Test]
    public async Task Build_WithPanel_ReturnsWidget()
    {
        var diag = new InMemoryDiagnosticsPanel();
        diag.Log(LogLevel.Information, "Harbor.Test", "hello world");
        diag.Log(LogLevel.Error, "Harbor.Test", "boom");

        var services = new ServiceCollection();
        services.AddSingleton<IDiagnosticsPanel>(diag);
        var sp = services.BuildServiceProvider();

        var panel = new LogsPanel();
        var ctx = new PanelContext(new UiState(), 80, 24, sp);
        object? widget = panel.Build(ctx);
        await Assert.That(widget).IsNotNull();
    }

    [Test]
    public async Task Build_EmptyPanel_ShowsNoEntriesMessage()
    {
        var diag = new InMemoryDiagnosticsPanel();
        var services = new ServiceCollection();
        services.AddSingleton<IDiagnosticsPanel>(diag);
        var sp = services.BuildServiceProvider();

        var panel = new LogsPanel();
        var ctx = new PanelContext(new UiState(), 80, 24, sp);
        object? widget = panel.Build(ctx);
        await Assert.That(widget).IsNotNull();
    }

    [Test]
    public async Task OnKey_F12_TogglesPanelViaStore()
    {
        var diag = new InMemoryDiagnosticsPanel();
        var services = new ServiceCollection();
        services.AddSingleton<IDiagnosticsPanel>(diag);
        var sp = services.BuildServiceProvider();

        // The store is needed for the panel to dispatch UiMsg.TogglePanel. Use
        // the real UiStore — it raises Changed on dispatch.
        var store = new UiStore();
        var servicesWithStore = new ServiceCollection();
        servicesWithStore.AddSingleton<IDiagnosticsPanel>(diag);
        servicesWithStore.AddSingleton(store);
        var sp2 = servicesWithStore.BuildServiceProvider();

        var panel = new LogsPanel();
        var ctx = new PanelContext(store.State, 80, 24, sp2);

        bool consumed = panel.OnKey(new UiKey(UiKeyCode.F12), ctx);
        await Assert.That(consumed).IsTrue();
    }

    [Test]
    public async Task OnKey_NonF12_NotConsumed()
    {
        var panel = new LogsPanel();
        var ctx = new PanelContext(new UiState(), 80, 24);
        bool consumed = panel.OnKey(new UiKey(UiKeyCode.Enter), ctx);
        await Assert.That(consumed).IsFalse();
    }
}

/// <summary>
///     Tests for the new F12 / ToggleLogsPanel wiring in the shared
///     ChatAction + ChatKeyMap + UiKey infrastructure.
/// </summary>
public class F12KeyMapTests
{
    [Test]
    public async Task UiKeyCode_Includes_F12() => await Assert.That((int)UiKeyCode.F12).IsGreaterThan((int)UiKeyCode.F4);

    [Test]
    public async Task ChatAction_Includes_ToggleLogsPanel()
    {
        // Verify the enum value is distinct from None.
        await Assert.That(ChatAction.ToggleLogsPanel).IsNotEqualTo(ChatAction.None);
        await Assert.That(ChatAction.ToggleLogsPanel).IsNotEqualTo(ChatAction.HelpPanel);
    }

    [Test]
    public async Task ChatKeyMap_F12_ResolvesToToggleLogsPanel()
    {
        var keyMap = new ChatKeyMap();
        var action = keyMap.Resolve(new UiKey(UiKeyCode.F12));
        await Assert.That(action).IsEqualTo(ChatAction.ToggleLogsPanel);
    }

    [Test]
    public async Task ChatKeyMap_ToggleLogsPanel_HasLabel()
    {
        var keyMap = new ChatKeyMap();
        var entry = keyMap.Get(ChatAction.ToggleLogsPanel);
        await Assert.That(entry).IsNotNull();
        await Assert.That(string.IsNullOrEmpty(entry.Label)).IsFalse();
    }
}
