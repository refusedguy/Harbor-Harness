namespace Harbor.Abstractions.Speech;

/// <summary>
///     Backend-agnostic speech-to-text seam. Audio capture and recognition belong
///     to the implementation; nothing about a model, a device or a network client
///     crosses this interface.
/// </summary>
/// <remarks>
/// <para>
///     A session is a push stream with two callbacks. The backend pushes zero or
///     more <c>onPartial</c> updates; each one REPLACES the previous partial, so
///     the host never has to deduplicate text. When audio ends, the backend pushes
///     at most one <c>onFinal</c> transcript. The host feeds both into its own
///     transcript buffer, which owns the commit-once and restore-on-cancel rules.
/// </para>
/// <para>
///     The returned task completes when the session ends, normally or through
///     the cancellation token. A backend must not throw for a cancellation it was
///     asked to perform. A real backend marshals its callbacks onto the host's
///     input thread: the composer buffer is not thread-safe.
/// </para>
/// </remarks>
public interface ISpeechToText
{
    /// <summary>
    ///     True when a real recognition backend is configured. A false value means
    ///     the implementation is an offline placeholder: the start/stop wiring works,
    ///     but no audio is transcribed.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    ///     Starts one dictation session.
    /// </summary>
    /// <param name="onPartial">Receives each replacement partial transcript.</param>
    /// <param name="onFinal">Receives the single final transcript, if one is produced.</param>
    /// <param name="cancellationToken">Signals the host's stop or cancel. It ends the session.</param>
    /// <returns>A task that completes when the session ends.</returns>
    Task StartAsync(
        Action<string> onPartial,
        Action<string> onFinal,
        CancellationToken cancellationToken = default);
}
