namespace Harbor.Ui.Framework.Rendering.Widgets;

/// <summary>
/// Fine-grained agent phase behind the coarse <see cref="StatusBarMode" />:
/// the mode says «running», the phase says «thinking vs tool-call», and the
/// end-of-run events say «errored vs succeeded». The host sets it alongside
/// the mode; presentation layers (mascot) map it to visuals. Zero state cost —
/// a byte-sized enum on the status view model.
/// </summary>
public enum AgentPhase : byte
{
    /// <summary>Derive everything from <see cref="StatusViewModel.Mode" /> alone.</summary>
    Auto = 0,

    /// <summary>LLM is streaming text.</summary>
    Thinking,

    /// <summary>A tool is executing.</summary>
    ToolCall,

    /// <summary>The last run failed — presentation may react briefly.</summary>
    Errored,

    /// <summary>The last run finished clean — presentation may react briefly.</summary>
    Succeeded,
}

/// <summary>
/// One-shot event reaction for the mascot (sprint mascot-brand T3): a short
/// overlay sequence the mascot plays when a notable event lands — error blink,
/// success bounce, approval wiggle. Not a mood: it overrides the current mood
/// frames for a few ticks, then the mood resumes.
/// </summary>
public enum MascotReaction : byte
{
    /// <summary>No reaction armed.</summary>
    None = 0,

    /// <summary>An error event fired — X-eyes blink sequence.</summary>
    ErrorBlink,

    /// <summary>The run finished clean — bounce sequence.</summary>
    SuccessBounce,

    /// <summary>An approval gate opened — wide-eyes wiggle sequence.</summary>
    ApprovalWiggle,
}
