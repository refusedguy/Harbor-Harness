namespace Harbor.Application.Telemetry;

public static class GenAiTags
{
    public const string AgentName = "gen_ai.agent.name";
    public const string RequestModel = "gen_ai.request.model";
    public const string ProviderName = "gen_ai.provider.name";
    public const string ToolName = "gen_ai.tool.name";
    public const string PromptTokens = "gen_ai.prompt.tokens";
    public const string CompletionTokens = "gen_ai.completion.tokens";
}
