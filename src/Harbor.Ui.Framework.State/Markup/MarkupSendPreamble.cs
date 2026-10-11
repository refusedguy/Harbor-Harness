namespace Harbor.Ui.Framework.State;

/// <summary>
///     Send-back preamble for the annotated screenshot (KILLER_FEATURES §2.7
///     Feature 14, issue #402 slice 2/2): the user turn that carries the baked
///     PNG also carries this text, naming the source screenshot, the
///     annotation count and the saved file path, so the model can reason about
///     what it is looking at (a bare image is ambiguous).
///     <para>
///         Pure strings — no I/O, no store — so the composition is
///         unit-testable and the host stays a thin glue over
///         <c>ImageAttachmentReader</c> (magic bytes, size cap, vision check).
///     </para>
/// </summary>
public static class MarkupSendPreamble
{
    /// <summary>
    ///     Build the user-turn text for a send. Never empty: blank names fall
    ///     back to <c>"image"</c>, negative counts clamp to zero.
    /// </summary>
    public static string Build(string sourceName, int annotationCount, string savedPath)
    {
        string name = string.IsNullOrWhiteSpace(sourceName) ? "image" : sourceName.Trim();
        int count = Math.Max(0, annotationCount);
        string annotations = count == 1 ? "1 annotation" : $"{count} annotations";
        return $"Annotated screenshot \"{name}\" ({annotations}), saved at {savedPath ?? string.Empty}. Review the marked areas.";
    }

    /// <summary>
    ///     Whether the overlay holds something sendable: open, with a saved
    ///     annotated copy and at least one live annotation. Baking is silent,
    ///     sending is never — this gate keeps the two apart (the host reports
    ///     which half is missing as inline overlay text).
    /// </summary>
    public static bool IsSendable(MarkupOverlayState markup) =>
        markup is { IsOpen: true }
        && !string.IsNullOrWhiteSpace(markup.SavedPath)
        && markup.Model.Items.Length > 0;
}
