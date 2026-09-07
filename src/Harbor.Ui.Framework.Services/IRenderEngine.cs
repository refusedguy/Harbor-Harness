using Harbor.Abstractions.Models;
namespace Harbor.Ui.Framework.Services;

public interface IRenderEngine
{
    public void RenderChatLine(ChatLine line, ChatRole role);
    public void RenderToolCall(string toolCallId);
    public void RenderStreamingBuffer(string buffer);
    public void RenderStatusMessage(string message);
    public void Clear();
}
