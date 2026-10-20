using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using Harbor.App.Avalonia.Views;
using Harbor.App.Avalonia.Views.Controls;
using Harbor.Ui.Framework;
using Harbor.Ui.Framework.Services;
using Harbor.Ui.Framework.ViewModels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Shared base of the two golden lanes: it renders a scene in the headless
///     compositor and compares the frame's content snapshot against the
///     committed baseline.
/// </summary>
/// <remarks>
///     <para>
///         The scenes live here and not in the test bodies on purpose. The
///         bodies are one line of data each — the shape a data-driven case
///         has — so the twelve golden scenarios do not read as twelve copies
///         of the same twenty lines, and the duplicate-test gate
///         (<c>tools/check-test-dup.py</c>) does not count them as one.
///     </para>
/// </remarks>
public abstract class GoldenTestBase : ComponentTestBase
{
    /// <summary>The accent the sparkline scenarios paint with (Mocha green).</summary>
    private static readonly SolidColorBrush SparklineStroke = new(Color.FromRgb(0xA6, 0xE3, 0xA1));

    /// <summary>
    ///     Render <paramref name="content" /> in a fixed-size golden window
    ///     and return the frame's content snapshot.
    /// </summary>
    /// <remarks>
    ///     Everything runs in ONE synchronous UI-thread delegate: pin theme →
    ///     construct → capture → close. The Avalonia dispatcher cannot pump
    ///     mid-delegate, so no orphaned continuation from another host can
    ///     flip the theme between the pin and the capture.
    /// </remarks>
    protected static string CaptureFrame(Func<Control> content, double width, double height) => UI<string>(() =>
    {
        GoldenFrame.PinDarkTheme();
        var window = GoldenFrame.CreateHostWindow(content(), width, height);
        var frame = GoldenFrame.CaptureSettledSnapshot(window);
        window.Close();
        return frame;
    });

    /// <summary>
    ///     Compare the frame's snapshot with the committed baseline and
    ///     assert that the scenario's own text is still in the frame — the
    ///     assertion that does not depend on the baseline, so a baseline
    ///     regenerated from a frame that lost its content cannot pass.
    /// </summary>
    /// <remarks>
    ///     The text must be VISIBLE in the frame: the snapshot carries what
    ///     the user sees, and a collapsed <c>Expander</c> keeps its content out
    ///     of the visual tree (the tool-call card's args and result live
    ///     there — the card renders collapsed until it is expanded, so the
    ///     golden pins the card as the product draws it).
    /// </remarks>
    protected static async Task VerifyGoldenAsync(string baselinePath, string snapshot, string expectedText)
    {
        GoldenFrame.VerifySnapshot(baselinePath, snapshot);
        await Assert.That(snapshot.Contains(expectedText, StringComparison.Ordinal)).IsTrue()
            .Because($"the golden frame must still carry the text of its scenario ('{expectedText}')");
    }

    /// <summary>A tool-call card in the given state.</summary>
    protected static ToolCallCardView ToolCard(ToolCallState status, string result, string icon = "E") => new()
    {
        DataContext = new ToolCallViewModel
        {
            ToolName = "edit",
            IconText = icon,
            ArgsPreview = "{\"path\": \"src/App.cs\"}",
            ResultPreview = result,
            Status = status,
            Duration = TimeSpan.FromMilliseconds(340),
        },
    };

    /// <summary>A toast surface carrying the given notifications.</summary>
    protected static ToastNotificationsView ToastView(params (ToastKind Kind, string Message)[] toasts)
    {
        var host = new GoldenToastHost();
        foreach ((ToastKind kind, string message) in toasts)
        {
            host.Toasts.Add(new ToastNotification(message, kind));
        }

        return new ToastNotificationsView { DataContext = host };
    }

    /// <summary>The three notifications the toast scenarios show.</summary>
    protected static (ToastKind Kind, string Message)[] ThreeToasts() =>
    [
        (ToastKind.Info, "Info: connection established."),
        (ToastKind.Success, "Success: file saved."),
        (ToastKind.Error, "Error: provider returned 503."),
    ];

    /// <summary>The streaming text in its IDLE state — the blink cursor hidden.</summary>
    protected static TypewriterStreamingText TypewriterView(string text) => new()
    {
        Text = text,
        IsStreaming = false,
    };

    /// <summary>A sparkline with the given values.</summary>
    protected static Sparkline SparklineView(params double[] values) => new()
    {
        Values = values,
        StrokeBrush = SparklineStroke,
        Width = 160,
        Height = 40,
    };
}

/// <summary>Minimal toast host for the golden frames — same shape the
/// production MainViewModel exposes (Toasts collection only).</summary>
file sealed class GoldenToastHost
{
    public ObservableCollection<ToastNotification> Toasts { get; } = new();
}
