using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Ui.Framework.Services;

public interface IRenderEngine
{
    void RenderChatLine(ChatLine line, ChatRole role);
    void RenderToolCall(string toolCallId);
    void RenderStreamingBuffer(string buffer);
    void RenderStatusMessage(string message);
    void Clear();
}
