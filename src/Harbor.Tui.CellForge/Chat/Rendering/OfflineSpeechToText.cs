using Harbor.Abstractions.Speech;

namespace Harbor.Tui.CellForge.Rendering;

/// <summary>
///     Offline placeholder for <see cref="ISpeechToText"/> (issue #2541). It owns no
///     microphone and loads no model, so it captures no audio and produces no
///     transcript. It exists so the composer's Ctrl+D start/stop path is wired and
///     runnable today; a real Whisper-style backend is a separate slice.
/// </summary>
/// <remarks>
///     The session is held open until the host cancels it (Ctrl+D stop or cancel),
///     then an empty final is published so the buffer's commit-once path still runs.
///     An empty final leaves the pre-dictation composer text untouched.
/// </remarks>
public sealed class OfflineSpeechToText : ISpeechToText
{
    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public async Task StartAsync(
        Action<string> onPartial,
        Action<string> onFinal,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A stop is a normal end of session. The empty final commits nothing,
            // so cancel and stop differ only in whether the host kept the buffer.
            onFinal(string.Empty);
        }
    }
}
