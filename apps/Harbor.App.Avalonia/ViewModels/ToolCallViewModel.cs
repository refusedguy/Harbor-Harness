using System;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Harbor.App.Avalonia.ViewModels;

/// <summary>
///     Status of a single tool invocation. Drives the color of the
///     status pill on <see cref="ToolCallViewModel"/>.
/// </summary>
public enum ToolCallStatus : byte
{
    /// <summary>Tool is currently executing.</summary>
    Running,

    /// <summary>Tool completed successfully.</summary>
    Success,

    /// <summary>Tool returned an error.</summary>
    Error,
}

/// <summary>
///     One tool call projected for the chat UI. Combines start + result
///     events for the same tool invocation into a single card state.
/// </summary>
/// <remarks>
///     <para>
///         The view-model is intentionally not a record: it needs
///         <see cref="ObservableProperty"/> source generators for
///         <c>IsExpanded</c> / <c>Status</c> / <c>Duration</c> /
///         <c>ArgsPreview</c> / <c>ResultPreview</c> so the
///         <c>ToolCallCardView</c> bindings re-evaluate when the
///         ChatViewModel mutates them in-place (e.g. when the matching
///         <c>ChatRole.ToolResult</c> line arrives).
///     </para>
///     <para>
///         <b>Id</b> is a stable identifier used to coalesce start/end
///         events. Currently derived from the tool name + index in the
///         transcript — a future refactor should use a real correlation
///         id from <c>AgentEvent</c>.
///     </para>
/// </remarks>
public sealed partial class ToolCallViewModel : ObservableObject
{
    /// <summary>Stable identifier used to coalesce start/end events.</summary>
    public string Id { get; init; } = string.Empty;

    [ObservableProperty]
    private string _toolName = string.Empty;

    [ObservableProperty]
    private string _iconText = "🔧";

    [ObservableProperty]
    private ToolCallStatus _status = ToolCallStatus.Running;

    [ObservableProperty]
    private TimeSpan _duration = TimeSpan.Zero;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _argsPreview = string.Empty;

    [ObservableProperty]
    private string _resultPreview = string.Empty;

    /// <summary>
    ///     Human-readable status pill label.
    /// </summary>
    public string StatusPill => Status switch
    {
        ToolCallStatus.Running => "● running",
        ToolCallStatus.Success => "✓ ok",
        ToolCallStatus.Error => "✗ err",
        _ => "?"
    };

    /// <summary>
    ///     Duration formatted for compact display (ms / s).
    /// </summary>
    public string DurationText => Duration.TotalMilliseconds < 1
        ? string.Empty
        : Duration.TotalSeconds < 1
            ? $"{Duration.TotalMilliseconds:F0}ms"
            : $"{Duration.TotalSeconds:F1}s";

    /// <summary>
    ///     Background brush for the status pill — yellow/green/red.
    ///     Resolved from <c>Application.Current.Resources</c> so it
    ///     tracks theme changes automatically.
    /// </summary>
    public IBrush StatusBackgroundBrush
    {
        get
        {
            string key = Status switch
            {
                ToolCallStatus.Running => "MochaYellow",
                ToolCallStatus.Success => "MochaGreen",
                ToolCallStatus.Error => "MochaRed",
                _ => "MochaOverlay2"
            };
            // Avalonia 11.2: TryFindResource extension was replaced by the
            // IResourceHost.TryGetResource instance method (takes a ThemeVariant).
            // We pass null for the theme to use the default-variant lookup —
            // the Mocha* brushes are defined in App.axaml under the default
            // theme so this resolves correctly regardless of dark/light.
            if (Application.Current?.Resources.TryGetResource(key, null, out var r) == true
                && r is IBrush brush)
            {
                return brush;
            }
            return Brushes.Gray;
        }
    }

    /// <summary>
    ///     Mark this tool call as completed with the given status.
    ///     Updates <see cref="Status"/> and notifies dependents
    ///     (<see cref="StatusPill"/> / <see cref="StatusBackgroundBrush"/>).
    /// </summary>
    public void Complete(ToolCallStatus status, string resultPreview, TimeSpan duration)
    {
        Status = status;
        ResultPreview = resultPreview;
        Duration = duration;
        // Trigger re-evaluation of computed properties.
        OnPropertyChanged(nameof(StatusPill));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(StatusBackgroundBrush));
    }
}