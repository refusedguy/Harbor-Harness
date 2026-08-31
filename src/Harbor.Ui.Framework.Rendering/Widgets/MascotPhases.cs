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
