// DictationBuffer.cs — the durable, domain-neutral half of #398 (killer-feature
// slice of #23). Audio capture and speech recognition belong to the host; this
// type owns only the text contract a composer needs:
//
//   * a partial transcript REPLACES the previous partial, so a stream of
//     updates never duplicates text in the prompt buffer;
//   * the final transcript is committed exactly once;
//   * cancelling restores the pre-dictation buffer;
//   * starting while the agent streams is refused without changing state;
//   * a visible status label is derived from one status enum.
//
// No Harbor, provider or key type appears here. It is deliberately a model, not
// a widget: the host paints StatusLabel wherever it paints composer chrome.
// Wiring it to ISpeechToText, the hotkey and the Whisper.net backend is a
// follow-up slice; this one ships the part that survives backend swaps.

namespace Harbor.Tui.CellForge.Generic.Widgets;

/// <summary>Visible state of one dictation session.</summary>
public enum DictationStatus
{
    /// <summary>No session; no indicator is shown.</summary>
    Idle,

    /// <summary>Audio is being captured; no transcript yet.</summary>
    Listening,

    /// <summary>A partial or final transcript is being applied.</summary>
    Transcribing,

    /// <summary>The session failed; <see cref="DictationBuffer.Error"/> explains why.</summary>
    Error,
}

/// <summary>
/// Domain-neutral transcript buffer for a prompt composer. The host supplies
/// transcripts and decides when to start; this type owns the baseline, the
/// in-flight partial, the commit count and the status. Text is read through
/// <see cref="Text"/> so a caller never has to know whether a session is live.
/// </summary>
public sealed class DictationBuffer
{
    private string _baseline;
    private string _partial = "";
    private string? _error;
    private DictationStatus _status = DictationStatus.Idle;
    private int _commits;

    /// <summary>
    /// Creates a buffer with the text that was in the composer before dictation.
    /// </summary>
    /// <param name="baseline">Pre-dictation composer text; null is treated as empty.</param>
    public DictationBuffer(string? baseline = null)
    {
        _baseline = baseline ?? "";
    }

    /// <summary>Current status of the session.</summary>
    public DictationStatus Status => _status;

    /// <summary>Failure message when <see cref="Status"/> is <see cref="DictationStatus.Error"/>; otherwise null.</summary>
    public string? Error => _error;

    /// <summary>True while a session owns the composer text.</summary>
    public bool IsActive => _status is DictationStatus.Listening or DictationStatus.Transcribing;

    /// <summary>Number of final transcripts committed. Never more than one per session.</summary>
    public int CommitCount => _commits;

    /// <summary>Pre-dictation text, restored by <see cref="Cancel"/>.</summary>
    public string Baseline => _baseline;

    /// <summary>
    /// Short glyph-free label for the status indicator: empty when idle,
    /// otherwise "listening", "transcribing" or "error".
    /// </summary>
    public string StatusLabel => _status switch
    {
        DictationStatus.Listening => "listening",
        DictationStatus.Transcribing => "transcribing",
        DictationStatus.Error => "error",
        _ => "",
    };

    /// <summary>
    /// Text the composer should display right now: baseline plus the current
    /// partial while active, baseline otherwise (including on error and cancel).
    /// </summary>
    public string Text => IsActive ? Join(_baseline, _partial) : _baseline;

    /// <summary>
    /// Starts a session from the current composer text.
    /// </summary>
    /// <param name="baseline">Composer text to restore on cancel.</param>
    /// <param name="agentStreaming">True when the agent is streaming; dictation is refused then.</param>
    /// <param name="rejection">Actionable message when the start is refused; null on success.</param>
    /// <returns>True when the session started.</returns>
    public bool TryStart(string? baseline, bool agentStreaming, out string? rejection)
    {
        if (agentStreaming)
        {
            rejection = "Dictation is unavailable while the agent is streaming.";
            return false;
        }

        if (IsActive)
        {
            rejection = "Dictation is already running.";
            return false;
        }

        _baseline = baseline ?? "";
        _partial = "";
        _error = null;
        _status = DictationStatus.Listening;
        rejection = null;
        return true;
    }

    /// <summary>
    /// Applies a partial transcript. Each call recomputes from the baseline, so
    /// a later update replaces the earlier one instead of appending to it.
    /// Ignored when no session is active.
    /// </summary>
    public void AcceptPartial(string? transcript)
    {
        if (!IsActive)
        {
            return;
        }

        _partial = transcript ?? "";
        _status = DictationStatus.Transcribing;
    }

    /// <summary>
    /// Commits the final transcript and ends the session. Only the first call
    /// in a session commits; later calls are ignored.
    /// </summary>
    public void AcceptFinal(string? transcript)
    {
        if (!IsActive)
        {
            return;
        }

        _baseline = Join(_baseline, transcript ?? "");
        _partial = "";
        _error = null;
        _status = DictationStatus.Idle;
        _commits++;
    }

    /// <summary>
    /// Ends the session without committing, restoring the pre-dictation text.
    /// Also clears an error state so the indicator disappears.
    /// </summary>
    public void Cancel()
    {
        if (!IsActive && _status != DictationStatus.Error)
        {
            return;
        }

        _partial = "";
        _error = null;
        _status = DictationStatus.Idle;
    }

    /// <summary>
    /// Marks the active session as failed. The composer falls back to the
    /// baseline while the indicator shows "error" with <paramref name="message"/>.
    /// </summary>
    public void Fail(string message)
    {
        if (!IsActive)
        {
            return;
        }

        _partial = "";
        _error = message ?? "";
        _status = DictationStatus.Error;
    }

    private static string Join(string baseline, string transcript)
    {
        if (baseline.Length == 0)
        {
            return transcript;
        }

        return transcript.Length == 0 ? baseline : baseline + " " + transcript;
    }
}
