namespace Harbor.Abstractions.Models;

public enum ChatRole : byte
{
    User,
    Assistant,
    Thinking,
    Tool,
    ToolResult,
    System,
    Error
}

public readonly record struct ChatLine(ChatRole Role, string Text, string? ToolCallId = null, string? MessageId = null, DateTime TimestampUtc = default);
