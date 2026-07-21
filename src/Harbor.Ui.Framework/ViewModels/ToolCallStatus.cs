namespace Harbor.Ui.Framework.ViewModels;
/// <summary>
///     Status of a single tool invocation. Drives the color of the
///     status pill on <c>ToolCallViewModel</c>. Pure enum — no UI-framework
///     dependency — extracted here so WPF/MAUI/Blazor apps can reuse the
///     same status vocabulary.
/// </summary>
public enum ToolCallStatus : byte
{
    /// <summary>Tool is currently executing.</summary>
    Running,

    /// <summary>Tool completed successfully.</summary>
    Success,

    /// <summary>Tool returned an error.</summary>
    Error
}
