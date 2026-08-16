namespace Harbor.Tui.SpectreTui.View;
/// <summary>Local color role — mirrors ChatRole without leaking formatter deps wrong way.</summary>
internal enum ChatRoleColor : byte
{
    User,
    Assistant,
    Thinking,
    Tool,
    ToolResult,
    System,
    Error,
    Other
}
