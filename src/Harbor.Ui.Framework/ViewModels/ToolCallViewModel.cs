using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
namespace Harbor.Ui.Framework.ViewModels;
/// <summary>
///     One tool call projected for the chat UI. Combines start + result
///     events for the same tool invocation into a single card state.
/// </summary>
/// <remarks>
///     <para>
///         Platform-agnostic — extracted from the Avalonia-specific
///         <c>Harbor.App.Avalonia.ViewModels.ToolCallViewModel</c> so the
///         same projection logic is reusable by WPF/MAUI/Blazor apps.
///         Concrete UIs map <see cref="StatusBrushKey" /> / <see cref="StatusPill" />
///         to actual brushes / labels via their own resource lookups
///         (e.g. <c>BrushKeyConverter</c> on Avalonia).
///     </para>
///     <para>
///         The view-model is intentionally not a record: it needs
///         <see cref="ObservableProperty" /> source generators for
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

    [ObservableProperty]
    private string _argsPreview = string.Empty;

    [ObservableProperty]
    private TimeSpan _duration = TimeSpan.Zero;

    [ObservableProperty]
    private string _iconText = "?";

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _resultPreview = string.Empty;

    [ObservableProperty]
    private ToolCallStatus _status = ToolCallStatus.Running;

    [ObservableProperty]
    private string _toolName = string.Empty;

    [ObservableProperty]
    private bool _isDiffTool;

    [ObservableProperty]
    private string? _diffFilePath;

    [ObservableProperty]
    private string? _diffPreview;

    [ObservableProperty]
    private string? _diffFull;

    /// <summary>Stable identifier used to coalesce start/end events.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    ///     Human-readable status pill label.
    /// </summary>
    public string StatusPill => Status switch
    {
        ToolCallStatus.Running => "running",
        ToolCallStatus.Success => "ok",
        ToolCallStatus.Error => "err",
        _ => "?"
    };

    /// <summary>
    ///     Duration formatted for compact display (ms / s).
    /// </summary>
    public string DurationText => Duration.TotalMilliseconds < 1
        ? string.Empty
        : Duration.TotalSeconds < 1
            ? $"{Duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}ms"
            : $"{Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s";

    /// <summary>
    ///     Resource-key string for the status pill background brush.
    ///     Platform apps resolve this to an <c>IBrush</c> /
    ///     <c>Brush</c> via a <c>BrushKeyConverter</c>-style lookup so
    ///     theme changes are tracked automatically without this VM
    ///     depending on any UI framework.
    /// </summary>
    public string StatusBrushKey => Status switch
    {
        ToolCallStatus.Running => "MochaYellow",
        ToolCallStatus.Success => "MochaGreen",
        ToolCallStatus.Error => "MochaRed",
        _ => "MochaOverlay2"
    };

    /// <summary>
    ///     Mark this tool call as completed with the given status.
    ///     Updates <see cref="Status" /> and notifies dependents
    ///     (<see cref="StatusPill" /> / <see cref="StatusBrushKey" />).
    /// </summary>
    public void Complete(ToolCallStatus status, string resultPreview, TimeSpan duration)
    {
        Status = status;
        ResultPreview = resultPreview;
        Duration = duration;
        // Trigger re-evaluation of computed properties.
        this.OnPropertyChanged(nameof(StatusPill));
        this.OnPropertyChanged(nameof(DurationText));
        this.OnPropertyChanged(nameof(StatusBrushKey));
    }
}
