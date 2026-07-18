namespace Harbor.Desktop.Abstractions.Models;

/// <summary>
///     Visual severity for a toast notification. Each kind maps to a fixed
///     accent color from <c>Harbor.Desktop.DesignSystem</c>:
///     Info → Blue, Success → Green, Warning → Peach, Error → Red.
/// </summary>
public enum ToastKind
{
    /// <summary>Informational toast (Blue).</summary>
    Info,

    /// <summary>Success toast (Green).</summary>
    Success,

    /// <summary>Warning toast (Peach).</summary>
    Warning,

    /// <summary>Error toast (Red).</summary>
    Error,
}
