using System;

namespace Harbor.Abstractions.Contracts;

/// <summary>
///     Applied to individual members of a mood enum to indicate which
///     frame-bank field supplies the animation frames for that mood.
///     <see cref="Harbor.CodeGen.MoodFrameGenerator" /> scans for enums whose members carry
///     this attribute and emits a dispatch table replacing the manual
///     <c>switch</c> expression in the host class.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class MoodFrameAttribute : Attribute
{
    /// <summary>
    ///     Gets or sets the name of the static field holding the frame bank
    ///     for this mood value. Required.
    /// </summary>
    public string FrameBank { get; }

    /// <summary>
    ///     Gets or sets the name of the static field holding the panel ear
    ///     row for this mood. Optional — only needed when the host dispatches
    ///     panel rows.
    /// </summary>
    public string? PanelEars { get; set; }

    /// <summary>
    ///     Gets or sets the name of the static field holding the panel paw
    ///     row for this mood. Optional.
    /// </summary>
    public string? PanelPaws { get; set; }

    /// <summary>
    ///     Initializes a new instance of <see cref="MoodFrameAttribute" />.
    /// </summary>
    /// <param name="frameBank">The name of the frame-bank static field.</param>
    public MoodFrameAttribute(string frameBank)
    {
        FrameBank = frameBank;
    }
}
