namespace Harbor.Abstractions.Tools;

/// <summary>
///     Media-type family predicates shared by Tools and CellForge (#1578):
///     five hand copies of <c>mime.StartsWith("image/", ...)</c> — three of
///     them with the inverted or stale line drift risk — replaced by
///     <see cref="IsImage" /> (plus <see cref="IsAudio" /> /
///     <see cref="IsVideo" />). null / no family match => false.
/// </summary>
public static class MimeTypes
{
    /// <summary>True when the media type falls in the image family ("image/*").</summary>
    public static bool IsImage(string? mime) =>
        mime is not null && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the media type falls in the audio family ("audio/*").</summary>
    public static bool IsAudio(string? mime) =>
        mime is not null && mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the media type falls in the video family ("video/*").</summary>
    public static bool IsVideo(string? mime) =>
        mime is not null && mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
}
