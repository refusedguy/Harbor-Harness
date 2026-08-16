using System.Collections.Generic;
using System.Collections.ObjectModel;
using Harbor.Ui.Framework.Navigation;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Unit tests for the <see cref="IContentHost" /> navigation contract.
///     These tests do NOT reference the concrete <c>AvaloniaContentHost</c>
///     or any Avalonia view-model types — they validate the interface contract
///     against a minimal host implementation that mirrors the Avalonia route
///     set (7 routes). This isolates contract behaviour (return values,
///     no-throw semantics, initial view) from platform wiring.
/// </summary>
/// <remarks>
///     The contract under test is documented in <c>DECISIONS.md</c> —
///     "Shell architecture": <c>NavigateTo</c> must log and leave the
///     active view unchanged for unknown routes (no throw); <c>TryNavigate</c>
///     returns <c>true</c> for known routes and sets <c>ActiveView</c>,
///     <c>false</c> for unknown routes with no change to <c>ActiveView</c>.
/// </remarks>
public class ContentHostTests
{
    /// <summary>
    ///     Minimal <see cref="IContentHost" /> implementation that mirrors the
    ///     Avalonia route set (7 routes) without pulling in Avalonia view-models.
    /// </summary>
    private sealed class FakeContentHost : IContentHost
    {
        public FakeContentHost()
        {
            Chat = new object();
            Sessions = new object();
            CodeEditor = new object();
            Diff = new object();
            TokenUsage = new object();
            Settings = new object();
            Board = new object();

            _viewsByRoute = new Dictionary<string, object>
            {
                ["chat"]       = Chat,
                ["sessions"]   = Sessions,
                ["code"]       = CodeEditor,
                ["diff"]       = Diff,
                ["tokenUsage"] = TokenUsage,
                ["settings"]   = Settings,
                ["board"]      = Board,
            };

            ActiveView = Chat;
        }

        public object? ActiveView { get; private set; }

        public IReadOnlyList<string> AvailableRoutes { get; }
            = new ReadOnlyCollection<string>(new[]
            {
                "chat",
                "sessions",
                "code",
                "diff",
                "tokenUsage",
                "settings",
                "board",
            });

        public object Chat { get; }
        public object Sessions { get; }
        public object CodeEditor { get; }
        public object Diff { get; }
        public object TokenUsage { get; }
        public object Settings { get; }
        public object Board { get; }

        private readonly Dictionary<string, object> _viewsByRoute;

        public bool TryNavigate(string route)
        {
            if (string.IsNullOrEmpty(route) || !_viewsByRoute.TryGetValue(route, out var target))
            {
                return false;
            }

            ActiveView = target;
            return true;
        }

        public void NavigateTo(string route)
        {
            TryNavigate(route);
        }
    }

    // ── TryNavigate ────────────────────────────────────────────────

    [Test]
    public async Task TryNavigate_KnownRoute_ReturnsTrue_And_ChangesActiveView()
    {
        var host = new FakeContentHost();
        var initial = host.ActiveView;
        await Assert.That(host.TryNavigate("sessions")).IsTrue();
        await Assert.That(host.ActiveView).IsEqualTo(host.Sessions);
        await Assert.That(host.ActiveView).IsNotEqualTo(initial);
    }

    [Test]
    public async Task TryNavigate_AllKnownRoutes_ReturnsTrue_And_SetsCorrectView()
    {
        var host = new FakeContentHost();

        await Assert.That(host.TryNavigate("chat")).IsTrue();
        await Assert.That(host.ActiveView).IsEqualTo(host.Chat);

        await Assert.That(host.TryNavigate("code")).IsTrue();
        await Assert.That(host.ActiveView).IsEqualTo(host.CodeEditor);

        await Assert.That(host.TryNavigate("diff")).IsTrue();
        await Assert.That(host.ActiveView).IsEqualTo(host.Diff);

        await Assert.That(host.TryNavigate("tokenUsage")).IsTrue();
        await Assert.That(host.ActiveView).IsEqualTo(host.TokenUsage);

        await Assert.That(host.TryNavigate("settings")).IsTrue();
        await Assert.That(host.ActiveView).IsEqualTo(host.Settings);

        await Assert.That(host.TryNavigate("board")).IsTrue();
        await Assert.That(host.ActiveView).IsEqualTo(host.Board);
    }

    [Test]
    public async Task TryNavigate_UnknownRoute_ReturnsFalse_And_LeavesActiveViewUnchanged()
    {
        var host = new FakeContentHost();
        var before = host.ActiveView;

        await Assert.That(host.TryNavigate("nonexistent")).IsFalse();
        await Assert.That(host.ActiveView).IsEqualTo(before);
    }

    [Test]
    public async Task TryNavigate_NullRoute_ReturnsFalse_And_LeavesActiveViewUnchanged()
    {
        var host = new FakeContentHost();
        var before = host.ActiveView;

        await Assert.That(host.TryNavigate(null!)).IsFalse();
        await Assert.That(host.ActiveView).IsEqualTo(before);
    }

    // ── NavigateTo ─────────────────────────────────────────────────

    [Test]
    public async Task NavigateTo_KnownRoute_DoesNotThrow_And_ChangesActiveView()
    {
        var host = new FakeContentHost();
        var initial = host.ActiveView;

        host.NavigateTo("code");

        await Assert.That(host.ActiveView).IsEqualTo(host.CodeEditor);
        await Assert.That(host.ActiveView).IsNotEqualTo(initial);
    }

    [Test]
    public async Task NavigateTo_UnknownRoute_DoesNotThrow_And_LeavesActiveViewUnchanged()
    {
        var host = new FakeContentHost();
        var before = host.ActiveView;

        host.NavigateTo("bogus");

        await Assert.That(host.ActiveView).IsEqualTo(before);
    }

    // ── AvailableRoutes ────────────────────────────────────────────

    [Test]
    public async Task AvailableRoutes_ContainsExpectedRoutes()
    {
        var host = new FakeContentHost();

        await Assert.That(host.AvailableRoutes).Contains("chat");
        await Assert.That(host.AvailableRoutes).Contains("sessions");
        await Assert.That(host.AvailableRoutes).Contains("code");
        await Assert.That(host.AvailableRoutes).Contains("settings");
        await Assert.That(host.AvailableRoutes).Contains("board");
    }

    [Test]
    public async Task AvailableRoutes_HasSevenRoutes()
    {
        var host = new FakeContentHost();

        await Assert.That(host.AvailableRoutes).HasCount(7);
    }

    // ── ActiveView default ─────────────────────────────────────────

    [Test]
    public async Task ActiveView_DefaultsToChatView_NotNull_AfterConstruction()
    {
        var host = new FakeContentHost();

        await Assert.That(host.ActiveView).IsNotNull();
        await Assert.That(host.ActiveView).IsEqualTo(host.Chat);
    }

    // ── Round-trip ─────────────────────────────────────────────────

    [Test]
    public async Task NavigateTo_Then_TryNavigate_Consistent()
    {
        var host = new FakeContentHost();

        host.NavigateTo("sessions");
        await Assert.That(host.ActiveView).IsEqualTo(host.Sessions);

        await Assert.That(host.TryNavigate("code")).IsTrue();
        await Assert.That(host.ActiveView).IsEqualTo(host.CodeEditor);
    }
}
