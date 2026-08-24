namespace Harbor.Ui.Framework.Navigation;

/// <summary>
///     Canonical overlay identifiers passed to <see cref="IShellChrome.OpenOverlay" />
///     / <see cref="IShellChrome.CloseOverlay" /> and registered in the overlay
///     controller. String literals here were a typo away from a silently dead
///     button (33+ scattered copies) — always reference these constants so a
///     rename or typo stops compiling.
/// </summary>
public static class OverlayIds
{
    /// <summary>Command palette (Ctrl+K).</summary>
    public const string Palette = "palette";

    /// <summary>Settings modal.</summary>
    public const string Settings = "settings";

    /// <summary>Diff viewer.</summary>
    public const string Diff = "diff";

    /// <summary>Token usage breakdown.</summary>
    public const string TokenUsage = "tokenUsage";

    /// <summary>Provider browser.</summary>
    public const string ProviderBrowser = "providerBrowser";

    /// <summary>Model picker flyout.</summary>
    public const string ModelPicker = "modelPicker";

    /// <summary>Sessions list flyout.</summary>
    public const string SessionsFlyout = "sessionsFlyout";

    /// <summary>Focus-session overlay.</summary>
    public const string FocusSession = "focusSession";
}
