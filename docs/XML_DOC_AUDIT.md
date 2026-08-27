# XML Documentation Audit

## Summary

- **Total members audited:** 2861
- **With XML doc:** 2021 (70%)
- **Without XML doc:** 840 (29%)

### Priority Breakdown

| Priority | Total | With Doc | Without Doc |
|----------|-------|----------|-------------|
| HIGH     | 1111     | 808       | 303         |
| MED      | 765    | 543       | 222         |
| LOW      | 985    | 670       | 315         |

## Project: Harbor.Abstractions

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Abstractions/Agents/AgentDefinition.cs | 43 | record AgentDefinition | YES | LOW |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 64 | public static AgentDefinition CodeDefault(string model, string providerId) => new( | YES | MED |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 78 | public static AgentDefinition PlanDefault(string model, string providerId) => new( | YES | MED |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 106 | public static AgentDefinition ExploreDefault(string model, string providerId) => new( | YES | MED |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 137 | public AgentDefinition WithModel(string model, string providerId) => this with | YES | MED |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 148 | public AgentDefinition WithPermission(PermissionRuleset permission) => this with | YES | MED |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 170 | interface IAgentRegistry | YES | HIGH |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 176 | public IReadOnlyList<AgentDefinition> GetAllAgents(); | YES | HIGH |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 183 | public Result<AgentDefinition> GetAgent(AgentName name); | YES | HIGH |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 190 | public Result Register(AgentDefinition agent); | YES | HIGH |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 197 | public Result Unregister(AgentName name); | YES | MED |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 209 | interface IAgentRegistryBuilder | YES | HIGH |
| Harbor.Abstractions/Agents/AgentDefinition.cs | 215 | public void AddAgent(AgentDefinition agent); | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 30 | interface IAgentRunner | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 37 | CancellationTokenSource AbortSource | YES | LOW |
| Harbor.Abstractions/Agents/IAgent.cs | 45 | public Task<Result> PromptAsync(string text, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 52 | public Task WaitForIdleAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 74 | public void ResetAbortSource(); | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 104 | interface IAgent | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 110 | AgentState State | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 118 | public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener); | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 126 | public Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 133 | public void Initialize(Session session, AgentDefinition agent); | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 139 | public void Steer(AgentMessage message); | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 151 | record AgentState | YES | LOW |
| Harbor.Abstractions/Agents/IAgent.cs | 165 | public static AgentState Idle(string sessionId, AgentDefinition agent) => new( | YES | MED |
| Harbor.Abstractions/Agents/IAgent.cs | 191 | interface IAgentLoop | YES | HIGH |
| Harbor.Abstractions/Agents/IAgent.cs | 200 | public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Agents/ISubAgentRunner.cs | 17 | record SubAgentRunRequest | YES | LOW |
| Harbor.Abstractions/Agents/ISubAgentRunner.cs | 36 | record SubAgentRunResult | YES | LOW |
| Harbor.Abstractions/Agents/ISubAgentRunner.cs | 60 | interface ISubAgentRunner | YES | HIGH |
| Harbor.Abstractions/Agents/ISubAgentRunner.cs | 67 | bool CanSpawn | YES | LOW |
| Harbor.Abstractions/Agents/ISubAgentRunner.cs | 78 | public Task<Result<SubAgentRunResult>> RunAsync(AgentDefinition agent, SubAgentRunRequest request, CancellationToken ct... | YES | HIGH |
| Harbor.Abstractions/Events/IEventBus.cs | 20 | interface IEventBus | YES | HIGH |
| Harbor.Abstractions/Events/IEventBus.cs | 27 | public Task PublishAsync(AgentEvent @event, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Events/IEventBus.cs | 34 | public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler); | YES | HIGH |
| Harbor.Abstractions/Events/IEventBus.cs | 49 | public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents); | YES | HIGH |
| Harbor.Abstractions/Events/IEventBus.cs | 59 | interface IEventSubscriber | YES | HIGH |
| Harbor.Abstractions/Events/IEventBus.cs | 66 | public Task SendAsync(AgentEvent @event, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Events/IEventBusMiddleware.cs | 26 | interface IEventBusMiddleware | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 29 | interface IPlugin | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 34 | string Name | YES | LOW |
| Harbor.Abstractions/Plugins/IPlugin.cs | 39 | Version Version | YES | LOW |
| Harbor.Abstractions/Plugins/IPlugin.cs | 44 | Version RequiredHarborVersion | YES | LOW |
| Harbor.Abstractions/Plugins/IPlugin.cs | 49 | string Description | YES | LOW |
| Harbor.Abstractions/Plugins/IPlugin.cs | 55 | public void Initialize(PluginContext context); | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 61 | public Task ShutdownAsync(CancellationToken cancellationToken = default); | YES | MED |
| Harbor.Abstractions/Plugins/IPlugin.cs | 67 | interface IToolPlugin | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 73 | public void RegisterTools(IToolRegistryBuilder builder); | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 79 | interface IProviderPlugin | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 85 | public void RegisterProviders(IProviderRegistryBuilder builder); | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 91 | interface IAgentPlugin | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 97 | public void RegisterAgents(IAgentRegistryBuilder builder); | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 103 | class PluginContext | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 138 | Version HarborVersion | YES | LOW |
| Harbor.Abstractions/Plugins/IPlugin.cs | 155 | interface IPluginHost | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 160 | IReadOnlyList<IPlugin> LoadedPlugins | YES | LOW |
| Harbor.Abstractions/Plugins/IPlugin.cs | 167 | public Result LoadPlugin(string path); | YES | HIGH |
| Harbor.Abstractions/Plugins/IPlugin.cs | 174 | public Result UnloadPlugin(string name); | YES | MED |
| Harbor.Abstractions/Plugins/IPlugin.cs | 180 | public Task ShutdownAllAsync(CancellationToken ct = default); | YES | MED |
| Harbor.Abstractions/Providers/IAuthResolver.cs | 9 | interface IAuthResolver | YES | HIGH |
| Harbor.Abstractions/Providers/IAuthResolver.cs | 12 | public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Providers/ILlmClient.cs | 21 | interface ILlmClient | YES | HIGH |
| Harbor.Abstractions/Providers/ILlmClient.cs | 26 | ProviderId ProviderId | YES | HIGH |
| Harbor.Abstractions/Providers/ILlmClient.cs | 35 | public IAsyncEnumerable<LlmEvent> StreamAsync( | YES | MED |
| Harbor.Abstractions/Providers/ILlmClient.cs | 44 | public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default); | YES | HIGH |
| Harbor.Abstractions/Providers/ILlmClient.cs | 63 | record LlmRequest | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 81 | record LlmMessage | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 86 | string Role | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 93 | record LlmUserMessage | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 103 | public static LlmUserMessage Text(string text) => | YES | MED |
| Harbor.Abstractions/Providers/ILlmClient.cs | 112 | record LlmAssistantMessage | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 127 | record LlmToolResultMessage | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 140 | record LlmContentBlock | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 145 | string Type | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 152 | record LlmTextBlock | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 163 | record LlmImageBlock | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 175 | record LlmToolCallBlock | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 187 | record LlmToolResultBlock | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 197 | record LlmThinkingBlock | YES | LOW |
| Harbor.Abstractions/Providers/ILlmClient.cs | 209 | record ToolDefinition | YES | LOW |
| Harbor.Abstractions/Providers/IProviderFactory.cs | 6 | interface IProviderFactory | **NO** | HIGH |
| Harbor.Abstractions/Providers/IProviderHealthCheck.cs | 12 | interface IProviderHealthCheck | YES | HIGH |
| Harbor.Abstractions/Providers/IProviderHealthCheck.cs | 29 | record struct | YES | LOW |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 21 | interface IProviderRegistry | YES | HIGH |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 27 | public IReadOnlyList<ProviderId> GetRegisteredProviderIds(); | YES | HIGH |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 35 | public Result<ILlmClient> GetClient(ProviderId providerId); | YES | HIGH |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 42 | public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default); | YES | HIGH |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 55 | public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellation... | YES | HIGH |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 62 | public void Register(ProviderId providerId, Func<ILlmClient> factory); | YES | HIGH |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 69 | public Result Unregister(ProviderId providerId); | YES | MED |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 80 | interface IProviderRegistryBuilder | YES | HIGH |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 87 | public void AddProvider(Func<ILlmClient> factory); | YES | HIGH |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 94 | public void AddProvider(ProviderId providerId, Func<ILlmClient> factory); | YES | HIGH |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 101 | public void AddProvider(string providerId, Func<ILlmClient> factory); | YES | HIGH |
| Harbor.Abstractions/Providers/IProviderRegistry.cs | 107 | public void AddProvider(IProviderFactory factory); | YES | HIGH |
| Harbor.Abstractions/Results/ResultErrors.cs | 8 | class ResultErrors | YES | MED |
| Harbor.Abstractions/Results/ResultErrors.cs | 11 | public static string Message(Exception ex) => | YES | MED |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 18 | interface ICompactionService | YES | HIGH |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 27 | public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model); | YES | MED |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 37 | public Task<Result<CompactionResult>> CompactAsync( | YES | MED |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 52 | record CompactionResult | YES | LOW |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 69 | interface ITokenEstimator | YES | HIGH |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 76 | public int Estimate(string text); | YES | MED |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 83 | public int EstimateMessage(AgentMessage message); | YES | MED |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 90 | public int EstimateMessages(IEnumerable<AgentMessage> messages); | YES | MED |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 107 | class HeuristicTokenEstimator | YES | MED |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 110 | public int Estimate(string text) | YES | MED |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 128 | public int EstimateMessage(AgentMessage message) | YES | MED |
| Harbor.Abstractions/Sessions/ICompactionService.cs | 162 | public int EstimateMessages(IEnumerable<AgentMessage> messages) | YES | MED |
| Harbor.Abstractions/Sessions/ISessionPorter.cs | 22 | interface ISessionPorter | YES | HIGH |
| Harbor.Abstractions/Sessions/ISessionPorter.cs | 33 | public Task<Result> ExportAsync( | YES | MED |
| Harbor.Abstractions/Sessions/ISessionPorter.cs | 44 | public Task<Result<string>> ImportAsync( | YES | MED |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 21 | interface ISessionStore | YES | HIGH |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 32 | public Task<Result<Session>> CreateAsync(string directory, string agentName, string providerId, string modelId, Cancella... | YES | HIGH |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 40 | public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 48 | public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default); | YES | MED |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 57 | public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default); | YES | MED |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 66 | public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default); | YES | MED |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 74 | public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 82 | public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 90 | public Task<Result> UpdateAsync(Session session, CancellationToken ct = default); | YES | MED |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 98 | public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 107 | public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default); | YES | MED |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 124 | interface ISessionContext | YES | HIGH |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 129 | Session Session | YES | LOW |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 134 | IReadOnlyList<AgentMessage> Messages | YES | LOW |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 139 | Channel<AgentMessage> SteeringQueue | YES | LOW |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 146 | public Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default); | YES | MED |
| Harbor.Abstractions/Sessions/ISessionStore.cs | 153 | public Task UpdateStatsAsync(Usage usage, CancellationToken ct = default); | YES | MED |
| Harbor.Abstractions/Sessions/ISystemPromptBuilder.cs | 20 | interface ISystemPromptBuilder | YES | HIGH |
| Harbor.Abstractions/Sessions/ISystemPromptBuilder.cs | 28 | public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Sessions/ISystemPromptBuilder.cs | 41 | record SystemPromptContext | YES | LOW |
| Harbor.Abstractions/Sessions/ISystemPromptBuilder.cs | 55 | record ContextFile | YES | LOW |
| Harbor.Abstractions/Sessions/ISystemPromptBuilder.cs | 63 | record SkillDescriptor | YES | LOW |
| Harbor.Abstractions/Sessions/ITokenTracker.cs | 5 | interface ITokenTracker | **NO** | HIGH |
| Harbor.Abstractions/Sessions/ITokenTracker.cs | 31 | record TokenStats | **NO** | LOW |
| Harbor.Abstractions/Tools/IMcpRegistry.cs | 17 | interface IMcpRegistry | YES | HIGH |
| Harbor.Abstractions/Tools/IMcpRegistry.cs | 25 | public Result Register(string name, string stdioCommand); | YES | HIGH |
| Harbor.Abstractions/Tools/IMcpRegistry.cs | 32 | public Result Unregister(string name); | YES | MED |
| Harbor.Abstractions/Tools/IMcpRegistry.cs | 38 | public IReadOnlyList<string> GetServerNames(); | YES | HIGH |
| Harbor.Abstractions/Tools/IMcpRegistry.cs | 52 | public IReadOnlyList<McpServerInstructions> GetInstructions(); | YES | HIGH |
| Harbor.Abstractions/Tools/IMcpRegistry.cs | 65 | public Task<Result<string>> InvokeAsync( | YES | HIGH |
| Harbor.Abstractions/Tools/ITool.cs | 20 | interface ITool | YES | HIGH |
| Harbor.Abstractions/Tools/ITool.cs | 25 | ToolName Name | YES | LOW |
| Harbor.Abstractions/Tools/ITool.cs | 30 | string DisplayName | YES | LOW |
| Harbor.Abstractions/Tools/ITool.cs | 35 | string Description | YES | LOW |
| Harbor.Abstractions/Tools/ITool.cs | 40 | JsonDocument ParameterSchema | YES | LOW |
| Harbor.Abstractions/Tools/ITool.cs | 45 | ExecutionMode ExecutionMode | YES | LOW |
| Harbor.Abstractions/Tools/ITool.cs | 50 | string? PromptSnippet | YES | LOW |
| Harbor.Abstractions/Tools/ITool.cs | 55 | IReadOnlyList<string> PromptGuidelines | YES | LOW |
| Harbor.Abstractions/Tools/ITool.cs | 64 | public Task<ToolResult> ExecuteAsync( | YES | HIGH |
| Harbor.Abstractions/Tools/ITool.cs | 74 | public Result ValidateArguments(JsonElement args) => Result.Success(); | YES | HIGH |
| Harbor.Abstractions/Tools/ITool.cs | 80 | enum ExecutionMode | YES | LOW |
| Harbor.Abstractions/Tools/ITool.cs | 105 | record ToolContext | YES | LOW |
| Harbor.Abstractions/Tools/ITool.cs | 122 | record ToolProgressUpdate | YES | LOW |
| Harbor.Abstractions/Tools/IToolFactory.cs | 5 | interface IToolFactory | **NO** | HIGH |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 21 | interface IToolRegistry | YES | HIGH |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 27 | public IReadOnlyList<ToolDescriptor> GetAllTools(); | YES | HIGH |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 35 | public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null); | YES | HIGH |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 42 | public Result<ITool> GetTool(ToolName name); | YES | HIGH |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 49 | public Result Register(ITool tool); | YES | HIGH |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 56 | public Result Unregister(ToolName name); | YES | MED |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 66 | interface IToolRegistryBuilder | YES | HIGH |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 72 | public void AddTool(ITool tool); | YES | HIGH |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 84 | public void AddTool(Func<ITool> factory); | YES | HIGH |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 90 | public void AddTool(IToolFactory factory); | YES | HIGH |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 102 | public void AddTool(Func<ILoggerFactory, ITool> factory); | YES | HIGH |
| Harbor.Abstractions/Tools/IToolRegistry.cs | 115 | record ToolDescriptor | YES | LOW |
| Harbor.Abstractions/Tools/IToolSource.cs | 6 | interface IToolSource | **NO** | HIGH |
| Harbor.Abstractions/Tools/JsonArgs.cs | 10 | class JsonArgs | YES | MED |
| Harbor.Abstractions/Tools/JsonArgs.cs | 13 | public static string? GetString(JsonElement args, string name) => | YES | HIGH |
| Harbor.Abstractions/Tools/JsonArgs.cs | 19 | public static int? GetInt(JsonElement args, string name) => | YES | HIGH |
| Harbor.Abstractions/Tools/JsonArgs.cs | 26 | public static bool GetBool(JsonElement args, string name) => | YES | HIGH |
| Harbor.Abstractions/Tools/JsonArgs.cs | 31 | public static bool? GetBoolOrNull(JsonElement args, string name) => | YES | HIGH |
| Harbor.Abstractions/Tools/JsonArgs.cs | 37 | public static Result<string> RequireString(JsonElement args, string name) | YES | MED |
| Harbor.Abstractions/Tools/JsonArgs.cs | 45 | public static Result<int> RequireInt(JsonElement args, string name) | YES | MED |
| Harbor.Abstractions/Tools/McpServerInstructions.cs | 9 | record McpServerInstructions | YES | LOW |
| Harbor.Abstractions/Tools/ToolErrors.cs | 15 | class ToolErrors | YES | HIGH |
| Harbor.Abstractions/Tools/ToolErrors.cs | 23 | public static Func<Exception, string> Handler( | YES | HIGH |
| Harbor.Abstractions/Tools/ToolErrors.cs | 35 | public static void KillQuietly(System.Diagnostics.Process process, ILogger? logger = null) | YES | MED |
| Harbor.Abstractions/Tools/ToolPaths.cs | 11 | class ToolPaths | YES | HIGH |
| Harbor.Abstractions/Tools/ToolPaths.cs | 18 | public static Result<string> Resolve(string rawPath) => | YES | HIGH |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 20 | interface IInputHandler | YES | HIGH |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 27 | public Task<Result<KeyPress>> ReadKeyAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 32 | event EventHandler<KeyPressEventArgs>? KeyPressed | YES | MED |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 41 | record KeyPress | YES | LOW |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 46 | class KeyPressEventArgs | YES | MED |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 60 | KeyPress Key | YES | LOW |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 72 | interface ISlashCommand | YES | HIGH |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 77 | string Name | YES | LOW |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 82 | string Description | YES | LOW |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 87 | string Usage | YES | LOW |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 92 | IReadOnlyList<string> Aliases | YES | LOW |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 101 | public Task<Result> ExecuteAsync(IReadOnlyList<string> args, ICommandContext context, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 107 | interface ICommandContext | YES | HIGH |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 112 | ISessionContext Session | YES | LOW |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 117 | IAgent Agent | YES | LOW |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 122 | IProviderRegistry Providers | YES | HIGH |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 127 | IToolRegistry Tools | YES | LOW |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 132 | Action<string> Output | YES | LOW |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 146 | interface ISlashCommandRouter | YES | HIGH |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 153 | public Result Register(ISlashCommand command); | YES | HIGH |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 160 | public Result Unregister(string name); | YES | MED |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 170 | public Task<Result<bool>> TryHandleAsync(string input, ICommandContext context, CancellationToken ct = default); | YES | HIGH |
| Harbor.Abstractions/Tui/ITuiRenderer.cs | 176 | public IReadOnlyList<ISlashCommand> GetRegisteredCommands(); | YES | HIGH |

## Project: Harbor.Abstractions.Contracts

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 25 | record AgentEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 27 | DateTimeOffset Timestamp | **NO** | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 30 | record AgentStartEvent | **NO** | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 38 | record TurnStartEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 43 | record MessageStartEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 48 | record MessageUpdateEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 53 | record MessageEndEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 58 | record ToolExecutionStartEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 66 | record ToolExecutionUpdateEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 73 | record ToolExecutionEndEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 81 | record TurnEndEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 90 | record AgentEndEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 97 | record AgentErrorEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 102 | record CompactionStartedEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 112 | record CompactionCompletedEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 124 | record CompactionFailedEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 129 | record SessionStatsEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 136 | record SessionChangedEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 155 | record LlmEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 157 | record TextStartEvent | **NO** | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 162 | record TextDeltaEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 167 | record TextEndEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 172 | record ThinkingStartEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 177 | record ThinkingDeltaEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 182 | record ThinkingEndEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 187 | record ToolCallStartEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 192 | record ToolCallDeltaEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 197 | record ToolCallEndEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 202 | record StepStartEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 207 | record StepFinishEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 216 | record FinishEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/AgentEvent.cs | 223 | record ErrorEvent | YES | LOW |
| Harbor.Abstractions.Contracts/Events/LlmStreamErrorException.cs | 21 | class LlmStreamErrorException | YES | MED |
| Harbor.Abstractions.Contracts/Events/LlmStreamErrorException.cs | 24 | ProviderErrorKind Kind | YES | LOW |
| Harbor.Abstractions.Contracts/Events/LlmStreamErrorException.cs | 27 | int? StatusCode | YES | LOW |
| Harbor.Abstractions.Contracts/Events/ProviderErrors.cs | 11 | enum ProviderErrorKind | YES | LOW |
| Harbor.Abstractions.Contracts/Events/ProviderErrors.cs | 43 | class ProviderErrors | YES | HIGH |
| Harbor.Abstractions.Contracts/Events/ProviderErrors.cs | 46 | public static bool IsTransient(ProviderErrorKind kind) => | YES | MED |
| Harbor.Abstractions.Contracts/Events/ProviderErrors.cs | 51 | public static ProviderErrorKind FromStatus(System.Net.HttpStatusCode status) | YES | MED |
| Harbor.Abstractions.Contracts/Events/ProviderErrors.cs | 68 | public static ProviderErrorKind FromException(Exception ex, CancellationToken cancellationToken = default) | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 8 | class IdentifierValidation | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 13 | public static bool IsValidProviderId(string value) | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 28 | public static bool IsValidToolName(string value) | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 53 | class SessionId | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 63 | string Value | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 70 | public static SessionId Create(string value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 82 | public static SessionId New() => Create(Guid.NewGuid().ToString("N")); | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 89 | public static Result<SessionId> TryCreate(string? value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 98 | public override string ToString() => Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 110 | public static implicit operator string(SessionId id) => id.Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 119 | class MessageId | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 129 | string Value | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 136 | public static MessageId Create(string value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 148 | public static MessageId New() => Create(Guid.NewGuid().ToString("N")); | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 155 | public static Result<MessageId> TryCreate(string? value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 164 | public override string ToString() => Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 176 | public static implicit operator string(MessageId id) => id.Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 186 | class ToolCallId | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 196 | string Value | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 203 | public static ToolCallId Create(string value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 215 | public static ToolCallId New() => Create(Guid.NewGuid().ToString("N")); | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 218 | public override string ToString() => Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 230 | public static implicit operator string(ToolCallId id) => id.Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 239 | class ProviderId | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 249 | string Value | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 257 | public static ProviderId Create(string value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 274 | public static Result<ProviderId> TryCreate(string? value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 290 | public override string ToString() => Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 302 | public static implicit operator string(ProviderId id) => id.Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 312 | class ModelRef | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 323 | ProviderId ProviderId | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 328 | string ModelId | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 336 | public static ModelRef Create(ProviderId providerId, string modelId) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 349 | public static Result<ModelRef> TryParse(string? value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 366 | public override string ToString() => $"{ProviderId}/{ModelId}"; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 379 | public static implicit operator string(ModelRef id) => id.ToString(); | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 388 | class ToolName | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 398 | string Value | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 406 | public static ToolName Create(string value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 423 | public static Result<ToolName> TryCreate(string? value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 439 | public override string ToString() => Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 451 | public static implicit operator string(ToolName name) => name.Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 460 | class AgentName | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 470 | string Value | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 477 | public static AgentName Create(string value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 490 | public static Result<AgentName> TryCreate(string? value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 499 | public override string ToString() => Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs | 511 | public static implicit operator string(AgentName name) => name.Value; | YES | MED |
| Harbor.Abstractions.Contracts/Models/MemoryPackFormatters.cs | 15 | class JsonElementMemoryPackFormatter | YES | MED |
| Harbor.Abstractions.Contracts/Models/MemoryPackFormatters.cs | 36 | public override void Deserialize(ref MemoryPackReader reader, scoped ref JsonElement value) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/MemoryPackFormatters.cs | 54 | public static void EnsureRegistered() | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 27 | record AgentMessage | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 37 | string Role | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 51 | record UserMessage | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 78 | record AssistantMessage | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 100 | public static AssistantMessage Empty(string sessionId, string model) => new( | YES | MED |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 114 | public AssistantMessage AppendText(string text) | YES | MED |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 133 | public AssistantMessage AppendThinking(string text) | YES | MED |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 149 | public AssistantMessage AppendToolCall(ToolCallPart toolCall) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 166 | public AssistantMessage WithFinish(StopReason reason, Usage usage) => | YES | MED |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 179 | record ToolResultMessage | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 203 | record ContentPart | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 208 | string Type | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 216 | record TextPart | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 227 | record ThinkingPart | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 240 | record ToolCallPart | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 262 | record FilePart | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 277 | record ToolResult | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 303 | string Output | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 306 | bool IsError | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 309 | IReadOnlyList<FileAttachment>? Attachments | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 316 | object? Metadata | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 324 | public static ToolResult Success(string output, object? metadata = null) => | YES | MED |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 333 | public static ToolResult Error(string output, object? metadata = null) => | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 341 | record ToolResultEntry | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 369 | string ToolCallId | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 372 | string ToolName | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 375 | string Output | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 378 | bool IsError | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 385 | object? Metadata | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 395 | public static ToolResultEntry From(string toolCallId, string toolName, ToolResult result) => | YES | MED |
| Harbor.Abstractions.Contracts/Models/Messages.cs | 406 | record FileAttachment | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 7 | enum SessionStatus | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 39 | record Session | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 65 | public static Session Create(string directory, string agentName, string providerId, string modelId, string? title = null... | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Session.cs | 96 | record SessionMetadata | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 116 | public SessionMetadata AddUsage(Usage usage) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Session.cs | 139 | record Usage | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 154 | record Pricing | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 170 | public decimal CalculateCost(Usage usage) | YES | MED |
| Harbor.Abstractions.Contracts/Models/Session.cs | 194 | record ModelInfo | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 210 | enum StopReason | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 231 | class StopReasonJsonConverter | **NO** | MED |
| Harbor.Abstractions.Contracts/Models/Session.cs | 238 | public static StopReason Parse(string? finishReason) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Session.cs | 273 | public override StopReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Session.cs | 284 | enum ReasoningEffort | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 299 | class ReasoningEffortJsonConverter | **NO** | MED |
| Harbor.Abstractions.Contracts/Models/Session.cs | 306 | record ToolChoice | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 309 | record Auto | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 312 | record None | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 315 | record Required | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 319 | record Specific | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 325 | enum CacheStrategy | YES | LOW |
| Harbor.Abstractions.Contracts/Models/Session.cs | 338 | class JsonStringConverter | YES | MED |
| Harbor.Abstractions.Contracts/Models/Session.cs | 341 | public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) | YES | HIGH |
| Harbor.Abstractions.Contracts/Models/Session.cs | 348 | public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(val... | YES | HIGH |
| Harbor.Abstractions.Contracts/Permissions/BashArgMatcher.cs | 29 | class BashArgMatcher | YES | MED |
| Harbor.Abstractions.Contracts/Permissions/BashArgMatcher.cs | 40 | public static bool HasShellMetacharacters(string command) | YES | MED |
| Harbor.Abstractions.Contracts/Permissions/BashArgMatcher.cs | 115 | public static bool IsDestructiveCommand(string command) | YES | MED |
| Harbor.Abstractions.Contracts/Permissions/BashArgMatcher.cs | 192 | public static IReadOnlyList<string> GetDenyMatchTargets(string command) | YES | HIGH |
| Harbor.Abstractions.Contracts/Permissions/BashArgMatcher.cs | 233 | public static bool IsAllowedByPrefixRule(string pattern, string command) | YES | MED |
| Harbor.Abstractions.Contracts/Permissions/BashArgMatcher.cs | 277 | public static IReadOnlyList<string> SplitArgv(string command) | YES | MED |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 32 | record PermissionRuleset | YES | LOW |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 132 | public PermissionRuleset Merge(PermissionRuleset other) | YES | MED |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 185 | public PermissionAction Evaluate(string permission, string argPath) | YES | MED |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 317 | record PermissionRule | YES | LOW |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 336 | public bool MatchesPermission(string permission) => | YES | MED |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 345 | public bool MatchesPattern(string argPath) | YES | MED |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 370 | enum PermissionAction | YES | LOW |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 389 | record PermissionRequest | YES | LOW |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 400 | record PermissionResponse | YES | LOW |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 418 | interface IPermissionService | YES | HIGH |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 428 | public Task<Result<PermissionResponse>> CheckAsync( | YES | HIGH |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 441 | public Task<Result<PermissionResponse>> AskUserAsync( | YES | MED |
| Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs | 451 | public PermissionRuleset GetRuleset(string agentName); | YES | HIGH |
| Harbor.Abstractions.Contracts/Permissions/ToolCategory.cs | 16 | enum ToolCategory | YES | LOW |
| Harbor.Abstractions.Contracts/Permissions/ToolCategory.cs | 39 | class ToolCategories | YES | HIGH |
| Harbor.Abstractions.Contracts/Permissions/ToolCategory.cs | 69 | public static bool TryClassify(string toolName, out ToolCategory category) | YES | MED |
| Harbor.Abstractions.Contracts/Permissions/ToolCategory.cs | 76 | public static bool CategoryMatches(string rulePermission, string toolName) | YES | MED |

## Project: Harbor.Application

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Application/Agents/AgentLoop.cs | 25 | class AgentLoop | YES | HIGH |
| Harbor.Application/Agents/AgentLoop.cs | 102 | public async Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default) | YES | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 9 | class DefaultAgent | YES | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 127 | AgentState State | YES | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 143 | public void ResetAbortSource() | YES | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 174 | public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) | YES | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 204 | public async Task<Result> PromptAsync(string text, CancellationToken ct = default) | YES | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 243 | public async Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) | YES | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 360 | public void Steer(AgentMessage message) => _steeringQueue.Writer.TryWrite(message); | YES | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 377 | public Task WaitForIdleAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 397 | public void Initialize(Session session, AgentDefinition agent) | YES | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 426 | public void Dispose() | YES | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 469 | public void Dispose() | **NO** | HIGH |
| Harbor.Application/Agents/DefaultAgent.cs | 480 | class DefaultSessionContext | YES | MED |
| Harbor.Application/Agents/DefaultAgent.cs | 506 | Session Session | **NO** | LOW |
| Harbor.Application/Agents/DefaultAgent.cs | 515 | public async Task AppendMessageAsync(AgentMessage message, CancellationToken ct = default) | **NO** | MED |
| Harbor.Application/Agents/DefaultAgent.cs | 530 | public async Task UpdateStatsAsync(Usage usage, CancellationToken ct = default) | **NO** | MED |
| Harbor.Application/Agents/IToolDispatcher.cs | 10 | interface IToolDispatcher | YES | HIGH |
| Harbor.Application/Agents/StreamingCoalescer.cs | 13 | record MalformedToolCall | YES | LOW |
| Harbor.Application/Agents/StreamingCoalescer.cs | 42 | class StreamingCoalescer | YES | MED |
| Harbor.Application/Agents/StreamingCoalescer.cs | 64 | public void Dispose() | YES | HIGH |
| Harbor.Application/Agents/StreamingCoalescer.cs | 74 | public void AppendTextDelta(string delta) | YES | MED |
| Harbor.Application/Agents/StreamingCoalescer.cs | 81 | public void AppendThinkingDelta(string delta) | YES | MED |
| Harbor.Application/Agents/StreamingCoalescer.cs | 88 | public void StartToolCall(string id, string toolName) => _pendingToolCalls[id] = (toolName, StringBuilderPool.Rent()); | YES | HIGH |
| Harbor.Application/Agents/StreamingCoalescer.cs | 91 | public void AppendToolCallDelta(string id, string argsDelta) | YES | HIGH |
| Harbor.Application/Agents/StreamingCoalescer.cs | 104 | public string FlushText() | YES | HIGH |
| Harbor.Application/Agents/StreamingCoalescer.cs | 117 | public string FlushThinking() | YES | HIGH |
| Harbor.Application/Agents/StreamingCoalescer.cs | 172 | public List<ToolCallPart> MaterializeToolCalls(List<MalformedToolCall>? malformedSink = null) | YES | HIGH |
| Harbor.Application/Agents/StreamingCoalescer.cs | 259 | public void DiscardPendingToolCalls() | YES | HIGH |
| Harbor.Application/Agents/StreamingCoalescer.cs | 269 | public void Reset() | YES | HIGH |
| Harbor.Application/Agents/SubAgentRunner.cs | 32 | class SubAgentRunner | YES | HIGH |
| Harbor.Application/Agents/SubAgentRunner.cs | 53 | public async Task<Result<SubAgentRunResult>> RunAsync( | YES | HIGH |
| Harbor.Application/Agents/SubAgentRunner.cs | 220 | class DeferredSubAgentRunner | YES | HIGH |
| Harbor.Application/Agents/SubAgentRunner.cs | 225 | public void Attach(ISubAgentRunner inner) | YES | MED |
| Harbor.Application/Agents/SubAgentRunner.cs | 232 | public Task<Result<SubAgentRunResult>> RunAsync( | YES | HIGH |
| Harbor.Application/Agents/ToolDispatcher.cs | 47 | class ToolDispatcher | YES | HIGH |
| Harbor.Application/Agents/ToolDispatcher.cs | 64 | public async Task<ToolResultMessage> ExecuteAsync( | YES | HIGH |
| Harbor.Application/Configuration/AuthStore.cs | 13 | class AuthStore | YES | HIGH |
| Harbor.Application/Configuration/AuthStore.cs | 41 | public async Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default) => | YES | HIGH |
| Harbor.Application/Configuration/AuthStore.cs | 95 | public async Task<Result> SetApiKeyAsync(string providerId, string apiKey, CancellationToken ct = default) | YES | HIGH |
| Harbor.Application/Configuration/AuthStore.cs | 108 | public async Task<Result> RemoveApiKeyAsync(string providerId, CancellationToken ct = default) | YES | HIGH |
| Harbor.Application/Configuration/AuthStore.cs | 130 | public Task<Result<IReadOnlyDictionary<string, bool>>> ListApiKeysAsync(CancellationToken ct = default) | YES | MED |
| Harbor.Application/Configuration/ConfigJsonContext.cs | 19 | class ConfigJsonContext | YES | MED |
| Harbor.Application/Configuration/ConfigSections.cs | 7 | record IdentityConfig | YES | LOW |
| Harbor.Application/Configuration/ConfigSections.cs | 29 | public Result<ModelRef> EffectiveModel() | YES | MED |
| Harbor.Application/Configuration/ConfigSections.cs | 42 | record ToolingConfig | YES | LOW |
| Harbor.Application/Configuration/ConfigSections.cs | 50 | public Result<ToolingConfig> Validate() | **NO** | HIGH |
| Harbor.Application/Configuration/ConfigSections.cs | 61 | record CostConfig | YES | LOW |
| Harbor.Application/Configuration/ConfigSections.cs | 65 | public Result<CostConfig> Validate() | **NO** | HIGH |
| Harbor.Application/Configuration/ConfigSections.cs | 75 | record CompactionConfig | YES | LOW |
| Harbor.Application/Configuration/ConfigSections.cs | 82 | public Result<CompactionConfig> Validate() | **NO** | HIGH |
| Harbor.Application/Configuration/ConfigSections.cs | 110 | record ConsoleExUiConfig | YES | LOW |
| Harbor.Application/Configuration/ConfigSections.cs | 120 | record PresentationConfig | YES | LOW |
| Harbor.Application/Configuration/ConfigSections.cs | 127 | ConsoleExUiConfig ConsoleEx | YES | LOW |
| Harbor.Application/Configuration/ConfigSections.cs | 131 | public Result<PresentationConfig> Validate() | **NO** | HIGH |
| Harbor.Application/Configuration/ConfigSections.cs | 144 | record RunLimitsConfig | YES | LOW |
| Harbor.Application/Configuration/ConfigSections.cs | 150 | public Result<RunLimitsConfig> Validate() | **NO** | HIGH |
| Harbor.Application/Configuration/ConfigSections.cs | 161 | record ProviderConfigEntry | YES | LOW |
| Harbor.Application/Configuration/ConfigSections.cs | 168 | public Result<ProviderConfigEntry> Validate() | **NO** | HIGH |
| Harbor.Application/Configuration/ConfigStore.cs | 22 | interface IConfigStore | YES | HIGH |
| Harbor.Application/Configuration/ConfigStore.cs | 27 | public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Application/Configuration/ConfigStore.cs | 33 | public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default); | YES | HIGH |
| Harbor.Application/Configuration/ConfigStore.cs | 43 | public Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default); | YES | MED |
| Harbor.Application/Configuration/ConfigStore.cs | 52 | public Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default); | YES | HIGH |
| Harbor.Application/Configuration/ConfigStore.cs | 59 | class JsonConfigStore | YES | HIGH |
| Harbor.Application/Configuration/ConfigStore.cs | 75 | public Task<Result<HarborConfig>> LoadAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Application/Configuration/ConfigStore.cs | 121 | public Task<Result> SaveAsync(HarborConfig config, CancellationToken ct = default) | YES | HIGH |
| Harbor.Application/Configuration/ConfigStore.cs | 147 | public async Task<Result> UpdateAsync(Func<HarborConfig, HarborConfig> updater, CancellationToken ct = default) | YES | MED |
| Harbor.Application/Configuration/ConfigStore.cs | 158 | public async Task<Result<string>> GetApiKeyAsync(string providerId, CancellationToken ct = default) | YES | HIGH |
| Harbor.Application/Configuration/ConfigStore.cs | 170 | public static string GetDefaultPath() | YES | HIGH |
| Harbor.Application/Configuration/HarborConfig.cs | 22 | class HarborConfig | YES | MED |
| Harbor.Application/Configuration/HarborConfig.cs | 25 | IdentityConfig Identity | YES | LOW |
| Harbor.Application/Configuration/HarborConfig.cs | 28 | PresentationConfig Ui | YES | LOW |
| Harbor.Application/Configuration/HarborConfig.cs | 31 | ToolingConfig Tooling | YES | LOW |
| Harbor.Application/Configuration/HarborConfig.cs | 34 | CostConfig Cost | YES | LOW |
| Harbor.Application/Configuration/HarborConfig.cs | 37 | CompactionConfig Compaction | YES | LOW |
| Harbor.Application/Configuration/HarborConfig.cs | 44 | string? SecondaryModel | YES | LOW |
| Harbor.Application/Configuration/HarborConfig.cs | 47 | RunLimitsConfig Run | YES | LOW |
| Harbor.Application/Configuration/HarborConfig.cs | 178 | public RawConfigDto ToRaw() => new() | YES | MED |
| Harbor.Application/Configuration/HarborConfig.cs | 216 | public Result<HarborConfig> Validate() | YES | HIGH |
| Harbor.Application/Configuration/HarborConfig.cs | 244 | class RawConfigDto | YES | MED |
| Harbor.Application/Configuration/HarborConfig.cs | 259 | ConsoleExUiConfig? ConsoleEx | YES | LOW |
| Harbor.Application/Configuration/HarborConfig.cs | 282 | class UiRawDto | YES | MED |
| Harbor.Application/Configuration/HarborConfig.cs | 293 | class ConfigNormalizer | YES | MED |
| Harbor.Application/Configuration/HarborConfig.cs | 306 | public static Result<HarborConfig> Normalize(RawConfigDto raw) | YES | MED |
| Harbor.Application/Configuration/ProviderPresets.cs | 11 | class ProviderPresets | YES | HIGH |
| Harbor.Application/Configuration/ProviderPresets.cs | 34 | public static Preset? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); | YES | HIGH |
| Harbor.Application/Configuration/ProviderPresets.cs | 38 | public static IReadOnlyList<Preset> GetNoAuth() => All.Where(p => !p.RequiresApiKey).ToList(); | YES | HIGH |
| Harbor.Application/Configuration/ProviderPresets.cs | 48 | record Preset | YES | LOW |
| Harbor.Application/Onboarding/OnboardingWizard.cs | 15 | class OnboardingWizard | YES | MED |
| Harbor.Application/Onboarding/OnboardingWizard.cs | 65 | public async Task<Result> RunAsync(Func<string, Task<string>> reader, Action<string> writer, CancellationToken ct = defa... | YES | HIGH |
| Harbor.Application/Permissions/PermissionService.cs | 8 | class PermissionService | YES | HIGH |
| Harbor.Application/Permissions/PermissionService.cs | 48 | public Task<Result<PermissionResponse>> CheckAsync( | YES | HIGH |
| Harbor.Application/Permissions/PermissionService.cs | 131 | public async Task<Result<PermissionResponse>> AskUserAsync( | YES | MED |
| Harbor.Application/Permissions/PermissionService.cs | 151 | public PermissionRuleset GetRuleset(string agentName) | YES | HIGH |
| Harbor.Application/Providers/ProviderHealthCheck.cs | 13 | class ProviderHealthCheck | YES | HIGH |
| Harbor.Application/Providers/ProviderHealthCheck.cs | 34 | public async Task<Result<ProviderHealth>> CheckAsync(ProviderId providerId, CancellationToken cancellationToken = defaul... | YES | HIGH |
| Harbor.Application/Providers/ProviderHealthCheck.cs | 76 | public static string Classify(string rawError) | YES | MED |
| Harbor.Application/Resilience/IRetryPolicy.cs | 6 | interface IRetryPolicy | **NO** | HIGH |
| Harbor.Application/Resilience/RetryOptions.cs | 3 | record RetryOptions | **NO** | LOW |
| Harbor.Application/Resilience/RetryPolicy.cs | 33 | class RetryPolicy | YES | MED |
| Harbor.Application/Resilience/RetryPolicy.cs | 86 | public static bool IsTransient(Exception ex, out TimeSpan? retryAfter) | YES | MED |
| Harbor.Application/Resilience/RetryPolicy.cs | 119 | public static bool IsTransient(ProviderErrorKind kind) => ProviderErrors.IsTransient(kind); | YES | MED |
| Harbor.Application/Resilience/RetryPolicyExtensions.cs | 17 | class RetryPolicyExtensions | YES | MED |
| Harbor.Application/Resources/CoreResources.cs | 5 | class CoreResources | **NO** | HIGH |
| Harbor.Application/Resources/CoreResources.cs | 10 | public static string GetLog(string name) => Log.GetString(name) ?? name; | **NO** | HIGH |
| Harbor.Application/Resources/CoreResources.cs | 11 | public static string GetError(string name) => Error.GetString(name) ?? name; | **NO** | HIGH |
| Harbor.Application/Sessions/CachingSystemPromptBuilder.cs | 31 | class CachingSystemPromptBuilder | YES | MED |
| Harbor.Application/Sessions/CachingSystemPromptBuilder.cs | 47 | public async Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default) | YES | HIGH |
| Harbor.Application/Sessions/CompactionService.cs | 24 | class CompactionService | YES | HIGH |
| Harbor.Application/Sessions/CompactionService.cs | 166 | int ReserveTokens | YES | LOW |
| Harbor.Application/Sessions/CompactionService.cs | 171 | int KeepRecentTokens | YES | LOW |
| Harbor.Application/Sessions/CompactionService.cs | 176 | int TailTurns | YES | LOW |
| Harbor.Application/Sessions/CompactionService.cs | 209 | public static IReadOnlyList<AgentMessage> TruncateToFit( | YES | MED |
| Harbor.Application/Sessions/CompactionService.cs | 300 | public static IReadOnlyList<AgentMessage> TruncateToFitStrict( | YES | MED |
| Harbor.Application/Sessions/CompactionService.cs | 382 | public static IReadOnlyList<AgentMessage> MaterializeCompactedView(IReadOnlyList<AgentMessage> messages) | YES | MED |
| Harbor.Application/Sessions/CompactionService.cs | 439 | public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) | YES | MED |
| Harbor.Application/Sessions/CompactionService.cs | 446 | public async Task<Result<CompactionResult>> CompactAsync( | YES | MED |
| Harbor.Application/Sessions/MessageConverter.cs | 6 | class MessageConverter | YES | MED |
| Harbor.Application/Sessions/MessageConverter.cs | 14 | public IReadOnlyList<LlmMessage> ToLlmMessages(IReadOnlyList<AgentMessage> messages) | YES | MED |
| Harbor.Application/Sessions/SystemPromptBuilder.cs | 12 | class SystemPromptBuilder | YES | MED |
| Harbor.Application/Sessions/SystemPromptBuilder.cs | 45 | public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default) | YES | HIGH |
| Harbor.Application/Sessions/TokenTracker.cs | 6 | class TokenTracker | **NO** | MED |
| Harbor.Application/Sessions/TokenTracker.cs | 24 | int ReserveTokens | **NO** | LOW |
| Harbor.Application/Sessions/TokenTracker.cs | 33 | public void RecordTurnUsage(Usage usage) | **NO** | MED |
| Harbor.Application/Sessions/TokenTracker.cs | 42 | public int Estimate(string text) => _estimator.Estimate(text); | **NO** | MED |
| Harbor.Application/Sessions/TokenTracker.cs | 44 | public int EstimateMessage(AgentMessage message) => _estimator.EstimateMessage(message); | **NO** | MED |
| Harbor.Application/Sessions/TokenTracker.cs | 46 | public int EstimateTokens(IReadOnlyList<AgentMessage> messages) => _estimator.EstimateMessages(messages); | **NO** | MED |
| Harbor.Application/Sessions/TokenTracker.cs | 49 | public void RecordAppendedMessage(AgentMessage message) | YES | MED |
| Harbor.Application/Sessions/TokenTracker.cs | 56 | public bool ShouldCompact(IReadOnlyList<AgentMessage> messages, ModelInfo model) | YES | MED |
| Harbor.Application/Sessions/TokenTracker.cs | 78 | public TokenStats GetStats() | **NO** | HIGH |
| Harbor.Application/Sessions/WorkspaceContextSource.cs | 34 | class WorkspaceContextSource | YES | MED |
| Harbor.Application/Sessions/WorkspaceContextSource.cs | 45 | public static IReadOnlyList<ContextFile> LoadContextFiles(string workingDirectory) | YES | HIGH |
| Harbor.Application/Sessions/WorkspaceContextSource.cs | 80 | public static string? FormatMcpInstructions(IReadOnlyList<McpServerInstructions>? servers) | YES | HIGH |
| Harbor.Application/Sessions/WorkspaceContextSource.cs | 101 | public static IReadOnlyList<SkillDescriptor> LoadSkills(string workingDirectory) | YES | HIGH |
| Harbor.Application/Sessions/WorkspaceContextSource.cs | 114 | public static IReadOnlyList<SkillDescriptor> LoadSkills(string workingDirectory, string? globalSkillsDir) | YES | HIGH |
| Harbor.Application/Telemetry/GenAiTags.cs | 3 | class GenAiTags | **NO** | MED |
| Harbor.Application/Telemetry/HarborTelemetry.cs | 5 | class HarborTelemetry | **NO** | MED |

## Project: Harbor.CodeGen

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.CodeGen/ResourceKeyGenerator.cs | 11 | class ResourceKeyGenerator | **NO** | MED |
| Harbor.CodeGen/ResourceKeyGenerator.cs | 13 | public void Initialize(IncrementalGeneratorInitializationContext context) | **NO** | HIGH |

## Project: Harbor.Core

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Core/FacadeMarker.cs | 19 | class FacadeMarker | YES | MED |

## Project: Harbor.Desktop.Abstractions

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Desktop.Abstractions/Configuration/AppConfigBase.cs | 47 | record AppConfigBase | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/AppConfigBase.cs | 55 | string AppId | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/AppConfigBase.cs | 62 | string ConfigFileName | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 53 | record CommonConfig | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 68 | public static void OverrideHarborHomeForTests(string harborDirectory) | YES | MED |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 86 | string ConfigVersion | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 96 | bool OnboardingCompleted | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 107 | string Theme | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 127 | string DefaultProvider | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 133 | string DefaultModel | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 139 | string DefaultAgent | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 150 | string StorageBackend | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 157 | string StoragePath | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 167 | string LogLevel | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 173 | bool EnableFileLogging | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 179 | int MaxLogFiles | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 189 | string PermissionMode | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 196 | ImmutableList<string> AlwaysAllowTools | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 202 | ImmutableList<string> AlwaysDenyTools | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 211 | bool EnablePlugins | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 218 | string PluginDirectories | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 226 | bool EnableScripting | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 235 | string HttpProxy | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 240 | int HttpTimeoutSeconds | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 246 | string UserAgent | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 254 | int CompactionReserveTokens | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 260 | int CompactionKeepRecentTokens | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 266 | int CompactionTailTurns | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CommonConfig.cs | 282 | string ConfigDirectory | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/CompositeConfig.cs | 44 | record CompositeConfig | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CompositeConfig.cs | 61 | CommonConfig Common | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CompositeConfig.cs | 68 | TAppConfig App | YES | LOW |
| Harbor.Desktop.Abstractions/Configuration/CompositeConfig.cs | 91 | public string EffectiveStorageBackend(string? envOverride = null) | YES | MED |
| Harbor.Desktop.Abstractions/Configuration/ConfigJsonContext.cs | 53 | class ConfigJsonContext | **NO** | MED |
| Harbor.Desktop.Abstractions/Configuration/ConfigJsonContext.cs | 61 | class ConfigJson | YES | MED |
| Harbor.Desktop.Abstractions/Configuration/ConfigJsonContext.cs | 73 | JsonSerializerOptions Options | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/ConfigJsonContext.cs | 88 | JsonTypeInfo<CommonConfig> CommonConfigInfo | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/IAppConfigStore.cs | 35 | interface IAppConfigStore | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/IAppConfigStore.cs | 44 | public Task<Result<T>> LoadAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/IAppConfigStore.cs | 52 | public Task<Result> SaveAsync(T config, CancellationToken ct = default); | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/IAppConfigStore.cs | 62 | public Task<Result> UpdateAsync(Func<T, T> updater, CancellationToken ct = default); | YES | MED |
| Harbor.Desktop.Abstractions/Configuration/ICommonConfigStore.cs | 31 | interface ICommonConfigStore | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/ICommonConfigStore.cs | 43 | public Task<Result<CommonConfig>> LoadAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/ICommonConfigStore.cs | 51 | public Task<Result> SaveAsync(CommonConfig config, CancellationToken ct = default); | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/ICommonConfigStore.cs | 61 | public Task<Result> UpdateAsync(Func<CommonConfig, CommonConfig> updater, CancellationToken ct = default); | YES | MED |
| Harbor.Desktop.Abstractions/Configuration/JsonAppConfigStore.cs | 49 | class JsonAppConfigStore | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/JsonAppConfigStore.cs | 111 | public async Task<Result<T>> LoadAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/JsonAppConfigStore.cs | 152 | public async Task<Result> SaveAsync(T config, CancellationToken ct = default) | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/JsonAppConfigStore.cs | 195 | public async Task<Result> UpdateAsync(Func<T, T> updater, CancellationToken ct = default) | YES | MED |
| Harbor.Desktop.Abstractions/Configuration/JsonAppConfigStore.cs | 217 | class ImmutableListConverter | YES | MED |
| Harbor.Desktop.Abstractions/Configuration/JsonAppConfigStore.cs | 225 | public override ImmutableList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/JsonAppConfigStore.cs | 250 | public override void Write(Utf8JsonWriter writer, ImmutableList<T> value, JsonSerializerOptions options) | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/JsonCommonConfigStore.cs | 50 | class JsonCommonConfigStore | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/JsonCommonConfigStore.cs | 77 | public async Task<Result<CommonConfig>> LoadAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/JsonCommonConfigStore.cs | 164 | public async Task<Result> SaveAsync(CommonConfig config, CancellationToken ct = default) | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/JsonCommonConfigStore.cs | 240 | public async Task<Result> UpdateAsync(Func<CommonConfig, CommonConfig> updater, CancellationToken ct = default) | YES | MED |
| Harbor.Desktop.Abstractions/Configuration/JsonCommonConfigStore.cs | 264 | class ImmutableDictionaryConverter | YES | MED |
| Harbor.Desktop.Abstractions/Configuration/JsonCommonConfigStore.cs | 273 | public override ImmutableDictionary<TKey, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOpti... | YES | HIGH |
| Harbor.Desktop.Abstractions/Configuration/JsonCommonConfigStore.cs | 312 | public override void Write(Utf8JsonWriter writer, ImmutableDictionary<TKey, TValue> value, JsonSerializerOptions options... | YES | HIGH |
| Harbor.Desktop.Abstractions/DesignSystem/ColorPalette.cs | 13 | class ColorPalette | YES | MED |
| Harbor.Desktop.Abstractions/DesignSystem/DesignTokens.cs | 6 | class DesignTokens | YES | MED |
| Harbor.Desktop.Abstractions/DesignSystem/RgbColor.cs | 12 | record struct | YES | LOW |
| Harbor.Desktop.Abstractions/DesignSystem/RgbColor.cs | 15 | public string ToHex() => string.Create(7, this, static (span, color) => | YES | MED |
| Harbor.Desktop.Abstractions/DesignSystem/RgbColor.cs | 29 | public static RgbColor Parse(string hex) | YES | HIGH |
| Harbor.Desktop.Abstractions/DesignSystem/RgbColor.cs | 60 | public static implicit operator RgbColor(string hex) => Parse(hex); | YES | MED |
| Harbor.Desktop.Abstractions/DesignSystem/RgbColor.cs | 63 | public static implicit operator RgbColor((int R, int G, int B) rgb) | YES | MED |
| Harbor.Desktop.Abstractions/DesignSystem/Typography.cs | 8 | class Typography | YES | MED |
| Harbor.Desktop.Abstractions/Messages/CrossVmMessages.cs | 4 | record ModelPickedMessage | YES | LOW |
| Harbor.Desktop.Abstractions/Messages/CrossVmMessages.cs | 7 | record OnboardingCompletedMessage | YES | LOW |
| Harbor.Desktop.Abstractions/Models/CommandPaletteItem.cs | 11 | record CommandPaletteItem | YES | LOW |
| Harbor.Desktop.Abstractions/Models/CommandPaletteItem.cs | 18 | public static CommandPaletteItem Create(string title, Action action) | YES | HIGH |
| Harbor.Desktop.Abstractions/Models/SessionDotState.cs | 3 | enum SessionDotState | **NO** | LOW |
| Harbor.Desktop.Abstractions/Models/ThemeKind.cs | 7 | enum ThemeKind | YES | LOW |
| Harbor.Desktop.Abstractions/Models/ToastKind.cs | 7 | enum ToastKind | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ChatViewModelBase.cs | 31 | class ChatViewModelBase | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ChatViewModelBase.cs | 106 | ObservableCollection<ChatLineViewModel> Lines | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ChatViewModelBase.cs | 113 | ObservableCollection<ToolCallViewModel> ToolCalls | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ChatViewModelBase.cs | 124 | ObservableCollection<object> Timeline | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ChatViewModelBase.cs | 301 | public static string RoleBrushKey(ChatRole role) => role switch | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/CodeEditorViewModelBase.cs | 5 | class CodeEditorViewModelBase | **NO** | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/CodeEditorViewModelBase.cs | 19 | ObservableCollection<EditorTabViewModelBase> Tabs | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/CodeEditorViewModelBase.cs | 40 | class EditorTabViewModelBase | **NO** | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/CodeEditorViewModelBase.cs | 69 | string Extension | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/CommandPaletteViewModelBase.cs | 5 | class CommandPaletteViewModelBase | **NO** | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/CommandPaletteViewModelBase.cs | 28 | ObservableCollection<CommandResultViewModel> Results | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/CommandPaletteViewModelBase.cs | 75 | public void InvokeSelected() | **NO** | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/CommandPaletteViewModelBase.cs | 92 | public void MoveUp() | **NO** | MED |
| Harbor.Desktop.Abstractions/ViewModels/CommandPaletteViewModelBase.cs | 100 | public void MoveDown() | **NO** | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/CommandResultViewModel.cs | 11 | record CommandResultViewModel | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/DiffViewModel.cs | 14 | class DiffViewModel | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/DiffViewModel.cs | 48 | public void ComputeDiff() | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/DiffViewModel.cs | 80 | public async Task CopyAsync( | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/DiffViewModelBase.cs | 16 | class DiffViewModelBase | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/EditorTabViewModel.cs | 4 | class EditorTabViewModel | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/EditorTabViewModel.cs | 22 | string FilePath | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/EditorTabViewModel.cs | 25 | string FileName | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/EditorTabViewModel.cs | 28 | string Extension | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/FocusSessionViewModel.cs | 4 | class FocusSessionViewModel | **NO** | MED |
| Harbor.Desktop.Abstractions/ViewModels/FocusSessionViewModelBase.cs | 17 | class FocusSessionViewModelBase | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/MonacoEditorViewModel.cs | 14 | class MonacoEditorViewModel | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/MonacoEditorViewModel.cs | 25 | class Fibonacci | **NO** | MED |
| Harbor.Desktop.Abstractions/ViewModels/MonacoEditorViewModel.cs | 27 | public static IEnumerable<int> Stream() | **NO** | MED |
| Harbor.Desktop.Abstractions/ViewModels/MonacoEditorViewModel.cs | 58 | public async Task SaveAsync( | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/MonacoEditorViewModel.cs | 75 | public async Task CopyAsync( | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/OnboardingProviderOption.cs | 10 | class OnboardingProviderOption | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/OnboardingProviderOption.cs | 28 | string Id | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/OnboardingProviderOption.cs | 31 | string DisplayName | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/OnboardingProviderOption.cs | 34 | string? AuthEnvVar | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/OnboardingProviderOption.cs | 37 | bool RequiresKey | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/OnboardingProviderOption.cs | 40 | string DefaultModel | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/OnboardingProviderOption.cs | 43 | string Icon | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/OnboardingViewModel.cs | 20 | class OnboardingViewModel | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/OnboardingViewModel.cs | 97 | ObservableCollection<string> AvailableModels | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/OnboardingViewModel.cs | 186 | ObservableCollection<OnboardingProviderOption> Providers | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/OnboardingViewModel.cs | 221 | public void Dispose() | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/PickerModelViewModel.cs | 8 | record PickerModelViewModel | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/PickerModelViewModel.cs | 26 | class ProviderGroupViewModel | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/PickerModelViewModel.cs | 56 | string Id | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/PickerModelViewModel.cs | 59 | string DisplayName | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/PickerModelViewModel.cs | 62 | string AuthStatusIcon | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/PickerModelViewModel.cs | 65 | string AuthStatusText | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/PickerModelViewModel.cs | 72 | string AuthStatusBrushKey | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/PickerModelViewModel.cs | 75 | bool IsAuthenticated | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/PickerModelViewModel.cs | 78 | bool RequiresApiKey | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/PickerModelViewModel.cs | 81 | string? SetupHint | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/PickerModelViewModel.cs | 84 | ObservableCollection<PickerModelViewModel> Models | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ProviderBrowserViewModel.cs | 17 | class ProviderBrowserViewModel | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ProviderBrowserViewModel.cs | 43 | ObservableCollection<ProviderRowViewModel> Providers | **NO** | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ProviderBrowserViewModel.cs | 44 | ObservableCollection<ModelRowViewModel> Models | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ProviderBrowserViewModel.cs | 155 | record ProviderRowViewModel | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ProviderBrowserViewModel.cs | 158 | record ModelRowViewModel | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ProviderBrowserViewModelBase.cs | 10 | class ProviderBrowserViewModelBase | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ProviderBrowserViewModelBase.cs | 33 | ObservableCollection<ProviderListItem> Providers | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ProviderBrowserViewModelBase.cs | 55 | record ProviderListItem | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ProviderConfigViewModel.cs | 29 | class ProviderConfigViewModel | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ProviderConfigViewModel.cs | 88 | string Id | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ProviderConfigViewModel.cs | 91 | string DisplayName | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ProviderConfigViewModel.cs | 94 | bool RequiresApiKey | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ProviderModelPickerViewModel.cs | 35 | class ProviderModelPickerViewModel | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ProviderModelPickerViewModel.cs | 75 | ObservableCollection<ProviderGroupViewModel> AllProviders | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ProviderModelPickerViewModel.cs | 78 | ObservableCollection<ProviderGroupViewModel> FilteredProviders | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ProviderModelPickerViewModelBase.cs | 30 | class ProviderModelPickerViewModelBase | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ProviderModelPickerViewModelBase.cs | 69 | event Action? ModelSelected | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/SessionCardViewModel.cs | 13 | class SessionCardViewModel | **NO** | MED |
| Harbor.Desktop.Abstractions/ViewModels/SessionCardViewModel.cs | 26 | string Id | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SessionCardViewModel.cs | 27 | string Title | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SessionCardViewModel.cs | 28 | string PreviewText | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SessionCardViewModel.cs | 29 | string Duration | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SessionCardViewModel.cs | 30 | DateTimeOffset CreatedAt | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SessionCardViewModel.cs | 31 | DateTimeOffset UpdatedAt | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SessionListViewModelBase.cs | 12 | class SessionListViewModelBase | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/SessionListViewModelBase.cs | 40 | ObservableCollection<SessionListItem> Sessions | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SessionListViewModelBase.cs | 91 | record SessionListItem | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SettingsViewModelBase.cs | 17 | class SettingsViewModelBase | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 15 | class ChatMessageViewModel | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 17 | string Role | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 18 | string Content | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 19 | DateTimeOffset Timestamp | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 46 | class SessionEntryViewModel | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 48 | string Id | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 49 | string Title | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 50 | string AgentName | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 51 | DateTimeOffset UpdatedAt | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 52 | string? ParentId | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 73 | class ProviderEntryViewModel | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 75 | string Id | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 76 | string DisplayName | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 77 | string Description | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 78 | IReadOnlyList<ModelEntryViewModel> Models | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 90 | class ModelEntryViewModel | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 92 | string Id | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 93 | string DisplayName | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 94 | int ContextWindow | **NO** | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 95 | int MaxOutputTokens | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 96 | bool SupportsVision | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 97 | bool SupportsTools | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 120 | class CommandEntry | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 122 | string Id | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 123 | string Title | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 124 | string Description | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 125 | string Category | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 141 | enum DiffLineKind | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 149 | class DiffLineViewModel | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 151 | string Text | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 152 | DiffLineKind Kind | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 169 | class DiffHunkViewModel | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 171 | string Header | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 172 | IReadOnlyList<DiffLineViewModel> Lines | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 186 | class TokenBarViewModel | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 188 | string Label | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 189 | double InputHeight | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 190 | double OutputHeight | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 191 | string InputBrushKey | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 192 | string OutputBrushKey | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 209 | class ToastViewModel | YES | MED |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 211 | string Id | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 212 | string Message | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 213 | Harbor.Desktop.Abstractions.Models.ToastKind Kind | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 214 | DateTimeOffset CreatedAt | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/SharedDataModels.cs | 215 | TimeSpan TimeToLive | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ThemeSettingsViewModelBase.cs | 20 | class ThemeSettingsViewModelBase | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ThemeSettingsViewModelBase.cs | 80 | public void Apply(string theme) | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ToastNotificationViewModelBase.cs | 12 | class ToastNotificationViewModelBase | YES | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/ToastNotificationViewModelBase.cs | 26 | ObservableCollection<ActiveToast> ActiveToasts | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/ToastNotificationViewModelBase.cs | 83 | record ActiveToast | YES | LOW |
| Harbor.Desktop.Abstractions/ViewModels/TokenUsageViewModelBase.cs | 5 | class TokenUsageViewModelBase | **NO** | HIGH |
| Harbor.Desktop.Abstractions/ViewModels/TokenUsageViewModelBase.cs | 28 | ObservableCollection<TokenUsageRow> Rows | **NO** | LOW |
| Harbor.Desktop.Abstractions/ViewModels/TokenUsageViewModelBase.cs | 38 | record TokenUsageRow | **NO** | LOW |

## Project: Harbor.Desktop.Animations

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Desktop.Animations/AnimationDurations.cs | 8 | class AnimationDurations | YES | MED |
| Harbor.Desktop.Animations/AnimationTokens.cs | 7 | class AnimationTokens | YES | MED |
| Harbor.Desktop.Animations/EasingFunctions.cs | 9 | class EasingFunctions | YES | MED |
| Harbor.Desktop.Animations/EasingFunctions.cs | 12 | public static double Linear(double t) => t; | YES | MED |
| Harbor.Desktop.Animations/EasingFunctions.cs | 15 | public static double EaseIn(double t) => t * t; | YES | MED |
| Harbor.Desktop.Animations/EasingFunctions.cs | 18 | public static double EaseOut(double t) => t * (2 - t); | YES | MED |
| Harbor.Desktop.Animations/EasingFunctions.cs | 21 | public static double EaseInOut(double t) | YES | MED |
| Harbor.Desktop.Animations/EasingFunctions.cs | 25 | public static double CubicInOut(double t) | YES | MED |
| Harbor.Desktop.Animations/EasingFunctions.cs | 29 | public static double QuarticOut(double t) | YES | MED |
| Harbor.Desktop.Animations/EasingFunctions.cs | 33 | public static double QuinticInOut(double t) | YES | MED |
| Harbor.Desktop.Animations/EasingFunctions.cs | 37 | public static double Spring(double t) | YES | MED |
| Harbor.Desktop.Animations/EasingFunctions.cs | 46 | public static Func<double, double> Resolve(string name) => name switch | YES | HIGH |
| Harbor.Desktop.Animations/EasingFunctions.cs | 59 | public static double Apply(Func<double, double> easing, double progress) | YES | HIGH |
| Harbor.Desktop.Animations/Transitions.cs | 8 | record FadeTransition | YES | LOW |
| Harbor.Desktop.Animations/Transitions.cs | 25 | record SlideTransition | YES | LOW |
| Harbor.Desktop.Animations/Transitions.cs | 42 | record ScaleTransition | YES | LOW |
| Harbor.Desktop.Animations/Transitions.cs | 58 | record ColorTransition | YES | LOW |
| Harbor.Desktop.Animations/Transitions.cs | 64 | public RgbColor Interpolate(double t) | YES | MED |

## Project: Harbor.Desktop.Shared

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Desktop.Shared/Commands/BuiltInCommands.cs | 9 | class BuiltInCommands | YES | MED |
| Harbor.Desktop.Shared/Commands/BuiltInCommands.cs | 21 | public static IReadOnlyList<CommandPaletteItem> Templates() => | YES | MED |
| Harbor.Desktop.Shared/Commands/SlashCommands.cs | 9 | class SlashCommands | YES | MED |
| Harbor.Desktop.Shared/Commands/SlashCommands.cs | 30 | public static Entry? Find(string command) | YES | HIGH |
| Harbor.Desktop.Shared/Commands/SlashCommands.cs | 48 | record Entry | YES | LOW |
| Harbor.Desktop.Shared/Locators/IShowPlaceholderFactory.cs | 12 | interface IShowPlaceholderOverlay | YES | HIGH |
| Harbor.Desktop.Shared/Locators/IShowPlaceholderFactory.cs | 25 | interface IShowPlaceholderFactory | YES | HIGH |
| Harbor.Desktop.Shared/Locators/IShowPlaceholderFactory.cs | 47 | class ShowPlaceholderFactory | YES | HIGH |
| Harbor.Desktop.Shared/Locators/IShowPlaceholderFactory.cs | 62 | public IShowPlaceholderOverlay CreatePlaceholder(string modalToken) | YES | HIGH |
| Harbor.Desktop.Shared/Locators/IShowPlaceholderFactory.cs | 94 | string OverlayId | **NO** | LOW |
| Harbor.Desktop.Shared/Locators/IViewModelLocator.cs | 16 | interface IViewModelLocator | YES | HIGH |
| Harbor.Desktop.Shared/Locators/LocatorRegistration.cs | 20 | class LocatorRegistration | YES | MED |
| Harbor.Desktop.Shared/Locators/LocatorRegistration.cs | 30 | public static void AddViewModelLocator(this IServiceCollection services) | YES | HIGH |
| Harbor.Desktop.Shared/Locators/ViewModelLocator.cs | 22 | class ViewModelLocator | YES | MED |
| Harbor.Desktop.Shared/Services/FuzzySearchService.cs | 8 | class FuzzySearchService | YES | HIGH |
| Harbor.Desktop.Shared/Services/FuzzySearchService.cs | 17 | public int Score(string candidate, string query) | YES | MED |
| Harbor.Desktop.Shared/Services/MarkdownToPlainTextService.cs | 10 | class MarkdownToPlainTextService | YES | HIGH |
| Harbor.Desktop.Shared/Services/MarkdownToPlainTextService.cs | 19 | public string ToPlainText(string markdown) | YES | MED |
| Harbor.Desktop.Shared/Services/MarkdownToPlainTextService.cs | 29 | public string ToSummary(string markdown, int maxChars = 100) | YES | HIGH |
| Harbor.Desktop.Shared/Services/RecentItemsService.cs | 7 | class RecentItemsService | YES | HIGH |
| Harbor.Desktop.Shared/Services/RecentItemsService.cs | 43 | public static string DefaultPath() | YES | MED |
| Harbor.Desktop.Shared/Services/RecentItemsService.cs | 50 | public void Add(string item) | YES | HIGH |
| Harbor.Desktop.Shared/Services/RecentItemsService.cs | 66 | public void Remove(string item) | YES | HIGH |
| Harbor.Desktop.Shared/Services/RecentItemsService.cs | 76 | public void Clear() | YES | HIGH |

## Project: Harbor.Diagnostics.Abstractions

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Diagnostics.Abstractions/Correlation.cs | 9 | record CorrelationContext | YES | LOW |
| Harbor.Diagnostics.Abstractions/Correlation.cs | 14 | CorrelationContext None | **NO** | LOW |
| Harbor.Diagnostics.Abstractions/Correlation.cs | 22 | class Correlation | YES | MED |
| Harbor.Diagnostics.Abstractions/Correlation.cs | 33 | public static IDisposable Push(CorrelationContext context) | YES | MED |
| Harbor.Diagnostics.Abstractions/Correlation.cs | 47 | public void Dispose() | **NO** | HIGH |
| Harbor.Diagnostics.Abstractions/NullTelemetry.cs | 4 | class NullTracer | YES | MED |
| Harbor.Diagnostics.Abstractions/NullTelemetry.cs | 12 | public ITelemetrySpan? StartSpan(string name, params KeyValuePair<string, object?>[] tags) | **NO** | HIGH |
| Harbor.Diagnostics.Abstractions/NullTelemetry.cs | 17 | class NullMetrics | YES | MED |
| Harbor.Diagnostics.Abstractions/NullTelemetry.cs | 25 | public void Counter(string name, double value = 1, params KeyValuePair<string, object?>[] tags) | **NO** | HIGH |
| Harbor.Diagnostics.Abstractions/NullTelemetry.cs | 29 | public void Histogram(string name, double value, params KeyValuePair<string, object?>[] tags) | **NO** | MED |
| Harbor.Diagnostics.Abstractions/TelemetryContracts.cs | 8 | interface ITelemetrySpan | YES | HIGH |
| Harbor.Diagnostics.Abstractions/TelemetryContracts.cs | 25 | interface ITracer | YES | HIGH |
| Harbor.Diagnostics.Abstractions/TelemetryContracts.cs | 40 | interface IMetrics | YES | HIGH |

## Project: Harbor.Extensions

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Extensions/ArrayPoolExtensions.cs | 9 | class ArrayPoolExtensions | YES | MED |
| Harbor.Extensions/ArrayPoolExtensions.cs | 47 | int Length | YES | LOW |
| Harbor.Extensions/ArrayPoolExtensions.cs | 64 | public void Dispose() => _pool.Return(Array); | YES | HIGH |
| Harbor.Extensions/ArrayPoolExtensions.cs | 72 | class StringBuilderPool | YES | MED |
| Harbor.Extensions/ArrayPoolExtensions.cs | 85 | public static PooledStringBuilder Rent(int capacity = 256) | YES | MED |
| Harbor.Extensions/ArrayPoolExtensions.cs | 103 | struct PooledStringBuilder | YES | LOW |
| Harbor.Extensions/ArrayPoolExtensions.cs | 108 | StringBuilder Builder | YES | HIGH |
| Harbor.Extensions/ArrayPoolExtensions.cs | 123 | public override string ToString() => Builder.ToString(); | YES | MED |
| Harbor.Extensions/ArrayPoolExtensions.cs | 129 | public void Dispose() | YES | HIGH |
| Harbor.Extensions/CollectionExtensions.cs | 7 | class CollectionExtensions | YES | MED |
| Harbor.Extensions/MemoryPackExtensions.cs | 7 | class MemoryPackExtensions | YES | MED |

## Project: Harbor.Hosting

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Hosting/HarborComposeOptions.cs | 11 | enum HarborToolSetKind | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 21 | enum HarborAgentModelSource | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 31 | enum HarborProviderFlavor | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 44 | class HarborComposeOptions | YES | MED |
| Harbor.Hosting/HarborComposeOptions.cs | 47 | string HarborDir | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 50 | string DefaultStorageBackend | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 57 | string? DefaultTuiRenderer | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 69 | string? ConfigPath | YES | HIGH |
| Harbor.Hosting/HarborComposeOptions.cs | 75 | Microsoft.Extensions.Configuration.IConfiguration? Configuration | YES | HIGH |
| Harbor.Hosting/HarborComposeOptions.cs | 81 | Func<ILoggerFactory>? BootstrapLoggerFactory | YES | HIGH |
| Harbor.Hosting/HarborComposeOptions.cs | 84 | HarborFeatureSet Features | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 87 | HarborToolSetKind ToolSet | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 90 | bool IncludeMcpTools | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 93 | HarborAgentModelSource ModelSource | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 96 | HarborProviderFlavor Providers | YES | HIGH |
| Harbor.Hosting/HarborComposeOptions.cs | 99 | Harbor.Abstractions.Providers.IAuthResolver? DesktopAuthResolver | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 102 | Harbor.Providers.OpenAiCompatible.IModelCatalog? DesktopModelCatalog | YES | LOW |
| Harbor.Hosting/HarborComposeOptions.cs | 108 | bool RegisterCommonConfigStore | YES | HIGH |
| Harbor.Hosting/HarborComposeOptions.cs | 111 | Action<HarborCompositionContext>? AfterConfiguration | YES | HIGH |
| Harbor.Hosting/HarborComposeOptions.cs | 114 | public static HarborComposeOptions CliDefault() => new() | YES | MED |
| Harbor.Hosting/HarborComposeOptions.cs | 121 | public static HarborComposeOptions DesktopDefault() => new() | YES | MED |
| Harbor.Hosting/HarborCompositionContext.cs | 21 | class HarborCompositionContext | YES | MED |
| Harbor.Hosting/HarborCompositionContext.cs | 30 | HarborComposeOptions Options | **NO** | HIGH |
| Harbor.Hosting/HarborCompositionContext.cs | 33 | ILoggerFactory LoggerFactory | YES | HIGH |
| Harbor.Hosting/HarborCompositionContext.cs | 35 | ILogger Logger | **NO** | LOW |
| Harbor.Hosting/HarborCompositionContext.cs | 38 | CommonConfig Common | YES | LOW |
| Harbor.Hosting/HarborCompositionContext.cs | 41 | HarborConfig Harbor | YES | LOW |
| Harbor.Hosting/HarborCompositionContext.cs | 47 | HarborRegistries Registries | YES | LOW |
| Harbor.Hosting/HarborCompositionContext.cs | 51 | class HarborRegistries | YES | MED |
| Harbor.Hosting/HarborCompositionContext.cs | 53 | AgentRegistry Agents | **NO** | LOW |
| Harbor.Hosting/HarborCompositionContext.cs | 54 | ToolRegistry Tools | **NO** | LOW |
| Harbor.Hosting/HarborCompositionContext.cs | 55 | ProviderRegistry Providers | **NO** | HIGH |
| Harbor.Hosting/HarborCompositionContext.cs | 56 | PanelRegistry Panels | **NO** | LOW |
| Harbor.Hosting/HarborCompositionContext.cs | 58 | internal void Freeze() | **NO** | LOW |
| Harbor.Hosting/HarborFeatureSet.cs | 4 | record HarborFeatureSet | YES | LOW |
| Harbor.Hosting/HarborFeatureSet.cs | 6 | HarborFeatureSet Disabled | **NO** | LOW |
| Harbor.Hosting/HarborFeatureSet.cs | 14 | class HarborBuildFeatures | YES | MED |
| Harbor.Hosting/Modules/ConfigAuthResolver.cs | 17 | class ConfigAuthResolver | YES | MED |
| Harbor.Hosting/Modules/ConfigAuthResolver.cs | 28 | public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Hosting/Modules/ConfigurationModule.cs | 15 | class ConfigurationModule | **NO** | HIGH |
| Harbor.Hosting/Modules/ConfigurationModule.cs | 22 | internal static HarborCompositionContext AddHarborConfiguration( | YES | LOW |
| Harbor.Hosting/Modules/CoreModule.cs | 14 | class CoreModule | **NO** | HIGH |
| Harbor.Hosting/Modules/CoreModule.cs | 21 | internal static IServiceCollection AddHarborCore( | YES | LOW |
| Harbor.Hosting/Modules/HttpClientsModule.cs | 6 | class HttpClientsModule | **NO** | HIGH |
| Harbor.Hosting/Modules/HttpClientsModule.cs | 8 | internal static IServiceCollection AddHarborHttpClients( | **NO** | LOW |
| Harbor.Hosting/Modules/IntelligenceModule.cs | 10 | class IntelligenceModule | **NO** | HIGH |
| Harbor.Hosting/Modules/IntelligenceModule.cs | 13 | internal static IServiceCollection AddHarborIntelligence( | YES | LOW |
| Harbor.Hosting/Modules/IpcModule.cs | 12 | class IpcModule | **NO** | HIGH |
| Harbor.Hosting/Modules/IpcModule.cs | 15 | internal static IServiceCollection AddHarborIpc( | YES | LOW |
| Harbor.Hosting/Modules/JsonProviderDiscovery.cs | 15 | class JsonProviderDiscovery | YES | HIGH |
| Harbor.Hosting/Modules/JsonProviderDiscovery.cs | 39 | public static IEnumerable<string> FindProvidersDirectories() | YES | HIGH |
| Harbor.Hosting/Modules/JsonProviderDiscovery.cs | 67 | public static void RegisterDesktopProviders( | YES | HIGH |
| Harbor.Hosting/Modules/JsonProviderDiscovery.cs | 125 | public static void RegisterJsonProviders( | **NO** | HIGH |
| Harbor.Hosting/Modules/PluginLoadHostAdapter.cs | 30 | class PluginLoadHost | YES | HIGH |
| Harbor.Hosting/Modules/PluginLoadHostAdapter.cs | 110 | public Result RegisterTool(ITool tool) => _tools.Register(tool); | YES | HIGH |
| Harbor.Hosting/Modules/PluginLoadHostAdapter.cs | 113 | public Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory) | YES | HIGH |
| Harbor.Hosting/Modules/PluginLoadHostAdapter.cs | 120 | public Result RegisterAgent(AgentDefinition agent) => _agents.Register(agent); | YES | HIGH |
| Harbor.Hosting/Modules/PluginLoadHostAdapter.cs | 123 | public Result RegisterTuiPlugin(ITuiPlugin plugin) | YES | HIGH |
| Harbor.Hosting/Modules/PluginLoadHostAdapter.cs | 133 | public Result RegisterPanelProvider(IPanelProvider panel) => Panels.Register(panel); | YES | HIGH |
| Harbor.Hosting/Modules/ProviderFactories.cs | 25 | class ProviderFactories | **NO** | HIGH |
| Harbor.Hosting/Modules/ProviderFactories.cs | 27 | internal static ProviderRegistry CreateProviderRegistry(HarborCompositionContext ctx, IServiceCollection services) | **NO** | LOW |
| Harbor.Hosting/Modules/ProviderFactories.cs | 75 | class OllamaProviderFactory | **NO** | HIGH |
| Harbor.Hosting/Modules/ProviderFactories.cs | 86 | public ILlmClient CreateClient(ILoggerFactory loggerFactory) => new OllamaLlmClient( | **NO** | HIGH |
| Harbor.Hosting/Modules/ProviderFactories.cs | 93 | class AnthropicProviderFactory | **NO** | HIGH |
| Harbor.Hosting/Modules/ProviderFactories.cs | 106 | public ILlmClient CreateClient(ILoggerFactory loggerFactory) => new AnthropicLlmClient( | **NO** | HIGH |
| Harbor.Hosting/Modules/ProviderFactories.cs | 113 | class OpenAiProviderFactory | **NO** | HIGH |
| Harbor.Hosting/Modules/ProviderFactories.cs | 126 | public ILlmClient CreateClient(ILoggerFactory loggerFactory) => new OpenAILlmClient( | **NO** | HIGH |
| Harbor.Hosting/Modules/ProviderFactories.cs | 135 | class DesktopOllamaProviderFactory | YES | HIGH |
| Harbor.Hosting/Modules/ProviderFactories.cs | 146 | public ILlmClient CreateClient(ILoggerFactory loggerFactory) => new OllamaLlmClient( | **NO** | HIGH |
| Harbor.Hosting/Modules/RegistriesModule.cs | 25 | class RegistriesModule | **NO** | HIGH |
| Harbor.Hosting/Modules/RegistriesModule.cs | 32 | internal static IServiceCollection AddHarborRegistries( | YES | LOW |
| Harbor.Hosting/Modules/StorageModule.cs | 9 | class StorageModule | **NO** | HIGH |
| Harbor.Hosting/Modules/StorageModule.cs | 16 | internal static IServiceCollection AddHarborStorage( | YES | LOW |
| Harbor.Hosting/Modules/TelemetryModule.cs | 15 | class TelemetryModule | YES | HIGH |
| Harbor.Hosting/Modules/TelemetryModule.cs | 17 | internal static IServiceCollection AddHarborTelemetry(this IServiceCollection services) | **NO** | LOW |
| Harbor.Hosting/Modules/ToolsCatalog.cs | 11 | class ToolsCatalog | **NO** | HIGH |
| Harbor.Hosting/Modules/ToolsCatalog.cs | 13 | internal static AgentRegistry CreateAgentRegistry(HarborCompositionContext ctx) | **NO** | LOW |
| Harbor.Hosting/Modules/ToolsCatalog.cs | 31 | internal static string ResolveDefaultModelFromCommon(Harbor.Desktop.Abstractions.Configuration.CommonConfig commonConfig... | YES | LOW |
| Harbor.Hosting/Modules/ToolsCatalog.cs | 44 | internal static IMcpRegistry CreateMcpRegistry(HarborCompositionContext ctx) | **NO** | LOW |
| Harbor.Hosting/Modules/ToolsCatalog.cs | 74 | internal static ToolRegistry CreateToolRegistry( | **NO** | LOW |
| Harbor.Hosting/Modules/TuiModule.cs | 9 | class TuiModule | **NO** | HIGH |
| Harbor.Hosting/Modules/TuiModule.cs | 15 | internal static IServiceCollection AddHarborTui( | YES | LOW |
| Harbor.Hosting/Registration.cs | 11 | class Registration | YES | MED |
| Harbor.Hosting/Registration.cs | 13 | public static HarborCompositionContext AddHarbor( | **NO** | HIGH |

## Project: Harbor.Ipc.Abstractions

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 16 | class DaemonBindPolicy | YES | MED |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 25 | public static Result<IPAddress> ResolveBindAddress(string? listenOn) | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 47 | public static bool IsTailscaleAddress(IPAddress address) | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 56 | public static bool IsPrivateLanAddress(IPAddress address) | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 70 | public static IPAddress? FindTailscaleAddress() | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 84 | public static IReadOnlyList<IPAddress> LanAddresses() | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 104 | public static IPAddress? SelectAdvertiseAddress() | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 149 | class PskStore | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 152 | string DefaultPath | YES | LOW |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 156 | public static string Generate() | YES | MED |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 164 | public static Result<string> Load(string path) | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 185 | public static Result Save(string path, string psk) | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 208 | public static Result<string> LoadOrBootstrap(string path) | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/DaemonBindPolicy.cs | 224 | public static bool Matches(string? provided, string expected) | YES | MED |
| Harbor.Ipc.Abstractions/Endpoints/EndpointDescriptor.cs | 26 | record EndpointDescriptor | YES | LOW |
| Harbor.Ipc.Abstractions/Endpoints/EndpointDescriptor.cs | 29 | record Uds | YES | LOW |
| Harbor.Ipc.Abstractions/Endpoints/EndpointDescriptor.cs | 35 | record Tcp | YES | LOW |
| Harbor.Ipc.Abstractions/Endpoints/EndpointDescriptor.cs | 38 | string? Psk | YES | LOW |
| Harbor.Ipc.Abstractions/Endpoints/EndpointDescriptor.cs | 46 | record Tailscale | YES | LOW |
| Harbor.Ipc.Abstractions/Endpoints/EndpointDescriptor.cs | 52 | string? Psk | YES | LOW |
| Harbor.Ipc.Abstractions/Endpoints/HostsCatalog.cs | 30 | class HostsCatalog | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/HostsCatalog.cs | 36 | string DefaultPath | YES | LOW |
| Harbor.Ipc.Abstractions/Endpoints/HostsCatalog.cs | 43 | public static Result<IReadOnlyDictionary<string, EndpointDescriptor>> Load(string path) | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/HostsCatalog.cs | 87 | public static Result<EndpointDescriptor> Resolve( | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/PairingCode.cs | 15 | class PairingCode | YES | MED |
| Harbor.Ipc.Abstractions/Endpoints/PairingCode.cs | 21 | public static string GeneratePsk() | YES | MED |
| Harbor.Ipc.Abstractions/Endpoints/PairingCode.cs | 29 | public static string Build(string host, int port, string psk) | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/PairingCode.cs | 38 | public static Result<(string Host, int Port, string Psk)> Parse(string code) | YES | HIGH |
| Harbor.Ipc.Abstractions/Endpoints/PairingCode.cs | 75 | record DaemonPairingInfo | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 22 | record HarborEvent | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 31 | record AgentStarted | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 38 | record MessageUpdate | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 45 | record MessageEnd | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 52 | record ToolStart | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 59 | record ToolEnd | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 66 | record TurnStart | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 73 | record TurnEnd | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 80 | record AgentEnded | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 87 | record AgentError | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 94 | record CompactionStarted | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 101 | record CompactionCompleted | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEvent.cs | 113 | enum HarborEventKind | YES | LOW |
| Harbor.Ipc.Abstractions/HarborEventMapping.cs | 17 | class HarborEventMapping | YES | MED |
| Harbor.Ipc.Abstractions/HarborEventMapping.cs | 22 | public static HarborEventData ToData(HarborEvent evt) => evt switch | YES | MED |
| Harbor.Ipc.Abstractions/HarborEventMapping.cs | 47 | public static HarborEvent FromData(HarborEventData data) => data switch | YES | MED |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 42 | interface IHarborClient | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 52 | bool IsConnected | YES | LOW |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 63 | public Task<Result> StartAgentAsync(string sessionId, string agentName, CancellationToken ct = default); | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 70 | public Task<Result> AbortAgentAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 80 | public Task<Result> SendPromptAsync(string prompt, CancellationToken ct = default); | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 86 | public Task<Result<Session>> CreateSessionAsync(string dir, string agent, string provider, string model, CancellationTok... | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 89 | public Task<Result<IReadOnlyList<Session>>> ListSessionsAsync(CancellationToken ct = default); | YES | MED |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 92 | public Task<Result<Session>> GetSessionAsync(string sessionId, CancellationToken ct = default); | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 95 | public Task<Result> DeleteSessionAsync(string sessionId, CancellationToken ct = default); | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 98 | public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default); | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 103 | public Task<Result<IReadOnlyList<ProviderId>>> ListProvidersAsync(CancellationToken ct = default); | YES | MED |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 106 | public Task<Result<IReadOnlyList<ModelInfo>>> ListModelsAsync(string? providerId = null, CancellationToken ct = default)... | YES | MED |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 111 | public Task<Result<IReadOnlyList<ToolDescriptor>>> ListToolsAsync(CancellationToken ct = default); | YES | MED |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 122 | public IAsyncEnumerable<HarborEvent> SubscribeToEventsAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 125 | public Task ConnectAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborClient.cs | 128 | public Task DisconnectAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborServer.cs | 24 | interface IHarborServer | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborServer.cs | 28 | bool IsRunning | YES | LOW |
| Harbor.Ipc.Abstractions/IHarborServer.cs | 31 | string Endpoint | YES | LOW |
| Harbor.Ipc.Abstractions/IHarborServer.cs | 37 | public Task StartAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Ipc.Abstractions/IHarborServer.cs | 45 | public Task StopAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Ipc.Abstractions/IPipeTransport.cs | 34 | interface IPipeTransport | YES | HIGH |
| Harbor.Ipc.Abstractions/IPipeTransport.cs | 37 | string Endpoint | YES | LOW |
| Harbor.Ipc.Abstractions/IPipeTransport.cs | 40 | bool IsBound | YES | LOW |
| Harbor.Ipc.Abstractions/IPipeTransport.cs | 50 | interface IIpcClientTransport | YES | HIGH |
| Harbor.Ipc.Abstractions/IPipeTransport.cs | 65 | interface IIpcServerTransport | YES | HIGH |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 26 | record HarborEventData | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 30 | record HarborEventAgentStarted | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 35 | record HarborEventMessageUpdate | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 41 | record HarborEventMessageEnd | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 46 | record HarborEventToolStart | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 52 | record HarborEventToolEnd | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 58 | record HarborEventTurnStart | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 63 | record HarborEventTurnEnd | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 68 | record HarborEventAgentEnded | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 73 | record HarborEventAgentError | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 78 | record HarborEventCompactionStarted | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborEventData.cs | 83 | record HarborEventCompactionCompleted | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 50 | record HarborRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 57 | Guid RequestId | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 64 | record StartAgentRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 82 | record AbortAgentRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 86 | record SendPromptRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 97 | record CreateSessionRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 109 | record ListSessionsRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 113 | record GetSessionRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 122 | record DeleteSessionRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 131 | record GetMessagesRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 142 | record ListProvidersRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 146 | record ListModelsRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 157 | record ListToolsRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 167 | record SubscribeToEventsRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 176 | ulong? LastSequence | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 187 | record ConnectRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 191 | record DisconnectRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborRequest.cs | 200 | record PskAuthRequest | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborResponse.cs | 39 | record HarborResponse | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborResponse.cs | 47 | Guid RequestId | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborResponse.cs | 56 | record OkResponse | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborResponse.cs | 72 | record ErrorResponse | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborResponse.cs | 76 | string Message | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborResponse.cs | 93 | record EventEnvelope | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborResponse.cs | 107 | ulong Sequence | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/HarborResponse.cs | 115 | string? TargetClientId | YES | HIGH |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 21 | class SessionMessagePackFormatter | YES | MED |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 23 | public void Serialize( | **NO** | HIGH |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 58 | public Session? Deserialize( | **NO** | HIGH |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 111 | class SessionMetadataMessagePackFormatter | YES | MED |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 113 | public void Serialize( | **NO** | HIGH |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 144 | public SessionMetadata? Deserialize( | **NO** | HIGH |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 190 | class ProviderIdMessagePackFormatter | YES | HIGH |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 192 | public void Serialize(ref MessagePackWriter writer, ProviderId? value, MessagePackSerializerOptions options) | **NO** | HIGH |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 198 | public ProviderId? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) | **NO** | HIGH |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 207 | class ToolDescriptorMessagePackFormatter | YES | HIGH |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 209 | public void Serialize(ref MessagePackWriter writer, ToolDescriptor? value, MessagePackSerializerOptions options) | **NO** | HIGH |
| Harbor.Ipc.Abstractions/Protocol/SessionMessagePackFormatters.cs | 229 | public ToolDescriptor? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) | **NO** | HIGH |
| Harbor.Ipc.Abstractions/Protocol/SubscriptionAck.cs | 11 | record SubscriptionAck | YES | LOW |
| Harbor.Ipc.Abstractions/Protocol/WireCodec.cs | 33 | class WireCodec | YES | MED |
| Harbor.Ipc.Abstractions/Protocol/WireCodec.cs | 88 | public static async Task WriteResponseAsync( | YES | HIGH |
| Harbor.Ipc.Abstractions/Protocol/WireCodec.cs | 101 | public static async Task WriteRequestAsync( | YES | HIGH |
| Harbor.Ipc.Abstractions/Protocol/WireCodec.cs | 115 | public static async Task<HarborRequest?> ReadRequestAsync( | YES | HIGH |
| Harbor.Ipc.Abstractions/Protocol/WireCodec.cs | 129 | public static async Task<HarborResponse?> ReadResponseAsync( | YES | HIGH |
| Harbor.Ipc.Abstractions/Protocol/WireCodec.cs | 250 | class IpcLogCategories | YES | MED |

## Project: Harbor.Ipc.Client

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Ipc.Client/IpcHarborClient.cs | 25 | class IpcHarborClient | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 82 | public async Task ConnectAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 91 | public async Task DisconnectAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 102 | public async Task<Result> StartAgentAsync(string sessionId, string agentName, CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 111 | public async Task<Result> AbortAgentAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 120 | public async Task<Result> SendPromptAsync(string prompt, CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 131 | public async Task<Result<Session>> CreateSessionAsync( | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 141 | public async Task<Result<IReadOnlyList<Session>>> ListSessionsAsync(CancellationToken ct = default) | YES | MED |
| Harbor.Ipc.Client/IpcHarborClient.cs | 152 | public async Task<Result<Session>> GetSessionAsync(string sessionId, CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 161 | public async Task<Result> DeleteSessionAsync(string sessionId, CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 170 | public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync( | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 184 | public async Task<Result<IReadOnlyList<ProviderId>>> ListProvidersAsync(CancellationToken ct = default) | YES | MED |
| Harbor.Ipc.Client/IpcHarborClient.cs | 195 | public async Task<Result<IReadOnlyList<ModelInfo>>> ListModelsAsync( | YES | MED |
| Harbor.Ipc.Client/IpcHarborClient.cs | 209 | public async Task<Result<IReadOnlyList<ToolDescriptor>>> ListToolsAsync(CancellationToken ct = default) | YES | MED |
| Harbor.Ipc.Client/IpcHarborClient.cs | 222 | public async IAsyncEnumerable<HarborEvent> SubscribeToEventsAsync( | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 240 | public async ValueTask DisposeAsync() | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClient.cs | 252 | class HarborResponseExtensions | YES | MED |
| Harbor.Ipc.Client/IpcHarborClient.cs | 255 | public static OkResponse AsOk(this HarborResponse response) | YES | MED |
| Harbor.Ipc.Client/IpcHarborClientExtensions.cs | 8 | class IpcHarborClientExtensions | YES | HIGH |
| Harbor.Ipc.Client/IpcHarborClientExtensions.cs | 17 | public static IServiceCollection UseIpcHarborClient(this IServiceCollection services, string pipeName = "harbor-ipc") | YES | MED |
| Harbor.Ipc.Client/Protocol/EventSubscription.cs | 8 | class EventSubscription | YES | MED |
| Harbor.Ipc.Client/Protocol/EventSubscription.cs | 24 | public async IAsyncEnumerable<HarborEvent> ReadAllAsync( | YES | HIGH |
| Harbor.Ipc.Client/Protocol/MessagePackRpcClient.cs | 24 | class MessagePackRpcClient | YES | HIGH |
| Harbor.Ipc.Client/Protocol/MessagePackRpcClient.cs | 72 | event EventHandler ConnectionLost | YES | MED |
| Harbor.Ipc.Client/Protocol/MessagePackRpcClient.cs | 75 | public async ValueTask DisposeAsync() | YES | HIGH |
| Harbor.Ipc.Client/Protocol/MessagePackRpcClient.cs | 110 | public async Task ConnectAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/Protocol/MessagePackRpcClient.cs | 129 | public async Task<HarborResponse> SendAsync(HarborRequest request, CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/Protocol/MessagePackRpcClient.cs | 264 | record struct | YES | LOW |
| Harbor.Ipc.Client/Protocol/ReconnectableRpcClient.cs | 27 | class ReconnectableRpcClient | YES | HIGH |
| Harbor.Ipc.Client/Protocol/ReconnectableRpcClient.cs | 46 | event EventHandler? Connected | YES | MED |
| Harbor.Ipc.Client/Protocol/ReconnectableRpcClient.cs | 59 | public TimeSpan NextBackoffDelay() | YES | MED |
| Harbor.Ipc.Client/Protocol/ReconnectableRpcClient.cs | 78 | public async Task<MessagePackRpcClient> ConnectAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/Protocol/ReconnectableRpcClient.cs | 85 | public async Task<HarborResponse> SendAsync(HarborRequest request, CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/Protocol/ReconnectableRpcClient.cs | 98 | public async IAsyncEnumerable<EventFrame> SubscribeWithReconnectAsync( | YES | HIGH |
| Harbor.Ipc.Client/Protocol/ReconnectableRpcClient.cs | 209 | public async ValueTask DisposeAsync() | YES | HIGH |
| Harbor.Ipc.Client/Protocol/ReconnectableRpcClient.cs | 300 | public async Task CutCurrentConnectionForTestAsync() | YES | HIGH |
| Harbor.Ipc.Client/Protocol/ReconnectableRpcClient.cs | 326 | public void Dispose() => _inner.ConnectionLost -= Handler; | **NO** | HIGH |
| Harbor.Ipc.Client/Transport/ClientPipeTransport.cs | 21 | class ClientPipeTransport | YES | HIGH |
| Harbor.Ipc.Client/Transport/ClientPipeTransport.cs | 48 | public async ValueTask DisposeAsync() | YES | HIGH |
| Harbor.Ipc.Client/Transport/ClientPipeTransport.cs | 58 | public async Task<Stream> ConnectAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/Transport/ClientPipeTransport.cs | 75 | public async Task DisconnectAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/Transport/TcpClientTransport.cs | 10 | class TcpClientTransport | YES | HIGH |
| Harbor.Ipc.Client/Transport/TcpClientTransport.cs | 28 | string Endpoint | YES | LOW |
| Harbor.Ipc.Client/Transport/TcpClientTransport.cs | 34 | public async ValueTask DisposeAsync() | YES | HIGH |
| Harbor.Ipc.Client/Transport/TcpClientTransport.cs | 44 | public async Task<Stream> ConnectAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Client/Transport/TcpClientTransport.cs | 69 | public async Task DisconnectAsync(CancellationToken ct = default) | YES | HIGH |

## Project: Harbor.Ipc.InProcess

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 39 | class InProcessHarborClient | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 98 | public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask; | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 101 | public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask; | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 120 | public async Task<Result> StartAgentAsync(string sessionId, string agentName, CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 140 | public Task<Result> AbortAgentAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 147 | public async Task<Result> SendPromptAsync(string prompt, CancellationToken ct = default) => await _agent.PromptAsync(pro... | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 152 | public async Task<Result<Session>> CreateSessionAsync( | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 156 | public async Task<Result<IReadOnlyList<Session>>> ListSessionsAsync(CancellationToken ct = default) => await _sessionSto... | YES | MED |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 159 | public async Task<Result<Session>> GetSessionAsync(string sessionId, CancellationToken ct = default) => await _sessionSt... | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 162 | public async Task<Result> DeleteSessionAsync(string sessionId, CancellationToken ct = default) => await _sessionStore.De... | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 165 | public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync( | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 171 | public Task<Result<IReadOnlyList<ProviderId>>> ListProvidersAsync(CancellationToken ct = default) | YES | MED |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 178 | public async Task<Result<IReadOnlyList<ModelInfo>>> ListModelsAsync( | YES | MED |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 200 | public Task<Result<IReadOnlyList<ToolDescriptor>>> ListToolsAsync(CancellationToken ct = default) | YES | MED |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 209 | public async IAsyncEnumerable<HarborEvent> SubscribeToEventsAsync( | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 225 | public async ValueTask DisposeAsync() | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClient.cs | 251 | internal HarborEvent? ProjectEvent(AgentEvent evt) | YES | LOW |
| Harbor.Ipc.InProcess/InProcessHarborClientExtensions.cs | 7 | class InProcessHarborClientExtensions | YES | HIGH |
| Harbor.Ipc.InProcess/InProcessHarborClientExtensions.cs | 17 | public static IServiceCollection UseInProcessHarborClient(this IServiceCollection services) | YES | HIGH |

## Project: Harbor.Ipc.Server

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Ipc.Server/HarborIpcServer.cs | 21 | class HarborIpcServer | YES | MED |
| Harbor.Ipc.Server/HarborIpcServer.cs | 89 | public async Task StartAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Server/HarborIpcServer.cs | 100 | public async Task StopAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Server/HarborIpcServer.cs | 107 | public async ValueTask DisposeAsync() | YES | HIGH |
| Harbor.Ipc.Server/HarborIpcServerExtensions.cs | 6 | class HarborIpcServerExtensions | YES | MED |
| Harbor.Ipc.Server/HarborIpcServerExtensions.cs | 18 | public static IServiceCollection UseHarborIpcServer(this IServiceCollection services, string pipeName = "harbor-ipc") | YES | MED |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 46 | class EventBroadcaster | YES | MED |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 98 | public async ValueTask DisposeAsync() | YES | HIGH |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 117 | public void Start() => _eventBusSubscription = _eventBus.Subscribe(OnEventAsync); | YES | HIGH |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 147 | public async Task<SubscriptionAckData> RegisterAsync( | YES | HIGH |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 211 | public async Task UnregisterAsync(Stream clientStream) | YES | MED |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 447 | class SubscriptionAckData | YES | MED |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 450 | ulong ServerSequence | YES | LOW |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 453 | bool ResyncRequired | YES | LOW |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 481 | Stream Stream | YES | LOW |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 484 | string ClientId | YES | HIGH |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 487 | SemaphoreSlim WriteLock | YES | LOW |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 490 | public bool IsTargetOf(EventEnvelope envelope) | YES | MED |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 497 | public bool TryDeliver(EventEnvelope envelope) => _outbound.Writer.TryWrite(envelope); | YES | MED |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 500 | public void StartWriter(ILogger logger) | YES | HIGH |
| Harbor.Ipc.Server/Protocol/EventBroadcaster.cs | 507 | public async Task StopWriterAsync() | YES | HIGH |
| Harbor.Ipc.Server/Protocol/MessagePackRpcServer.cs | 52 | class MessagePackRpcServer | YES | MED |
| Harbor.Ipc.Server/Protocol/MessagePackRpcServer.cs | 93 | public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false); | YES | HIGH |
| Harbor.Ipc.Server/Protocol/MessagePackRpcServer.cs | 99 | public async Task RunAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Server/Protocol/MessagePackRpcServer.cs | 110 | public async Task StopAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Server/Protocol/RequestDispatcher.cs | 26 | class RequestDispatcher | YES | MED |
| Harbor.Ipc.Server/Protocol/RequestDispatcher.cs | 62 | public async Task<HarborResponse> DispatchAsync( | YES | HIGH |
| Harbor.Ipc.Server/Protocol/RequestDispatcher.cs | 97 | public void ReleaseClientLeases(string clientId) => _leases.ReleaseAll(clientId); | YES | HIGH |
| Harbor.Ipc.Server/Protocol/RequestDispatcher.cs | 100 | public string? GetLeaseOwner(string sessionId) => _leases.GetOwner(sessionId); | YES | HIGH |
| Harbor.Ipc.Server/Protocol/ResilientFrameReader.cs | 12 | enum FrameReadOutcome | YES | LOW |
| Harbor.Ipc.Server/Protocol/ResilientFrameReader.cs | 34 | record struct | YES | LOW |
| Harbor.Ipc.Server/Protocol/ResilientFrameReader.cs | 64 | class ResilientFrameReader | YES | MED |
| Harbor.Ipc.Server/Protocol/ResilientFrameReader.cs | 98 | public async ValueTask<FrameReadResult> ReadRequestAsync(Stream stream, CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Server/Protocol/SessionLeaseRegistry.cs | 10 | class SessionLeaseRegistry | YES | HIGH |
| Harbor.Ipc.Server/Protocol/SessionLeaseRegistry.cs | 16 | public bool TryAcquire(string sessionId, string clientId) | YES | MED |
| Harbor.Ipc.Server/Protocol/SessionLeaseRegistry.cs | 32 | public void Release(string sessionId, string clientId) | YES | HIGH |
| Harbor.Ipc.Server/Protocol/SessionLeaseRegistry.cs | 44 | public void ReleaseAll(string clientId) | YES | HIGH |
| Harbor.Ipc.Server/Protocol/SessionLeaseRegistry.cs | 57 | public string? GetOwner(string sessionId) | YES | HIGH |
| Harbor.Ipc.Server/Transport/ServerPipeTransport.cs | 29 | class ServerPipeTransport | YES | MED |
| Harbor.Ipc.Server/Transport/ServerPipeTransport.cs | 68 | public async ValueTask DisposeAsync() | YES | HIGH |
| Harbor.Ipc.Server/Transport/ServerPipeTransport.cs | 84 | public static TimeSpan ComputeAcceptBackoff(int consecutiveFailures) | YES | HIGH |
| Harbor.Ipc.Server/Transport/ServerPipeTransport.cs | 101 | public async Task<ChannelReader<Stream>> BindAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Server/Transport/ServerPipeTransport.cs | 125 | public async Task UnbindAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Server/Transport/TcpServerTransport.cs | 16 | class TcpServerTransport | YES | MED |
| Harbor.Ipc.Server/Transport/TcpServerTransport.cs | 46 | string Endpoint | YES | LOW |
| Harbor.Ipc.Server/Transport/TcpServerTransport.cs | 49 | int? BoundPort | YES | LOW |
| Harbor.Ipc.Server/Transport/TcpServerTransport.cs | 55 | public async ValueTask DisposeAsync() | YES | HIGH |
| Harbor.Ipc.Server/Transport/TcpServerTransport.cs | 65 | public async Task<ChannelReader<Stream>> BindAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Ipc.Server/Transport/TcpServerTransport.cs | 105 | public async Task UnbindAsync(CancellationToken ct = default) | YES | HIGH |

## Project: Harbor.Logging

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Logging/LoggerSetup.cs | 12 | class LoggerSetup | YES | MED |
| Harbor.Logging/LoggerSetup.cs | 22 | public static ILogger Create( | YES | HIGH |
| Harbor.Logging/LoggerSetup.cs | 80 | public static ILogger CreateWithDiagnostics( | YES | HIGH |
| Harbor.Logging/LoggerSetup.cs | 99 | public static void CleanupOldLogs(string logDir, int maxFiles = 50) | YES | HIGH |
| Harbor.Logging/LoggerSetup.cs | 127 | class DiagnosticsSink | YES | MED |
| Harbor.Logging/LoggerSetup.cs | 136 | public void Emit(LogEvent logEvent) | **NO** | HIGH |

## Project: Harbor.Plugins.Abstractions

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Plugins.Abstractions/CompiledPluginAssembly.cs | 26 | record CompiledPluginAssembly | YES | LOW |
| Harbor.Plugins.Abstractions/IPluginCompiler.cs | 22 | interface IPluginCompiler | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginCompiler.cs | 34 | public Task<CompilationResult> CompileAsync(PluginScript script, CancellationToken ct = default); | YES | MED |
| Harbor.Plugins.Abstractions/IPluginCompiler.cs | 42 | record struct | YES | LOW |
| Harbor.Plugins.Abstractions/IPluginCompiler.cs | 89 | public static CompilationResult Fresh(CompiledPluginAssembly asm) => | YES | MED |
| Harbor.Plugins.Abstractions/IPluginCompiler.cs | 93 | public static CompilationResult Cached(CompiledPluginAssembly asm) => | YES | MED |
| Harbor.Plugins.Abstractions/IPluginCompiler.cs | 97 | public static CompilationResult Failure(string error, IReadOnlyList<Diagnostic> diagnostics) => | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginCompiler.cs | 101 | public static CompilationResult Failure(string error) => | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginInstantiator.cs | 15 | interface IPluginInstantiator | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginInstantiator.cs | 27 | public Result<IReadOnlyList<LoadedPlugin>> Instantiate(CompiledPluginAssembly compiled); | YES | MED |
| Harbor.Plugins.Abstractions/IPluginLoadHost.cs | 32 | interface IPluginLoadHost | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginLoadHost.cs | 40 | IServiceCollection Services | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginLoadHost.cs | 46 | IConfiguration Configuration | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginLoadHost.cs | 52 | ILoggerFactory LoggerFactory | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginLoadHost.cs | 65 | public Result RegisterTool(ITool tool); | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginLoadHost.cs | 74 | public Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory); | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginLoadHost.cs | 81 | public Result RegisterAgent(AgentDefinition agent); | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginLoadHost.cs | 89 | public Result RegisterTuiPlugin(ITuiPlugin plugin); | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginLoadHost.cs | 98 | public Result RegisterPanelProvider(IPanelProvider panel); | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginRegistrar.cs | 16 | interface IPluginRegistrar | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginRegistrar.cs | 25 | public Result Register(LoadedPlugin plugin, IPluginLoadHost host); | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginSource.cs | 24 | interface IPluginSource | YES | HIGH |
| Harbor.Plugins.Abstractions/IPluginSource.cs | 33 | public IAsyncEnumerable<PluginScript> GetScriptsAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Plugins.Abstractions/LoadedPlugin.cs | 16 | record LoadedPlugin | YES | LOW |
| Harbor.Plugins.Abstractions/PluginScript.cs | 8 | class PluginScript | YES | HIGH |
| Harbor.Plugins.Abstractions/PluginScript.cs | 23 | string Path | YES | LOW |
| Harbor.Plugins.Abstractions/PluginScript.cs | 26 | string Source | YES | LOW |
| Harbor.Plugins.Abstractions/PluginScript.cs | 32 | string Hash | YES | LOW |
| Harbor.Plugins.Abstractions/PluginScript.cs | 40 | public static async Task<Result<PluginScript>> LoadAsync(string path, CancellationToken ct = default) | YES | HIGH |

## Project: Harbor.Plugins.Compilation

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Plugins.Compilation/CachingCompiler.cs | 24 | class CachingCompiler | YES | MED |
| Harbor.Plugins.Compilation/CachingCompiler.cs | 47 | public async Task<CompilationResult> CompileAsync(PluginScript script, CancellationToken ct = default) | YES | MED |
| Harbor.Plugins.Compilation/PluginAssemblyReferences.cs | 26 | class PluginAssemblyReferences | YES | HIGH |
| Harbor.Plugins.Compilation/RoslynPluginCompiler.cs | 25 | class RoslynPluginCompiler | YES | HIGH |
| Harbor.Plugins.Compilation/RoslynPluginCompiler.cs | 43 | public Task<CompilationResult> CompileAsync(PluginScript script, CancellationToken ct = default) | YES | MED |

## Project: Harbor.Plugins.Host

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Plugins.Host/McpPluginLoadHost.cs | 23 | class McpPluginLoadHost | YES | HIGH |
| Harbor.Plugins.Host/McpPluginLoadHost.cs | 37 | ILoggerFactory LoggerFactory | **NO** | HIGH |
| Harbor.Plugins.Host/McpPluginLoadHost.cs | 42 | public Result RegisterTool(ITool tool) | **NO** | HIGH |
| Harbor.Plugins.Host/McpPluginLoadHost.cs | 47 | public Result RegisterProvider(ProviderId providerId, Func<ILlmClient> factory) | **NO** | HIGH |
| Harbor.Plugins.Host/McpPluginLoadHost.cs | 54 | public Result RegisterAgent(AgentDefinition agent) | **NO** | HIGH |
| Harbor.Plugins.Host/McpPluginLoadHost.cs | 61 | public Result RegisterTuiPlugin(ITuiPlugin plugin) | **NO** | HIGH |
| Harbor.Plugins.Host/McpPluginLoadHost.cs | 68 | public Result RegisterPanelProvider(IPanelProvider panel) | **NO** | HIGH |
| Harbor.Plugins.Host/McpStdioServer.cs | 17 | class McpStdioServer | YES | MED |
| Harbor.Plugins.Host/McpStdioServer.cs | 32 | public async Task RunAsync(CancellationToken ct) | **NO** | HIGH |
| Harbor.Plugins.Host/NullEventBus.cs | 11 | class NullEventBus | YES | HIGH |
| Harbor.Plugins.Host/NullEventBus.cs | 13 | public Task PublishAsync(AgentEvent @event, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Plugins.Host/NullEventBus.cs | 16 | public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler) | **NO** | HIGH |
| Harbor.Plugins.Host/NullEventBus.cs | 23 | public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) => ImmutableArray<AgentEvent>.Empty; | **NO** | HIGH |
| Harbor.Plugins.Host/NullEventBus.cs | 28 | public void Dispose() { } | **NO** | HIGH |
| Harbor.Plugins.Host/Program.cs | 14 | class Program | **NO** | MED |
| Harbor.Plugins.Host/Program.cs | 16 | public static async Task<int> Main(string[] args) | **NO** | MED |

## Project: Harbor.Plugins.Hosting

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Plugins.Hosting/PluginHost.cs | 22 | class PluginHost | YES | HIGH |
| Harbor.Plugins.Hosting/PluginHost.cs | 68 | public async Task<Result<IReadOnlyList<LoadedPlugin>>> LoadAllAsync( | YES | HIGH |
| Harbor.Plugins.Hosting/PluginHostBuilder.cs | 28 | class PluginHostBuilder | YES | HIGH |
| Harbor.Plugins.Hosting/PluginHostBuilder.cs | 37 | public PluginHostBuilder WithSource(IPluginSource source) | YES | MED |
| Harbor.Plugins.Hosting/PluginHostBuilder.cs | 44 | public PluginHostBuilder WithCompiler(IPluginCompiler compiler) | YES | MED |
| Harbor.Plugins.Hosting/PluginHostBuilder.cs | 51 | public PluginHostBuilder WithInstantiator(IPluginInstantiator instantiator) | YES | MED |
| Harbor.Plugins.Hosting/PluginHostBuilder.cs | 58 | public PluginHostBuilder WithRegistrar(IPluginRegistrar registrar) | YES | MED |
| Harbor.Plugins.Hosting/PluginHostBuilder.cs | 65 | public PluginHostBuilder WithOptions(Action<PluginHostOptions> configure) | YES | MED |
| Harbor.Plugins.Hosting/PluginHostBuilder.cs | 76 | public PluginHost Build(ILogger<PluginHost>? logger = null) | YES | HIGH |
| Harbor.Plugins.Hosting/PluginHostOptions.cs | 6 | class PluginHostOptions | YES | HIGH |
| Harbor.Plugins.Hosting/PluginHostOptions.cs | 12 | string CacheDirectory | YES | LOW |
| Harbor.Plugins.Hosting/PluginHostOptions.cs | 20 | string PluginRoot | YES | LOW |
| Harbor.Plugins.Hosting/PluginHostOptions.cs | 29 | bool ContinueOnError | YES | LOW |

## Project: Harbor.Plugins.Instantiation

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Plugins.Instantiation/PluginLifecycle.cs | 9 | class PluginLifecycle | YES | HIGH |
| Harbor.Plugins.Instantiation/PluginLifecycle.cs | 28 | public static PluginContext BuildContext( | YES | HIGH |
| Harbor.Plugins.Instantiation/PluginLifecycle.cs | 59 | public static Result Initialize(IPlugin plugin, PluginContext context) | YES | HIGH |
| Harbor.Plugins.Instantiation/PluginLifecycle.cs | 82 | public static async Task<Result> ShutdownAsync(IPlugin plugin, CancellationToken ct = default) | YES | MED |
| Harbor.Plugins.Instantiation/ReflectionPluginInstantiator.cs | 12 | class ReflectionPluginInstantiator | YES | HIGH |
| Harbor.Plugins.Instantiation/ReflectionPluginInstantiator.cs | 15 | public Result<IReadOnlyList<LoadedPlugin>> Instantiate(CompiledPluginAssembly compiled) | YES | MED |

## Project: Harbor.Plugins.Registration

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Plugins.Registration/PanelRegistryPluginAdapter.cs | 27 | class PanelRegistryPluginAdapter | YES | HIGH |
| Harbor.Plugins.Registration/PanelRegistryPluginAdapter.cs | 39 | public Result Register(IPanelProvider panel) | YES | HIGH |
| Harbor.Plugins.Registration/PanelRegistryPluginAdapter.cs | 48 | public Result Unregister(string id) => Result.Success(); | YES | MED |
| Harbor.Plugins.Registration/PanelRegistryPluginAdapter.cs | 54 | public IPanelProvider? Get(string id) => null; | YES | HIGH |
| Harbor.Plugins.Registration/PluginRegistrar.cs | 27 | class PluginRegistrar | YES | HIGH |
| Harbor.Plugins.Registration/PluginRegistrar.cs | 49 | public Result Register(LoadedPlugin plugin, IPluginLoadHost host) | YES | HIGH |
| Harbor.Plugins.Registration/PluginRegistrar.cs | 122 | public void AddTool(ITool tool) | **NO** | HIGH |
| Harbor.Plugins.Registration/PluginRegistrar.cs | 130 | public void AddTool(Func<ITool> factory) => AddTool(factory()); | **NO** | HIGH |
| Harbor.Plugins.Registration/PluginRegistrar.cs | 131 | public void AddTool(IToolFactory factory) => AddTool(factory.CreateTool(_loggerFactory)); | **NO** | HIGH |
| Harbor.Plugins.Registration/PluginRegistrar.cs | 132 | public void AddTool(Func<ILoggerFactory, ITool> factory) => AddTool(factory(_loggerFactory)); | **NO** | HIGH |
| Harbor.Plugins.Registration/PluginRegistrar.cs | 163 | public void AddProvider(IProviderFactory factory) | YES | HIGH |
| Harbor.Plugins.Registration/PluginRegistrar.cs | 170 | public void AddProvider(Func<ILlmClient> factory) | **NO** | HIGH |
| Harbor.Plugins.Registration/PluginRegistrar.cs | 181 | public void AddProvider(ProviderId providerId, Func<ILlmClient> factory) | **NO** | HIGH |
| Harbor.Plugins.Registration/PluginRegistrar.cs | 188 | public void AddProvider(string providerId, Func<ILlmClient> factory) | **NO** | HIGH |
| Harbor.Plugins.Registration/PluginRegistrar.cs | 211 | public void AddAgent(AgentDefinition agent) | **NO** | HIGH |
| Harbor.Plugins.Registration/SafePluginRegistrar.cs | 11 | class SafePluginRegistrar | YES | HIGH |
| Harbor.Plugins.Registration/SafePluginRegistrar.cs | 28 | public Result Register(LoadedPlugin plugin, IPluginLoadHost host) | YES | HIGH |

## Project: Harbor.Plugins.Runtime

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Plugins.Runtime/CompiledPlugin.cs | 16 | record CompiledPlugin | YES | LOW |
| Harbor.Plugins.Runtime/CsPluginLoader.cs | 35 | class CsPluginLoader | **NO** | HIGH |
| Harbor.Plugins.Runtime/CsPluginLoader.cs | 79 | public async Task<IReadOnlyList<PluginScript>> DiscoverScriptsAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Plugins.Runtime/CsPluginLoader.cs | 95 | public async Task<Result<IReadOnlyList<CompiledPlugin>>> DiscoverAndLoadAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Plugins.Runtime/CsPluginLoader.cs | 119 | public async Task<PluginCompilationResult> CompileAndLoadAsync(PluginScript script, CancellationToken ct = default) | YES | HIGH |
| Harbor.Plugins.Runtime/CsPluginLoader.cs | 170 | public async Task<Result<IReadOnlyList<CompiledPlugin>>> CompileAndLoadAllAsync(PluginScript script, CancellationToken c... | YES | HIGH |
| Harbor.Plugins.Runtime/PluginCompilationResult.cs | 8 | record struct | YES | LOW |
| Harbor.Plugins.Runtime/PluginCompilationResult.cs | 39 | public static PluginCompilationResult Success(CompiledPlugin plugin) => | YES | MED |
| Harbor.Plugins.Runtime/PluginCompilationResult.cs | 43 | public static PluginCompilationResult Failure(string error, IReadOnlyList<Diagnostic> diagnostics) => | YES | HIGH |
| Harbor.Plugins.Runtime/PluginCompilationResult.cs | 47 | public static PluginCompilationResult Failure(string error) => | YES | HIGH |

## Project: Harbor.Plugins.Storage

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Plugins.Storage/CompositePluginSource.cs | 14 | class CompositePluginSource | YES | HIGH |
| Harbor.Plugins.Storage/CompositePluginSource.cs | 39 | public async IAsyncEnumerable<PluginScript> GetScriptsAsync( | YES | HIGH |
| Harbor.Plugins.Storage/EmbeddedResourcePluginSource.cs | 24 | class EmbeddedResourcePluginSource | YES | HIGH |
| Harbor.Plugins.Storage/EmbeddedResourcePluginSource.cs | 47 | public async IAsyncEnumerable<PluginScript> GetScriptsAsync( | YES | HIGH |
| Harbor.Plugins.Storage/FileSystemPluginSource.cs | 21 | class FileSystemPluginSource | YES | HIGH |
| Harbor.Plugins.Storage/FileSystemPluginSource.cs | 42 | public async IAsyncEnumerable<PluginScript> GetScriptsAsync( | YES | HIGH |
| Harbor.Plugins.Storage/InMemoryPluginSource.cs | 14 | class InMemoryPluginSource | YES | HIGH |
| Harbor.Plugins.Storage/InMemoryPluginSource.cs | 36 | public async IAsyncEnumerable<PluginScript> GetScriptsAsync( | YES | HIGH |
| Harbor.Plugins.Storage/InMemoryPluginSource.cs | 53 | public void Add(PluginScript script) | YES | HIGH |
| Harbor.Plugins.Storage/InMemoryPluginSource.cs | 66 | public void Add(string path, string source) | YES | HIGH |

## Project: Harbor.Providers.Anthropic

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Providers.Anthropic/AnthropicLlmClient.cs | 25 | class AnthropicLlmClient | YES | HIGH |
| Harbor.Providers.Anthropic/AnthropicLlmClient.cs | 59 | ProviderId ProviderId | **NO** | HIGH |
| Harbor.Providers.Anthropic/AnthropicLlmClient.cs | 61 | public async IAsyncEnumerable<LlmEvent> StreamAsync( | **NO** | MED |
| Harbor.Providers.Anthropic/AnthropicLlmClient.cs | 115 | public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) | **NO** | HIGH |
| Harbor.Providers.Anthropic/AnthropicLlmClient.cs | 427 | class AnthropicConfig | YES | MED |
| Harbor.Providers.Anthropic/AnthropicLlmClient.cs | 429 | string? BaseUrl | **NO** | LOW |
| Harbor.Providers.Anthropic/AnthropicLlmClient.cs | 430 | string? ApiVersion | **NO** | LOW |
| Harbor.Providers.Anthropic/AnthropicLlmClient.cs | 431 | string? BetaFeatures | **NO** | LOW |
| Harbor.Providers.Anthropic/AnthropicLlmClient.cs | 437 | class AnthropicModels | YES | MED |

## Project: Harbor.Providers.Ollama

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Providers.Ollama/OllamaLlmClient.cs | 25 | class OllamaLlmClient | YES | HIGH |
| Harbor.Providers.Ollama/OllamaLlmClient.cs | 49 | ProviderId ProviderId | **NO** | HIGH |
| Harbor.Providers.Ollama/OllamaLlmClient.cs | 51 | public async IAsyncEnumerable<LlmEvent> StreamAsync( | **NO** | MED |
| Harbor.Providers.Ollama/OllamaLlmClient.cs | 114 | public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) => | **NO** | HIGH |
| Harbor.Providers.Ollama/OllamaLlmClient.cs | 330 | class OllamaConfig | **NO** | MED |
| Harbor.Providers.Ollama/OllamaLlmClient.cs | 332 | string? BaseUrl | **NO** | LOW |
| Harbor.Providers.Ollama/OllamaLlmClient.cs | 333 | string KeepAlive | **NO** | LOW |

## Project: Harbor.Providers.OpenAI

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Providers.OpenAI/OpenAILlmClient.cs | 24 | class OpenAILlmClient | YES | HIGH |
| Harbor.Providers.OpenAI/OpenAILlmClient.cs | 55 | ProviderId ProviderId | **NO** | HIGH |
| Harbor.Providers.OpenAI/OpenAILlmClient.cs | 57 | public async IAsyncEnumerable<LlmEvent> StreamAsync( | **NO** | MED |
| Harbor.Providers.OpenAI/OpenAILlmClient.cs | 130 | public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) | **NO** | HIGH |
| Harbor.Providers.OpenAI/OpenAILlmClient.cs | 430 | class OpenAIConfig | **NO** | MED |
| Harbor.Providers.OpenAI/OpenAILlmClient.cs | 432 | string? BaseUrl | **NO** | LOW |
| Harbor.Providers.OpenAI/OpenAILlmClient.cs | 433 | bool ForceResponsesApi | **NO** | LOW |
| Harbor.Providers.OpenAI/OpenAILlmClient.cs | 436 | class OpenAIModels | **NO** | MED |

## Project: Harbor.Providers.OpenAiCompatible

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 24 | interface IProviderCompatFlag | YES | HIGH |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 27 | ProviderId ProviderId | YES | HIGH |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 35 | public bool IsPropertyOmitted(string propertyName, LlmRequest request); | YES | MED |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 42 | public void Write(Utf8JsonWriter writer, LlmRequest request); | YES | HIGH |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 49 | class DeepSeekReasonerCompatFlag | YES | MED |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 52 | ProviderId ProviderId | YES | HIGH |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 55 | public bool IsPropertyOmitted(string propertyName, LlmRequest request) | YES | MED |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 63 | public void Write(Utf8JsonWriter writer, LlmRequest request) { } | YES | HIGH |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 71 | class GroqMaxTokensCompatFlag | YES | MED |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 74 | ProviderId ProviderId | YES | HIGH |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 77 | public bool IsPropertyOmitted(string propertyName, LlmRequest request) => false; | YES | MED |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 80 | public void Write(Utf8JsonWriter writer, LlmRequest request) | YES | HIGH |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 94 | class ProviderCompatFlags | YES | HIGH |
| Harbor.Providers.OpenAiCompatible/Compat/IProviderCompatFlag.cs | 108 | public static IReadOnlyList<IProviderCompatFlag>? For(ProviderId providerId) | YES | MED |
| Harbor.Providers.OpenAiCompatible/OpenAiCompatibleJsonContext.cs | 13 | class OpenAiCompatibleJsonContext | YES | MED |
| Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs | 12 | class OpenAiCompatibleLlmClient | **NO** | HIGH |
| Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs | 42 | ProviderId ProviderId | **NO** | HIGH |
| Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs | 44 | public async IAsyncEnumerable<LlmEvent> StreamAsync( | **NO** | MED |
| Harbor.Providers.OpenAiCompatible/OpenAiCompatibleLlmClient.cs | 142 | public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) => awa... | **NO** | HIGH |
| Harbor.Providers.OpenAiCompatible/OpenAiSseParser.cs | 15 | class OpenAiSseParser | YES | MED |
| Harbor.Providers.OpenAiCompatible/OpenAiSseParser.cs | 17 | public static IEnumerable<LlmEvent> ParseChunk(ReadOnlySpan<char> data, Dictionary<int, string> indexToId, ILogger logge... | **NO** | HIGH |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 8 | class ProviderConfig | YES | HIGH |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 17 | string Id | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 18 | string DisplayName | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 19 | string Description | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 20 | string BaseUrl | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 21 | string ApiType | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 22 | string? ApiVersion | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 23 | string AuthType | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 24 | string? AuthHeader | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 25 | string? AuthEnvVar | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 26 | string? ModelsUrl | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 27 | int ModelsRefreshHours | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 28 | string? ModelsPath | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 29 | ModelMapping? ModelMapping | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 30 | List<ModelInfo>? Models | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 33 | int Timeout | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 41 | IReadOnlyList<IProviderCompatFlag>? Quirks | YES | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 43 | public ProviderId GetProviderId() => ProviderId.Create(Id); | **NO** | HIGH |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 45 | public static Result<ProviderConfig> LoadFromFile(string path) => | **NO** | HIGH |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 57 | public static Result<ProviderConfig> LoadFromJson(string json) => | YES | HIGH |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 83 | class ModelMapping | **NO** | MED |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 85 | string? Id | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 86 | string? DisplayName | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 87 | string? ContextWindow | **NO** | HIGH |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 88 | string? MaxOutputTokens | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 89 | string? SupportsVision | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 90 | string? SupportsToolUse | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 91 | string? SupportsReasoning | **NO** | LOW |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 107 | class EnvVarAuthResolver | YES | MED |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 118 | public Task<Result<string>> ResolveApiKeyAsync(string providerId, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 138 | interface IModelCatalog | YES | HIGH |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 140 | public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default); | **NO** | HIGH |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 143 | class DynamicModelCatalog | **NO** | MED |
| Harbor.Providers.OpenAiCompatible/ProviderConfig.cs | 158 | public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(ProviderConfig config, CancellationToken ct = default... | **NO** | HIGH |

## Project: Harbor.Providers.Shared

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Providers.Shared/OpenAiWire.cs | 20 | class OpenAiWire | YES | MED |
| Harbor.Providers.Shared/OpenAiWire.cs | 27 | public static IEnumerable<LlmEvent> ParseChatChunk(JsonElement root, Dictionary<int, string> indexToId) | YES | HIGH |
| Harbor.Providers.Shared/OpenAiWire.cs | 102 | public static Usage? ReadUsage(JsonElement root) | YES | HIGH |
| Harbor.Providers.Shared/OpenAiWire.cs | 118 | public static IReadOnlyList<LlmEvent> TryParseChatChunkLine( | YES | HIGH |
| Harbor.Providers.Shared/SsePump.cs | 21 | class ProviderPayload | YES | HIGH |
| Harbor.Providers.Shared/SsePump.cs | 28 | public static string FirstTextOrEmpty(IReadOnlyList<LlmContentBlock> content, ILogger logger, string providerId) | YES | MED |
| Harbor.Providers.Shared/SsePump.cs | 54 | class ChunkStreamState | YES | MED |
| Harbor.Providers.Shared/SsePump.cs | 60 | int MalformedChunks | YES | LOW |
| Harbor.Providers.Shared/SsePump.cs | 63 | public void CountMalformed() => MalformedChunks++; | YES | HIGH |
| Harbor.Providers.Shared/SsePump.cs | 74 | class SsePump | YES | MED |
| Harbor.Providers.Shared/SsePump.cs | 107 | public static async Task RunAsync( | YES | HIGH |
| Harbor.Providers.Shared/SsePump.cs | 212 | public static Task RunSseAsync( | YES | HIGH |

## Project: Harbor.Registries

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Registries/Agents/AgentRegistry.cs | 8 | class AgentRegistry | YES | HIGH |
| Harbor.Registries/Agents/AgentRegistry.cs | 13 | public IReadOnlyList<AgentDefinition> GetAllAgents() | YES | HIGH |
| Harbor.Registries/Agents/AgentRegistry.cs | 31 | public Result<AgentDefinition> GetAgent(AgentName name) | YES | HIGH |
| Harbor.Registries/Agents/AgentRegistry.cs | 40 | public Result Register(AgentDefinition agent) | YES | HIGH |
| Harbor.Registries/Agents/AgentRegistry.cs | 49 | public Result Unregister(AgentName name) | YES | MED |
| Harbor.Registries/Agents/AgentRegistry.cs | 62 | class AgentRegistryBuilder | YES | HIGH |
| Harbor.Registries/Agents/AgentRegistry.cs | 76 | public void AddAgent(AgentDefinition agent) | YES | HIGH |
| Harbor.Registries/Events/InMemoryEventBus.cs | 58 | class InMemoryEventBus | YES | HIGH |
| Harbor.Registries/Events/InMemoryEventBus.cs | 170 | public async Task PublishAsync(AgentEvent @event, CancellationToken ct = default) | YES | HIGH |
| Harbor.Registries/Events/InMemoryEventBus.cs | 340 | public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler) | YES | HIGH |
| Harbor.Registries/Events/InMemoryEventBus.cs | 364 | public IReadOnlyList<AgentEvent> GetScrollback(int maxEvents) | YES | HIGH |
| Harbor.Registries/Events/InMemoryEventBus.cs | 458 | public int Strike() => Interlocked.Increment(ref _slowStrikes); | **NO** | MED |
| Harbor.Registries/Events/InMemoryEventBus.cs | 460 | public void ResetSlowStrikes() => Interlocked.Exchange(ref _slowStrikes, 0); | **NO** | HIGH |
| Harbor.Registries/Events/InMemoryEventBus.cs | 472 | public void Dispose() | **NO** | HIGH |
| Harbor.Registries/Events/SamplingMiddleware.cs | 18 | class SamplingMiddleware | YES | MED |
| Harbor.Registries/Events/SamplingMiddleware.cs | 33 | public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Registries/Events/TypeFilterMiddleware.cs | 22 | class TypeFilterMiddleware | YES | MED |
| Harbor.Registries/Events/TypeFilterMiddleware.cs | 45 | public ValueTask<bool> ProcessAsync(ref AgentEvent @event, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 15 | class ProviderRegistry | YES | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 56 | public IReadOnlyList<ProviderId> GetRegisteredProviderIds() | YES | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 76 | public Result<ILlmClient> GetClient(ProviderId providerId) | YES | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 105 | public async Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancel... | YES | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 123 | public async Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) | YES | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 234 | public void Register(ProviderId providerId, Func<ILlmClient> factory) | YES | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 244 | public Result Unregister(ProviderId providerId) | YES | MED |
| Harbor.Registries/Providers/ProviderRegistry.cs | 262 | public void InvalidateModelCache(ProviderId providerId) | YES | MED |
| Harbor.Registries/Providers/ProviderRegistry.cs | 272 | public void Freeze() | YES | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 303 | class ProviderRegistryBuilder | YES | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 327 | public void AddProvider(Func<ILlmClient> factory) | **NO** | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 346 | public void AddProvider(ProviderId providerId, Func<ILlmClient> factory) => _registry.Register(providerId, factory); | YES | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 358 | public void AddProvider(string providerId, Func<ILlmClient> factory) | YES | HIGH |
| Harbor.Registries/Providers/ProviderRegistry.cs | 373 | public void AddProvider(IProviderFactory factory) | YES | HIGH |
| Harbor.Registries/Tools/CompositeToolRegistry.cs | 6 | class CompositeToolRegistry | **NO** | HIGH |
| Harbor.Registries/Tools/CompositeToolRegistry.cs | 11 | public void AddSource(IToolSource source) | **NO** | HIGH |
| Harbor.Registries/Tools/CompositeToolRegistry.cs | 17 | public IReadOnlyList<ToolDescriptor> GetAllTools() | **NO** | HIGH |
| Harbor.Registries/Tools/CompositeToolRegistry.cs | 53 | public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null) | **NO** | HIGH |
| Harbor.Registries/Tools/CompositeToolRegistry.cs | 77 | public Result<ITool> GetTool(ToolName name) | **NO** | HIGH |
| Harbor.Registries/Tools/CompositeToolRegistry.cs | 99 | public Result Register(ITool tool) => Result.Failure("CompositeToolRegistry is read-only. Use AddSource to add tools."); | **NO** | HIGH |
| Harbor.Registries/Tools/CompositeToolRegistry.cs | 101 | public Result Unregister(ToolName name) => Result.Failure("CompositeToolRegistry is read-only."); | **NO** | MED |
| Harbor.Registries/Tools/CompositeToolRegistry.cs | 103 | public void Freeze() | **NO** | HIGH |
| Harbor.Registries/Tools/InMemoryMcpRegistry.cs | 14 | class InMemoryMcpRegistry | YES | HIGH |
| Harbor.Registries/Tools/InMemoryMcpRegistry.cs | 29 | public Result Register(string name, string stdioCommand) | YES | HIGH |
| Harbor.Registries/Tools/InMemoryMcpRegistry.cs | 44 | public Result Unregister(string name) | YES | MED |
| Harbor.Registries/Tools/InMemoryMcpRegistry.cs | 52 | public IReadOnlyList<string> GetServerNames() | YES | HIGH |
| Harbor.Registries/Tools/InMemoryMcpRegistry.cs | 64 | public IReadOnlyList<McpServerInstructions> GetInstructions() => | YES | HIGH |
| Harbor.Registries/Tools/InMemoryMcpRegistry.cs | 68 | public Task<Result<string>> InvokeAsync( | YES | HIGH |
| Harbor.Registries/Tools/ToolRegistry.cs | 12 | class ToolRegistry | YES | HIGH |
| Harbor.Registries/Tools/ToolRegistry.cs | 33 | public IReadOnlyList<ToolDescriptor> GetAllTools() | YES | HIGH |
| Harbor.Registries/Tools/ToolRegistry.cs | 64 | public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null) | YES | HIGH |
| Harbor.Registries/Tools/ToolRegistry.cs | 99 | public Result<ITool> GetTool(ToolName name) | YES | HIGH |
| Harbor.Registries/Tools/ToolRegistry.cs | 118 | public Result Register(ITool tool) | YES | HIGH |
| Harbor.Registries/Tools/ToolRegistry.cs | 130 | public Result Unregister(ToolName name) | YES | MED |
| Harbor.Registries/Tools/ToolRegistry.cs | 172 | public void Freeze() | YES | HIGH |
| Harbor.Registries/Tools/ToolRegistry.cs | 204 | class ToolRegistryBuilder | YES | HIGH |
| Harbor.Registries/Tools/ToolRegistry.cs | 221 | public void AddTool(ITool tool) | YES | HIGH |
| Harbor.Registries/Tools/ToolRegistry.cs | 234 | public void AddTool(Func<ITool> factory) => AddTool(factory()); | YES | HIGH |
| Harbor.Registries/Tools/ToolRegistry.cs | 240 | public void AddTool(IToolFactory factory) => AddTool(factory.CreateTool(_loggerFactory)); | YES | HIGH |
| Harbor.Registries/Tools/ToolRegistry.cs | 243 | public void AddTool(Func<ILoggerFactory, ITool> factory) => AddTool(factory(_loggerFactory)); | YES | HIGH |

## Project: Harbor.Storage.Jsonl

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Storage.Jsonl/JsonlCodecContext.cs | 37 | class JsonlCodecContext | YES | MED |
| Harbor.Storage.Jsonl/JsonlCodecContext.cs | 56 | record UserPayload | **NO** | LOW |
| Harbor.Storage.Jsonl/JsonlCodecContext.cs | 61 | record AssistantPayload | **NO** | LOW |
| Harbor.Storage.Jsonl/JsonlCodecContext.cs | 69 | record ToolResultPayload | **NO** | LOW |
| Harbor.Storage.Jsonl/JsonlCodecContext.cs | 72 | record TextPartPayload | **NO** | LOW |
| Harbor.Storage.Jsonl/JsonlCodecContext.cs | 76 | record ThinkingPartPayload | **NO** | LOW |
| Harbor.Storage.Jsonl/JsonlCodecContext.cs | 80 | record ToolCallPartPayload | **NO** | LOW |
| Harbor.Storage.Jsonl/JsonlCodecContext.cs | 86 | record FilePartPayload | **NO** | LOW |
| Harbor.Storage.Jsonl/JsonlCodecContext.cs | 92 | record UnknownPartPayload | **NO** | LOW |
| Harbor.Storage.Jsonl/JsonlMessageCodec.cs | 29 | class JsonlMessageCodec | YES | MED |
| Harbor.Storage.Jsonl/JsonlMessageCodec.cs | 44 | public static object SerializeMessagePayload(AgentMessage message) | YES | HIGH |
| Harbor.Storage.Jsonl/JsonlMessageCodec.cs | 65 | public static object SerializePart(ContentPart part) => part switch | YES | HIGH |
| Harbor.Storage.Jsonl/JsonlMessageCodec.cs | 88 | public static Result<AgentMessage> DeserializeMessage(string sessionId, JsonElement element) | YES | HIGH |
| Harbor.Storage.Jsonl/JsonlMessageCodec.cs | 215 | public static ContentPart? DeserializePart(JsonElement element) | YES | HIGH |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 30 | class JsonlSessionStore | YES | HIGH |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 93 | public Task<Result<Session>> CreateAsync( | YES | HIGH |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 146 | public async Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) | YES | HIGH |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 177 | public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) | **NO** | MED |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 210 | public async Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) | YES | MED |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 258 | public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) | YES | MED |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 350 | public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default... | YES | HIGH |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 398 | public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) | YES | HIGH |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 430 | public async Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) | YES | HIGH |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 469 | public async Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) | **NO** | MED |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 482 | public async Task<Result> UpdateAsync(Session session, CancellationToken ct = default) | YES | MED |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 941 | record SessionCacheEntry | YES | LOW |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 945 | record SessionHeaderEntry | **NO** | LOW |
| Harbor.Storage.Jsonl/JsonlSessionStore.cs | 965 | record MessageEntry | **NO** | LOW |
| Harbor.Storage.Jsonl/SessionPorter.cs | 27 | class JsonlSessionPorter | YES | MED |
| Harbor.Storage.Jsonl/SessionPorter.cs | 39 | public async Task<Result> ExportAsync( | YES | MED |
| Harbor.Storage.Jsonl/SessionPorter.cs | 83 | public async Task<Result<string>> ImportAsync( | YES | MED |
| Harbor.Storage.Jsonl/SessionPorter.cs | 190 | record ExportEnvelope | YES | LOW |

## Project: Harbor.Storage.Memory

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Storage.Memory/MemorySessionStore.cs | 11 | class MemorySessionStore | YES | HIGH |
| Harbor.Storage.Memory/MemorySessionStore.cs | 16 | public Task<Result<Session>> CreateAsync( | **NO** | HIGH |
| Harbor.Storage.Memory/MemorySessionStore.cs | 26 | public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Storage.Memory/MemorySessionStore.cs | 33 | public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) | **NO** | MED |
| Harbor.Storage.Memory/MemorySessionStore.cs | 42 | public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) | **NO** | MED |
| Harbor.Storage.Memory/MemorySessionStore.cs | 60 | public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) | **NO** | MED |
| Harbor.Storage.Memory/MemorySessionStore.cs | 76 | public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Storage.Memory/MemorySessionStore.cs | 88 | public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Storage.Memory/MemorySessionStore.cs | 95 | public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Storage.Memory/MemorySessionStore.cs | 102 | public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) | **NO** | MED |
| Harbor.Storage.Memory/MemorySessionStore.cs | 112 | public Task<Result> UpdateAsync(Session session, CancellationToken ct = default) | **NO** | MED |
| Harbor.Storage.Memory/MemorySessionStore.cs | 122 | public void Clear() | **NO** | HIGH |

## Project: Harbor.Storage.Sqlite

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Storage.Sqlite/SqliteSessionStore.cs | 16 | class SqliteSessionStore | YES | HIGH |
| Harbor.Storage.Sqlite/SqliteSessionStore.cs | 65 | public Task<Result<Session>> CreateAsync( | **NO** | HIGH |
| Harbor.Storage.Sqlite/SqliteSessionStore.cs | 100 | public async Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Storage.Sqlite/SqliteSessionStore.cs | 130 | public async Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) | **NO** | MED |
| Harbor.Storage.Sqlite/SqliteSessionStore.cs | 158 | public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) | **NO** | MED |
| Harbor.Storage.Sqlite/SqliteSessionStore.cs | 196 | public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) | **NO** | MED |
| Harbor.Storage.Sqlite/SqliteSessionStore.cs | 219 | public async Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default... | **NO** | HIGH |
| Harbor.Storage.Sqlite/SqliteSessionStore.cs | 244 | public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Storage.Sqlite/SqliteSessionStore.cs | 259 | public async Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Storage.Sqlite/SqliteSessionStore.cs | 278 | public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) | **NO** | MED |
| Harbor.Storage.Sqlite/SqliteSessionStore.cs | 294 | public Task<Result> UpdateAsync(Session session, CancellationToken ct = default) | **NO** | MED |

## Project: Harbor.Telemetry.Core

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Telemetry.Core/ActivityTracer.cs | 10 | class ActivityTracer | YES | MED |
| Harbor.Telemetry.Core/ActivityTracer.cs | 18 | public ITelemetrySpan? StartSpan(string name, params KeyValuePair<string, object?>[] tags) | **NO** | HIGH |
| Harbor.Telemetry.Core/ActivityTracer.cs | 57 | public void SetTag(string key, object? value) | **NO** | HIGH |
| Harbor.Telemetry.Core/ActivityTracer.cs | 65 | public void SetError(string? description = null) | **NO** | HIGH |
| Harbor.Telemetry.Core/ActivityTracer.cs | 75 | public void Dispose() => activity.Dispose(); | **NO** | HIGH |
| Harbor.Telemetry.Core/ActivityTracer.cs | 80 | class TelemetryTagNames | YES | MED |
| Harbor.Telemetry.Core/HarborTelemetrySources.cs | 13 | class HarborTelemetrySources | YES | MED |
| Harbor.Telemetry.Core/InstrumentedLlmClient.cs | 17 | class InstrumentedProviderRegistry | YES | HIGH |
| Harbor.Telemetry.Core/InstrumentedLlmClient.cs | 20 | public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => inner.GetRegisteredProviderIds(); | **NO** | HIGH |
| Harbor.Telemetry.Core/InstrumentedLlmClient.cs | 22 | public Result<ILlmClient> GetClient(ProviderId providerId) | **NO** | HIGH |
| Harbor.Telemetry.Core/InstrumentedLlmClient.cs | 30 | public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) | **NO** | HIGH |
| Harbor.Telemetry.Core/InstrumentedLlmClient.cs | 35 | public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(ProviderId providerId, CancellationToken cancellation... | **NO** | HIGH |
| Harbor.Telemetry.Core/InstrumentedLlmClient.cs | 42 | public void Register(ProviderId providerId, Func<ILlmClient> factory) => | **NO** | HIGH |
| Harbor.Telemetry.Core/InstrumentedLlmClient.cs | 45 | public Result Unregister(ProviderId providerId) => inner.Unregister(providerId); | **NO** | MED |
| Harbor.Telemetry.Core/InstrumentedLlmClient.cs | 53 | class InstrumentedLlmClient | YES | HIGH |
| Harbor.Telemetry.Core/InstrumentedLlmClient.cs | 61 | public async IAsyncEnumerable<LlmEvent> StreamAsync( | **NO** | MED |
| Harbor.Telemetry.Core/InstrumentedLlmClient.cs | 138 | public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsAsync(CancellationToken cancellationToken = default) | **NO** | HIGH |
| Harbor.Telemetry.Core/InstrumentedToolRegistry.cs | 17 | class InstrumentedToolRegistry | YES | HIGH |
| Harbor.Telemetry.Core/InstrumentedToolRegistry.cs | 19 | public IReadOnlyList<ToolDescriptor> GetAllTools() => inner.GetAllTools(); | **NO** | HIGH |
| Harbor.Telemetry.Core/InstrumentedToolRegistry.cs | 21 | public IReadOnlyList<ToolDescriptor> ResolveTools(string agentName, PermissionRuleset? sessionPermission = null) | **NO** | HIGH |
| Harbor.Telemetry.Core/InstrumentedToolRegistry.cs | 24 | public Result<ITool> GetTool(ToolName name) | **NO** | HIGH |
| Harbor.Telemetry.Core/InstrumentedToolRegistry.cs | 32 | public Result Register(ITool tool) => | **NO** | HIGH |
| Harbor.Telemetry.Core/InstrumentedToolRegistry.cs | 35 | public Result Unregister(ToolName name) => inner.Unregister(name); | **NO** | MED |
| Harbor.Telemetry.Core/InstrumentedToolRegistry.cs | 42 | class TelemetryToolDecorator | YES | HIGH |
| Harbor.Telemetry.Core/InstrumentedToolRegistry.cs | 58 | public Result ValidateArguments(JsonElement args) => inner.ValidateArguments(args); | **NO** | HIGH |
| Harbor.Telemetry.Core/InstrumentedToolRegistry.cs | 60 | public async Task<ToolResult> ExecuteAsync( | **NO** | HIGH |
| Harbor.Telemetry.Core/MeterMetrics.cs | 12 | class MeterMetrics | YES | MED |
| Harbor.Telemetry.Core/MeterMetrics.cs | 19 | public void Counter(string name, double value = 1, params KeyValuePair<string, object?>[] tags) | **NO** | HIGH |
| Harbor.Telemetry.Core/MeterMetrics.cs | 24 | public void Histogram(string name, double value, params KeyValuePair<string, object?>[] tags) | **NO** | MED |
| Harbor.Telemetry.Core/TracingAgentProxy.cs | 19 | class TracingAgentProxy | YES | HIGH |
| Harbor.Telemetry.Core/TracingAgentProxy.cs | 27 | public void ResetAbortSource() => inner.ResetAbortSource(); | **NO** | HIGH |
| Harbor.Telemetry.Core/TracingAgentProxy.cs | 29 | public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener) => | **NO** | HIGH |
| Harbor.Telemetry.Core/TracingAgentProxy.cs | 32 | public void Initialize(Session session, AgentDefinition agent) => inner.Initialize(session, agent); | **NO** | HIGH |
| Harbor.Telemetry.Core/TracingAgentProxy.cs | 34 | public void Steer(AgentMessage message) => inner.Steer(message); | **NO** | HIGH |
| Harbor.Telemetry.Core/TracingAgentProxy.cs | 36 | public async Task<Result> PromptAsync(string text, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Telemetry.Core/TracingAgentProxy.cs | 39 | public async Task<Result> PromptAsync(UserMessage message, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Telemetry.Core/TracingAgentProxy.cs | 42 | public Task WaitForIdleAsync(CancellationToken ct = default) => inner.WaitForIdleAsync(ct); | **NO** | HIGH |
| Harbor.Telemetry.Core/TracingAgentProxy.cs | 44 | public void Dispose() => inner.Dispose(); | **NO** | HIGH |

## Project: Harbor.Telemetry.Otlp

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Telemetry.Otlp/HarborOtlpExporter.cs | 18 | class HarborOtlpExporter | YES | MED |
| Harbor.Telemetry.Otlp/HarborOtlpExporter.cs | 25 | public static IDisposable Attach(string? endpoint = null) | YES | MED |
| Harbor.Telemetry.Otlp/HarborOtlpExporter.cs | 51 | public void Dispose() | **NO** | HIGH |

## Project: Harbor.Terminal.Abstractions

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Terminal.Abstractions/BaseTuiRenderer.cs | 41 | class BaseTuiRenderer | YES | HIGH |
| Harbor.Terminal.Abstractions/BaseTuiRenderer.cs | 62 | ViewRegistry Views | **NO** | LOW |
| Harbor.Terminal.Abstractions/BaseTuiRenderer.cs | 63 | ViewModelRegistry ViewModels | **NO** | LOW |
| Harbor.Terminal.Abstractions/BaseTuiRenderer.cs | 64 | ITuiRenderContext Context | **NO** | HIGH |
| Harbor.Terminal.Abstractions/BaseTuiRenderer.cs | 72 | public virtual Task<Result> InitializeAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Terminal.Abstractions/BaseTuiRenderer.cs | 84 | public virtual async Task RenderAsync(AgentEvent @event, CancellationToken ct = default) | YES | HIGH |
| Harbor.Terminal.Abstractions/BaseTuiRenderer.cs | 138 | public abstract Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default); | **NO** | HIGH |
| Harbor.Terminal.Abstractions/BaseTuiRenderer.cs | 139 | public abstract Task<Result> WriteAsync(string text, CancellationToken ct = default); | **NO** | HIGH |
| Harbor.Terminal.Abstractions/BaseTuiRenderer.cs | 140 | public abstract Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default); | **NO** | HIGH |
| Harbor.Terminal.Abstractions/BaseTuiRenderer.cs | 141 | public abstract Task<Result> ClearAsync(CancellationToken ct = default); | **NO** | HIGH |
| Harbor.Terminal.Abstractions/BaseTuiRenderer.cs | 143 | public virtual void Dispose() { } | **NO** | HIGH |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 11 | interface ITuiRenderer | YES | HIGH |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 14 | ITuiRenderContext Context | YES | HIGH |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 17 | ViewRegistry Views | YES | LOW |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 20 | ViewModelRegistry ViewModels | YES | LOW |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 23 | public Task<Result> InitializeAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 26 | public Task RenderAsync(AgentEvent @event, CancellationToken ct = default); | YES | HIGH |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 29 | public Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default); | YES | HIGH |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 32 | public Task<Result> WriteAsync(string text, CancellationToken ct = default); | YES | HIGH |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 35 | public Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default); | YES | HIGH |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 38 | public Task<Result> ClearAsync(CancellationToken ct = default); | YES | HIGH |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 49 | public Task<int> RunInteractiveAsync(IAgent agent, IServiceProvider host, CancellationToken ct = default) | YES | HIGH |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 58 | interface IInteractiveTuiRenderer | YES | HIGH |
| Harbor.Terminal.Abstractions/ITuiRenderer.cs | 65 | public void SetSlashHandler(Func<string, Task> handler); | YES | HIGH |
| Harbor.Terminal.Abstractions/Plugins/ITuiPlugin.cs | 59 | interface ITuiPlugin | YES | HIGH |
| Harbor.Terminal.Abstractions/Plugins/ITuiPlugin.cs | 62 | string Name | YES | LOW |
| Harbor.Terminal.Abstractions/Plugins/ITuiPlugin.cs | 65 | Version Version | YES | LOW |
| Harbor.Terminal.Abstractions/Plugins/ITuiPlugin.cs | 68 | string Description | YES | LOW |
| Harbor.Terminal.Abstractions/Plugins/ITuiPlugin.cs | 82 | public void RegisterTui(ViewRegistry views, ViewModelRegistry viewModels); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 15 | interface ITuiRenderContext | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 18 | int Width | YES | LOW |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 21 | int Height | YES | LOW |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 24 | bool SupportsColor | YES | LOW |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 28 | public void Write(string text); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 32 | public void WriteLine(string? text = null); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 38 | public void WriteColored(string text, TuiColor foreground, TuiColor? background = null); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 43 | public void WriteStyled(string text, TuiStyle style); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 48 | public void SetCursorPosition(int row, int col); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 51 | public void ClearLine(); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 54 | public void Clear(); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 57 | public void HideCursor(); | YES | MED |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 60 | public void ShowCursor(); | YES | MED |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 63 | public void EnterAlternateScreen(); | YES | MED |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 66 | public void ExitAlternateScreen(); | YES | MED |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 69 | public void Flush(); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 78 | record struct | YES | LOW |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 120 | public static TuiColor FromRgb(int r, int g, int b) => new((byte)r, (byte)g, (byte)b); | YES | MED |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 123 | public override string ToString() => $"#{R:X2}{G:X2}{B:X2}"; | YES | MED |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 130 | enum TuiStyle | YES | LOW |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 157 | class CaptureRenderContext | YES | MED |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 176 | public void Write(string text) => _sb.Append(text); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 179 | public void WriteLine(string? text = null) => _sb.AppendLine(text ?? string.Empty); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 182 | public void WriteColored(string text, TuiColor foreground, TuiColor? background = null) => _sb.Append(text); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 185 | public void WriteStyled(string text, TuiStyle style) => _sb.Append(text); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 188 | public void SetCursorPosition(int row, int col) { } | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 191 | public void ClearLine() { } | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 194 | public void Clear() => _sb.Clear(); | YES | HIGH |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 197 | public void HideCursor() { } | YES | MED |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 200 | public void ShowCursor() { } | YES | MED |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 203 | public void EnterAlternateScreen() { } | YES | MED |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 206 | public void ExitAlternateScreen() { } | YES | MED |
| Harbor.Terminal.Abstractions/Renderers/ITuiRenderContext.cs | 209 | public void Flush() { } | YES | HIGH |
| Harbor.Terminal.Abstractions/Rendering/GfmTable.cs | 6 | enum GfmAlign | YES | LOW |
| Harbor.Terminal.Abstractions/Rendering/GfmTable.cs | 20 | record GfmTable | YES | LOW |
| Harbor.Terminal.Abstractions/Rendering/GfmTableFormatter.cs | 17 | class GfmTableFormatter | YES | MED |
| Harbor.Terminal.Abstractions/Rendering/GfmTableFormatter.cs | 35 | public static IReadOnlyList<string> Format(GfmTable table, int maxWidth = 0) | **NO** | HIGH |
| Harbor.Terminal.Abstractions/Rendering/GfmTableParser.cs | 8 | class GfmTableParser | YES | MED |
| Harbor.Terminal.Abstractions/Rendering/GfmTableParser.cs | 14 | public static bool IsTableStart(IReadOnlyList<string> lines, int index) | YES | HIGH |
| Harbor.Terminal.Abstractions/Rendering/GfmTableParser.cs | 26 | public static bool TryParse( | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewModels/ITuiViewModel.cs | 20 | interface ITuiViewModel | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewModels/ITuiViewModel.cs | 26 | string Id | YES | LOW |
| Harbor.Terminal.Abstractions/ViewModels/ITuiViewModel.cs | 33 | public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default); | YES | MED |
| Harbor.Terminal.Abstractions/ViewModels/ITuiViewModel.cs | 41 | class BindsToViewAttribute | YES | MED |
| Harbor.Terminal.Abstractions/ViewModels/ITuiViewModel.cs | 55 | string ViewId | YES | LOW |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 11 | class StatusBarViewModel | YES | MED |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 62 | public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default) | YES | MED |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 95 | public void SetModel(ModelInfo? model) => ContextWindow = model?.ContextWindow ?? 0; | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 114 | class ChatHistoryViewModel | YES | MED |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 140 | public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default) | YES | MED |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 195 | public void AddEntry(ChatEntry entry) => _entries.Add(entry); | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 200 | public void Clear() | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 219 | class InputViewModel | YES | MED |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 237 | public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask; | YES | MED |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 263 | class DiffPreviewViewModel | YES | MED |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 286 | public Task UpdateFromEventAsync(AgentEvent @event, CancellationToken ct = default) | YES | MED |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 303 | public void AddDiff(DiffEntry entry) | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 337 | record ChatEntry | YES | LOW |
| Harbor.Terminal.Abstractions/ViewModels/TuiViewModels.cs | 345 | record DiffEntry | YES | LOW |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 10 | class ViewRegistry | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 28 | public void Register(ITuiView view) | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 52 | public bool Unregister(string viewId) | YES | MED |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 66 | public ITuiView? Get(string viewId) | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 77 | public IReadOnlyList<ITuiView> GetByPlacement(TuiViewPlacement placement) | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 88 | public IReadOnlyList<ITuiView> GetAll() | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 97 | public void Freeze() | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 110 | class ViewModelRegistry | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 119 | public void Register(ITuiViewModel viewModel) | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 132 | public bool Unregister(string id) | YES | MED |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 159 | public ITuiViewModel? Get(string id) | YES | HIGH |
| Harbor.Terminal.Abstractions/ViewRegistry.cs | 171 | public IReadOnlyList<ITuiViewModel> GetAll() | YES | HIGH |
| Harbor.Terminal.Abstractions/Views/ChatHistoryView.cs | 20 | class ChatHistoryView | YES | MED |
| Harbor.Terminal.Abstractions/Views/ChatHistoryView.cs | 32 | public override Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default) | YES | HIGH |
| Harbor.Terminal.Abstractions/Views/DiffPreviewView.cs | 19 | class DiffPreviewView | YES | MED |
| Harbor.Terminal.Abstractions/Views/DiffPreviewView.cs | 31 | public override Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default) | YES | HIGH |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 11 | interface ITuiView | YES | HIGH |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 14 | string Id | YES | LOW |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 17 | string DisplayName | YES | LOW |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 20 | TuiViewPlacement Placement | YES | LOW |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 23 | ITuiViewModel? ViewModel | YES | LOW |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 26 | public Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default); | YES | HIGH |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 29 | public bool HandleKey(KeyPress key) => false; | YES | HIGH |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 32 | public Task OnEventAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask; | YES | MED |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 38 | class TuiViewBase | YES | HIGH |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 42 | TViewModel? ViewModel | **NO** | LOW |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 43 | string Id | **NO** | LOW |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 44 | string DisplayName | **NO** | LOW |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 45 | TuiViewPlacement Placement | **NO** | LOW |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 52 | public abstract Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default); | **NO** | HIGH |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 54 | public virtual bool HandleKey(KeyPress key) => false; | **NO** | HIGH |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 55 | public virtual Task OnEventAsync(AgentEvent @event, CancellationToken ct = default) => Task.CompletedTask; | **NO** | MED |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 57 | public virtual void Dispose() { } | **NO** | HIGH |
| Harbor.Terminal.Abstractions/Views/ITuiView.cs | 63 | enum TuiViewPlacement | YES | LOW |
| Harbor.Terminal.Abstractions/Views/InputView.cs | 19 | class InputView | YES | MED |
| Harbor.Terminal.Abstractions/Views/InputView.cs | 33 | public override Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default) | YES | HIGH |
| Harbor.Terminal.Abstractions/Views/StatusBarView.cs | 22 | class StatusBarView | YES | MED |
| Harbor.Terminal.Abstractions/Views/StatusBarView.cs | 34 | public override Task RenderAsync(ITuiRenderContext context, CancellationToken ct = default) | YES | HIGH |

## Project: Harbor.Tools.Builtin

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Tools.Builtin/FacadeMarker.cs | 11 | class FacadeMarker | YES | MED |
| Harbor.Tools.Builtin/Tools/Bash/BashTool.cs | 9 | class BashTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Bash/BashTool.cs | 20 | IReadOnlyList<string> PromptGuidelines | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Bash/BashTool.cs | 27 | JsonDocument ParameterSchema | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Bash/BashTool.cs | 40 | public Result ValidateArguments(JsonElement args) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Bash/BashTool.cs | 49 | public async Task<ToolResult> ExecuteAsync( | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Edit/EditTool.cs | 10 | class EditTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Edit/EditTool.cs | 28 | IReadOnlyList<string> PromptGuidelines | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Edit/EditTool.cs | 36 | JsonDocument ParameterSchema | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Edit/EditTool.cs | 62 | public Result ValidateArguments(JsonElement args) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Edit/EditTool.cs | 98 | public async Task<ToolResult> ExecuteAsync( | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Edit/EditTool.cs | 356 | public static EditResult Success(string text, int count) => new(true, text, count, null); | **NO** | MED |
| Harbor.Tools.Builtin/Tools/Edit/EditTool.cs | 357 | public static EditResult Fail(string error) => new(false, string.Empty, 0, error); | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Glob/GlobTool.cs | 11 | class GlobTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Glob/GlobTool.cs | 37 | IReadOnlyList<string> PromptGuidelines | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Glob/GlobTool.cs | 45 | JsonDocument ParameterSchema | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Glob/GlobTool.cs | 61 | public Result ValidateArguments(JsonElement args) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Glob/GlobTool.cs | 70 | public Task<ToolResult> ExecuteAsync( | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Grep/GrepTool.cs | 12 | class GrepTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Grep/GrepTool.cs | 48 | IReadOnlyList<string> PromptGuidelines | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Grep/GrepTool.cs | 55 | JsonDocument ParameterSchema | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Grep/GrepTool.cs | 79 | public Result ValidateArguments(JsonElement args) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Grep/GrepTool.cs | 88 | public Task<ToolResult> ExecuteAsync( | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Ls/LsTool.cs | 9 | class LsTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Ls/LsTool.cs | 35 | IReadOnlyList<string> PromptGuidelines | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Ls/LsTool.cs | 43 | JsonDocument ParameterSchema | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Ls/LsTool.cs | 56 | public Result ValidateArguments(JsonElement args) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Ls/LsTool.cs | 69 | public Task<ToolResult> ExecuteAsync( | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Ls/LsTool.cs | 289 | int Count | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Ls/LsTool.cs | 290 | bool Truncated | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Ls/LsTool.cs | 292 | public bool TryAdd() | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpArgvParser.cs | 24 | class McpArgvParser | YES | MED |
| Harbor.Tools.Builtin/Tools/Mcp/McpJsonRpcTransport.cs | 7 | class McpJsonRpcTransport | **NO** | MED |
| Harbor.Tools.Builtin/Tools/Mcp/McpJsonRpcTransport.cs | 25 | public async Task WriteAsync(JsonElement message, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpJsonRpcTransport.cs | 31 | public async Task<JsonDocument?> ReadAsync(CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpJsonRpcTransport.cs | 42 | public ValueTask DisposeAsync() | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpJsonSerializerContext.cs | 7 | class McpJsonSerializerContext | **NO** | MED |
| Harbor.Tools.Builtin/Tools/Mcp/McpProcessClient.cs | 12 | class McpProcessClient | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpProcessClient.cs | 44 | public async Task WaitForExitAsync(CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpProcessClient.cs | 47 | public void Kill() | **NO** | MED |
| Harbor.Tools.Builtin/Tools/Mcp/McpProcessClient.cs | 76 | public async ValueTask DisposeAsync() | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpProcessClient.cs | 103 | public void DisposeSync() | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 11 | class McpRegistry | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 28 | public Result Register(string name, string stdioCommand) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 44 | public Result Register(string name, McpServerStartInfo startInfo) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 74 | public Result RegisterFromConfig(string path) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 194 | public Result Unregister(string name) | **NO** | MED |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 205 | public IReadOnlyList<string> GetServerNames() => _servers.Keys.ToArray(); | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 208 | public IReadOnlyList<McpServerInstructions> GetInstructions() | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 234 | public async Task<Result<string>> InvokeAsync(string server, string method, JsonElement args, CancellationToken cancella... | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 285 | public async ValueTask DisposeAsync() | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 307 | public void SetInstructions(string? instructions) => _instructions = instructions; | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 310 | public bool TrySetInstructions(string? instructions) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 321 | public McpProcessClient? GetProcess() | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 358 | public ValueTask DisposeAsync() | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs | 365 | public void DisposeSync() => _process?.DisposeSync(); | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpServerStartInfo.cs | 7 | class McpServerStartInfo | YES | MED |
| Harbor.Tools.Builtin/Tools/Mcp/McpServerStartInfo.cs | 10 | IReadOnlyList<string> Args | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpServerStartInfo.cs | 11 | string? WorkingDirectory | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfig.cs | 9 | class McpServerConfig | YES | MED |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfig.cs | 12 | string? Command | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfig.cs | 15 | IReadOnlyList<string>? Args | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfig.cs | 18 | string? Cwd | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfig.cs | 24 | bool? Disabled | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfig.cs | 30 | class McpServersConfig | YES | MED |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfigLoader.cs | 8 | record McpServerEntry | YES | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfigLoader.cs | 15 | class McpServersConfigLoader | YES | MED |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfigLoader.cs | 17 | string ProjectRoot | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfigLoader.cs | 18 | string Home | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfigLoader.cs | 19 | string HarborHome | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfigLoader.cs | 32 | public IReadOnlyList<McpServerEntry> Load(params string[] paths) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfigLoader.cs | 61 | public IReadOnlyList<McpServerEntry> LoadFromJson(string json) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpServersConfigLoader.cs | 64 | public string Expand(string? value) | **NO** | MED |
| Harbor.Tools.Builtin/Tools/Mcp/McpToolAdapter.cs | 5 | class McpToolAdapter | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpToolAdapter.cs | 25 | IReadOnlyList<string> PromptGuidelines | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpToolAdapter.cs | 26 | JsonDocument ParameterSchema | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpToolAdapter.cs | 35 | public Result ValidateArguments(JsonElement args) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpToolAdapter.cs | 42 | public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken =... | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpToolTool.cs | 11 | class McpToolTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpToolTool.cs | 56 | IReadOnlyList<string> PromptGuidelines | YES | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpToolTool.cs | 65 | JsonDocument ParameterSchema | YES | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/McpToolTool.cs | 78 | public Result ValidateArguments(JsonElement args) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/McpToolTool.cs | 97 | public async Task<ToolResult> ExecuteAsync( | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 15 | class ProcessTree | YES | MED |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 19 | public static void KillTree(Process process, SafeJobHandle? job) | **NO** | MED |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 48 | public static void PromoteToGroupLeader(Process process) | **NO** | MED |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 54 | public static SafeJobHandle? KillOnCloseJob(Process process) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 98 | internal static extern bool CloseHandle(IntPtr hObject); | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 109 | long PerProcessUserTimeLimit | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 110 | long PerJobUserTimeLimit | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 111 | uint LimitFlags | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 112 | UIntPtr MinimumWorkingSetSize | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 113 | UIntPtr MaximumWorkingSetSize | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 114 | uint ActiveProcessLimit | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 115 | UIntPtr Affinity | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 116 | uint PriorityClass | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 117 | uint SchedulingClass | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 123 | ulong ReadOperationCount | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 124 | ulong WriteOperationCount | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 125 | ulong OtherOperationCount | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 126 | ulong ReadTransferCount | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 127 | ulong WriteTransferCount | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 128 | ulong OtherTransferCount | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 134 | JobObjectBasicLimitInformation BasicLimitInformation | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 135 | IoCounters IoInfo | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 136 | UIntPtr ProcessMemoryLimit | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 137 | UIntPtr JobMemoryLimit | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 138 | UIntPtr PeakProcessMemoryUsed | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 139 | UIntPtr PeakJobMemoryUsed | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Mcp/ProcessTree.cs | 154 | class SafeJobHandle | YES | MED |
| Harbor.Tools.Builtin/Tools/Notebook/NotebookTool.cs | 13 | class NotebookTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Notebook/NotebookTool.cs | 61 | IReadOnlyList<string> PromptGuidelines | YES | LOW |
| Harbor.Tools.Builtin/Tools/Notebook/NotebookTool.cs | 71 | JsonDocument ParameterSchema | YES | LOW |
| Harbor.Tools.Builtin/Tools/Notebook/NotebookTool.cs | 101 | public Result ValidateArguments(JsonElement args) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Notebook/NotebookTool.cs | 135 | public async Task<ToolResult> ExecuteAsync( | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Patch/PatchTool.cs | 13 | class PatchTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Patch/PatchTool.cs | 45 | IReadOnlyList<string> PromptGuidelines | YES | LOW |
| Harbor.Tools.Builtin/Tools/Patch/PatchTool.cs | 54 | JsonDocument ParameterSchema | YES | LOW |
| Harbor.Tools.Builtin/Tools/Patch/PatchTool.cs | 66 | public Result ValidateArguments(JsonElement args) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Patch/PatchTool.cs | 82 | public async Task<ToolResult> ExecuteAsync( | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Patch/PatchTool.cs | 184 | bool ProducedNoChanges | YES | LOW |
| Harbor.Tools.Builtin/Tools/Patch/PatchTool.cs | 191 | long TrailingArtifactLfBytes | YES | LOW |
| Harbor.Tools.Builtin/Tools/Read/ReadTool.cs | 11 | class ReadTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Read/ReadTool.cs | 31 | IReadOnlyList<string> PromptGuidelines | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Read/ReadTool.cs | 39 | JsonDocument ParameterSchema | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Read/ReadTool.cs | 51 | public Result ValidateArguments(JsonElement args) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Read/ReadTool.cs | 69 | public async Task<ToolResult> ExecuteAsync( | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/RipGrep/RipGrepTool.cs | 11 | class RipGrepTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/RipGrep/RipGrepTool.cs | 47 | IReadOnlyList<string> PromptGuidelines | YES | LOW |
| Harbor.Tools.Builtin/Tools/RipGrep/RipGrepTool.cs | 56 | JsonDocument ParameterSchema | YES | LOW |
| Harbor.Tools.Builtin/Tools/RipGrep/RipGrepTool.cs | 72 | public Result ValidateArguments(JsonElement args) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/RipGrep/RipGrepTool.cs | 83 | public async Task<ToolResult> ExecuteAsync( | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Task/TaskTool.cs | 21 | class TaskTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Task/TaskTool.cs | 46 | IReadOnlyList<string> PromptGuidelines | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Task/TaskTool.cs | 53 | JsonDocument ParameterSchema | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Task/TaskTool.cs | 70 | public Result ValidateArguments(JsonElement args) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Task/TaskTool.cs | 84 | public async Task<ToolResult> ExecuteAsync( | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 14 | class TreeTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 56 | IReadOnlyList<string> PromptGuidelines | YES | LOW |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 65 | JsonDocument ParameterSchema | YES | LOW |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 79 | public Result ValidateArguments(JsonElement args) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 95 | public Task<ToolResult> ExecuteAsync( | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 294 | int Count | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 295 | int Dirs | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 296 | int Files | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 297 | bool Truncated | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 299 | public bool TryAdd() | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 310 | public void DirAdded() => Dirs++; | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Tree/TreeTool.cs | 311 | public void FileAdded() => Files++; | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/WebFetch/WebFetchTool.cs | 42 | class WebFetchTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/WebFetch/WebFetchTool.cs | 111 | ICollection<string> AllowedHosts | YES | LOW |
| Harbor.Tools.Builtin/Tools/WebFetch/WebFetchTool.cs | 132 | IReadOnlyList<string> PromptGuidelines | YES | LOW |
| Harbor.Tools.Builtin/Tools/WebFetch/WebFetchTool.cs | 141 | JsonDocument ParameterSchema | YES | LOW |
| Harbor.Tools.Builtin/Tools/WebFetch/WebFetchTool.cs | 154 | public Result ValidateArguments(JsonElement args) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/WebFetch/WebFetchTool.cs | 176 | public async Task<ToolResult> ExecuteAsync( | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Write/SymlinkGuard.cs | 20 | class SymlinkGuard | YES | MED |
| Harbor.Tools.Builtin/Tools/Write/SymlinkGuard.cs | 28 | public static Result Check(string path, string workspaceRoot) | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Write/SymlinkGuard.cs | 57 | public static Result Check(string path) => Check(path, Environment.CurrentDirectory); | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Write/SymlinkGuard.cs | 64 | public static bool ContainsTraversalSegments(string path) | YES | MED |
| Harbor.Tools.Builtin/Tools/Write/WriteTool.cs | 9 | class WriteTool | YES | HIGH |
| Harbor.Tools.Builtin/Tools/Write/WriteTool.cs | 24 | IReadOnlyList<string> PromptGuidelines | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Write/WriteTool.cs | 31 | JsonDocument ParameterSchema | **NO** | LOW |
| Harbor.Tools.Builtin/Tools/Write/WriteTool.cs | 43 | public Result ValidateArguments(JsonElement args) | **NO** | HIGH |
| Harbor.Tools.Builtin/Tools/Write/WriteTool.cs | 62 | public async Task<ToolResult> ExecuteAsync( | **NO** | HIGH |

## Project: Harbor.Transport.Remote

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Transport.Remote/PsAuthHandler.cs | 6 | class PsAuthHandler | **NO** | MED |
| Harbor.Transport.Remote/PsAuthHandler.cs | 8 | public static string GeneratePsk() | **NO** | MED |
| Harbor.Transport.Remote/PsAuthHandler.cs | 15 | public static bool Validate(string? provided, string expected) | **NO** | HIGH |
| Harbor.Transport.Remote/RemoteClient.cs | 6 | class RemoteClient | **NO** | HIGH |
| Harbor.Transport.Remote/RemoteClient.cs | 17 | public async Task ConnectAsync(Uri uri, CancellationToken ct) | **NO** | HIGH |
| Harbor.Transport.Remote/RemoteClient.cs | 23 | public async Task SendAsync(UiTransportPacket packet, CancellationToken ct) | **NO** | HIGH |
| Harbor.Transport.Remote/RemoteClient.cs | 30 | public async ValueTask DisposeAsync() | **NO** | HIGH |
| Harbor.Transport.Remote/RemoteGateway.cs | 7 | class RemoteGateway | **NO** | MED |
| Harbor.Transport.Remote/RemoteGateway.cs | 18 | public async Task StartAsync(int port, CancellationToken ct) | **NO** | HIGH |
| Harbor.Transport.Remote/RemoteGateway.cs | 70 | public async Task StopAsync(CancellationToken ct) | **NO** | HIGH |
| Harbor.Transport.Remote/UiTransportPacket.cs | 5 | record UiTransportPacket | **NO** | LOW |
| Harbor.Transport.Remote/UiTransportPacket.cs | 7 | public static UiTransportPacket FromEvent(AgentEvent @event) | **NO** | MED |

## Project: Harbor.Tui.Ansi

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Tui.Ansi/Ansi.cs | 5 | class Ansi | YES | MED |
| Harbor.Tui.Ansi/Ansi.cs | 34 | public static string Fg(int n) => $"\x1b[38;5;{n}m"; | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 35 | public static string Bg(int n) => $"\x1b[48;5;{n}m"; | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 38 | public static string Fg(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m"; | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 39 | public static string Bg(int r, int g, int b) => $"\x1b[48;2;{r};{g};{b}m"; | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 42 | public static void MoveTo(int row, int col) => Console.Write($"\x1b[{row};{col}H"); | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 43 | public static void MoveUp(int n) => Console.Write($"\x1b[{n}A"); | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 44 | public static void MoveDown(int n) => Console.Write($"\x1b[{n}B"); | **NO** | HIGH |
| Harbor.Tui.Ansi/Ansi.cs | 45 | public static void MoveRight(int n) => Console.Write($"\x1b[{n}C"); | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 46 | public static void MoveLeft(int n) => Console.Write($"\x1b[{n}D"); | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 47 | public static void ClearLine() => Console.Write("\x1b[2K\r"); | **NO** | HIGH |
| Harbor.Tui.Ansi/Ansi.cs | 48 | public static void ClearLineFromCursor() => Console.Write("\x1b[K"); | **NO** | HIGH |
| Harbor.Tui.Ansi/Ansi.cs | 49 | public static void ClearScreen() => Console.Write("\x1b[2J\x1b[H"); | **NO** | HIGH |
| Harbor.Tui.Ansi/Ansi.cs | 50 | public static void HideCursor() => Console.Write("\x1b[?25l"); | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 51 | public static void ShowCursor() => Console.Write("\x1b[?25h"); | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 54 | public static void EnterAltScreen() => Console.Write("\x1b[?1049h"); | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 55 | public static void ExitAltScreen() => Console.Write("\x1b[?1049l"); | **NO** | MED |
| Harbor.Tui.Ansi/Ansi.cs | 57 | public static void WriteColored(string text, string fg, string bg = "") | **NO** | HIGH |
| Harbor.Tui.Ansi/Ansi.cs | 65 | public static void WriteLineColored(string text, string fg, string bg = "") | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 26 | class AnsiTuiRenderer | YES | MED |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 33 | ITuiRenderContext Context | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 44 | public override Task<Result> InitializeAsync(CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 58 | public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 129 | public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 138 | public override Task<Result> WriteAsync(string text, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 144 | public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 150 | public override Task<Result> ClearAsync(CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 156 | public override void Dispose() | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 166 | class AnsiRenderContext | YES | MED |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 172 | public void Write(string text) => Console.Write(text); | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 174 | public void WriteLine(string? text = null) => Console.WriteLine(text ?? string.Empty); | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 176 | public void WriteColored(string text, TuiColor foreground, TuiColor? background = null) | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 185 | public void WriteStyled(string text, TuiStyle style) | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 205 | public void SetCursorPosition(int row, int col) => Console.SetCursorPosition(col, row); | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 206 | public void ClearLine() => Console.Write("\x1b[2K\r"); | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 207 | public void Clear() => Console.Write("\x1b[2J\x1b[H"); | **NO** | HIGH |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 208 | public void HideCursor() => Console.Write("\x1b[?25l"); | **NO** | MED |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 209 | public void ShowCursor() => Console.Write("\x1b[?25h"); | **NO** | MED |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 210 | public void EnterAlternateScreen() => Console.Write("\x1b[?1049h"); | **NO** | MED |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 211 | public void ExitAlternateScreen() => Console.Write("\x1b[?1049l"); | **NO** | MED |
| Harbor.Tui.Ansi/AnsiTuiRenderer.cs | 212 | public void Flush() => Console.Out.Flush(); | **NO** | HIGH |
| Harbor.Tui.Ansi/TerminalQrRenderer.cs | 15 | class TerminalQrRenderer | YES | MED |
| Harbor.Tui.Ansi/TerminalQrRenderer.cs | 24 | public static string Render(Uri uri) | **NO** | HIGH |

## Project: Harbor.Tui.ConsoleEx

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Tui.ConsoleEx/Capabilities/CapabilityProber.cs | 8 | interface ICapabilityProbeTransport | YES | HIGH |
| Harbor.Tui.ConsoleEx/Capabilities/CapabilityProber.cs | 26 | class CapabilityProber | YES | MED |
| Harbor.Tui.ConsoleEx/Capabilities/CapabilityProber.cs | 39 | public bool IsInsideMultiplexer() | YES | MED |
| Harbor.Tui.ConsoleEx/Capabilities/CapabilityProber.cs | 47 | public static TerminalCapabilities Evaluate(IReadOnlyList<CapabilityEvent> responses) | YES | MED |
| Harbor.Tui.ConsoleEx/Capabilities/CapabilityProber.cs | 80 | public async Task<TerminalCapabilities> ProbeAsync( | YES | MED |
| Harbor.Tui.ConsoleEx/Capabilities/TerminalCapabilities.cs | 4 | struct TerminalCapabilities | YES | LOW |
| Harbor.Tui.ConsoleEx/Capabilities/TerminalCapabilities.cs | 7 | bool Probed | YES | LOW |
| Harbor.Tui.ConsoleEx/Capabilities/TerminalCapabilities.cs | 10 | bool Kitty | YES | LOW |
| Harbor.Tui.ConsoleEx/Capabilities/TerminalCapabilities.cs | 13 | uint KittyFlags | YES | LOW |
| Harbor.Tui.ConsoleEx/Capabilities/TerminalCapabilities.cs | 16 | bool VtResponsive | YES | LOW |
| Harbor.Tui.ConsoleEx/Capabilities/TerminalCapabilities.cs | 19 | bool BracketedPasteConfirmed | YES | LOW |
| Harbor.Tui.ConsoleEx/Capabilities/TerminalCapabilities.cs | 25 | bool SyncUpdates | YES | LOW |
| Harbor.Tui.ConsoleEx/Capabilities/TerminalCapabilities.cs | 27 | public static TerminalCapabilities Unprobed() => default; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Capabilities/TerminalQueries.cs | 8 | class TerminalQueries | YES | MED |
| Harbor.Tui.ConsoleEx/Capabilities/TerminalQueries.cs | 16 | public static string KittyPush(uint flags) => $"\u001B[>{flags}u"; | YES | MED |
| Harbor.Tui.ConsoleEx/Input/CapabilityEvent.cs | 4 | enum CapabilityEventKind | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/CapabilityEvent.cs | 21 | struct CapabilityEvent | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/CapabilityEvent.cs | 24 | uint Flags | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/CapabilityEvent.cs | 25 | int Mode | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/CapabilityEvent.cs | 26 | int Value | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/CapabilityEvent.cs | 27 | int Row | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/CapabilityEvent.cs | 28 | int Column | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/CapabilityEvent.cs | 30 | public static CapabilityEvent KittyFlags(uint flags) => new(CapabilityEventKind.KittyFlagsReport, flags, 0, 0, 0, 0); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/CapabilityEvent.cs | 31 | public static CapabilityEvent DecRqm(int mode, int value) => new(CapabilityEventKind.DecRqmReport, 0, mode, value, 0, 0)... | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/CapabilityEvent.cs | 32 | public static CapabilityEvent Da(int firstParam) => new(CapabilityEventKind.DeviceAttributes, 0, firstParam, 0, 0, 0); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/CapabilityEvent.cs | 33 | public static CapabilityEvent CursorPosition(int row, int column) => new(CapabilityEventKind.CursorPositionReport, 0, 0,... | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/FocusRouter.cs | 6 | interface IFocusTarget | YES | HIGH |
| Harbor.Tui.ConsoleEx/Input/FocusRouter.cs | 19 | class FocusRouter | YES | MED |
| Harbor.Tui.ConsoleEx/Input/FocusRouter.cs | 28 | public void Add(IFocusTarget target) => _order.Add(target); | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Input/FocusRouter.cs | 30 | public bool Next() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/FocusRouter.cs | 40 | public bool Previous() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/FocusRouter.cs | 56 | public bool Jump(int index) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/FocusRouter.cs | 66 | public bool FocusById(string id) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/ITerminalModeController.cs | 9 | interface ITerminalModeController | YES | HIGH |
| Harbor.Tui.ConsoleEx/Input/InputEvent.cs | 4 | enum InputEventKind | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/InputEvent.cs | 23 | struct InputEvent | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/InputEvent.cs | 39 | ResizeSignal Resize | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/InputEvent.cs | 42 | public static InputEvent FromKey(KeyEvent evt) => new(InputEventKind.Key, evt, default, default, default, default); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/InputEvent.cs | 43 | public static InputEvent FromMouse(MouseEvent evt) => new(InputEventKind.Mouse, default, evt, default, default, default)... | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/InputEvent.cs | 44 | public static InputEvent FromPaste(PasteEvent evt) => new(InputEventKind.Paste, default, default, evt, default, default)... | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/InputEvent.cs | 45 | public static InputEvent FromResize(ResizeSignal evt) => new(InputEventKind.Resize, default, default, default, evt, defa... | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/InputEvent.cs | 46 | public static InputEvent FromCapability(CapabilityEvent evt) => new(InputEventKind.Capability, default, default, default... | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/InputEvent.cs | 47 | public static InputEvent Unknown() => new(InputEventKind.Unknown, default, default, default, default, default); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/InputEvent.cs | 49 | public override string ToString() => Kind switch | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/KeyCode.cs | 8 | enum KeyCode | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/KeyEvent.cs | 20 | struct KeyEvent | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/KeyEvent.cs | 28 | KeyCode Key | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/KeyEvent.cs | 29 | Rune Character | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/KeyEvent.cs | 30 | KeyModifiers Modifiers | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/KeyEvent.cs | 32 | bool IsKittyEncoded | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/KeyEvent.cs | 33 | uint Codepoint | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/KeyEvent.cs | 35 | public static KeyEvent Char(Rune character, KeyModifiers modifiers = KeyModifiers.None, bool isKittyEncoded = false) => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/KeyEvent.cs | 38 | public static KeyEvent Simple(KeyCode key, KeyModifiers modifiers = KeyModifiers.None, bool isKittyEncoded = false) => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/KeyEvent.cs | 41 | public override string ToString() => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/KeyEventType.cs | 8 | enum KeyEventType | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/KeyModifiers.cs | 9 | enum KeyModifiers | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/MouseEvent.cs | 5 | enum MouseButton | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/MouseEvent.cs | 19 | enum MouseEventType | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/MouseEvent.cs | 33 | struct MouseEvent | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/MouseEvent.cs | 41 | MouseButton Button | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/MouseEvent.cs | 42 | int Column | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/MouseEvent.cs | 43 | int Row | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/MouseEvent.cs | 44 | KeyModifiers Modifiers | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/MouseEvent.cs | 46 | public override string ToString() => $"{Type} {Button} @({Column},{Row}) [{Modifiers}]"; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/MouseRouter.cs | 6 | interface IPointerTarget | YES | HIGH |
| Harbor.Tui.ConsoleEx/Input/MouseRouter.cs | 23 | class MouseRouter | YES | MED |
| Harbor.Tui.ConsoleEx/Input/MouseRouter.cs | 38 | public void Bind(IPointerTarget target, Rect rect) => _regions.Add((target, rect)); | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Input/MouseRouter.cs | 40 | public void Rebind(IPointerTarget target, Rect rect) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/MouseRouter.cs | 46 | public void Clear() => _regions.Clear(); | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Input/MouseRouter.cs | 48 | public void Press(int col, int row) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/MouseRouter.cs | 57 | public void Release(int col, int row) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Input/MouseRouter.cs | 66 | public void Wheel(int col, int row, int delta) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/MouseRouter.cs | 76 | public (IPointerTarget Target, Rect Rect)? HitTest(int col, int row) | YES | MED |
| Harbor.Tui.ConsoleEx/Input/NullModeController.cs | 4 | class NullModeController | YES | MED |
| Harbor.Tui.ConsoleEx/Input/NullModeController.cs | 6 | public void Enter() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/NullModeController.cs | 10 | public void Restore() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/PasteEvent.cs | 15 | struct PasteEvent | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/PasteEvent.cs | 17 | string Text | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/PasteEvent.cs | 18 | bool WasTruncated | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/PasteEvent.cs | 20 | public override string ToString() => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/ResizeSignal.cs | 4 | struct ResizeSignal | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/ResizeSignal.cs | 6 | int Width | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/ResizeSignal.cs | 7 | int Height | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/ResizeSignal.cs | 9 | public override string ToString() => $"Resize({Width}x{Height})"; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/TerminalInputSource.cs | 16 | class TerminalInputSource | YES | MED |
| Harbor.Tui.ConsoleEx/Input/TerminalInputSource.cs | 25 | EscapeSequenceParser Parser | YES | HIGH |
| Harbor.Tui.ConsoleEx/Input/TerminalInputSource.cs | 54 | public Task RunAsync(CancellationToken cancellationToken) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Input/TerminalInputSource.cs | 275 | public void Dispose() | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Input/TerminalInputSourceOptions.cs | 5 | class TerminalInputSourceOptions | YES | MED |
| Harbor.Tui.ConsoleEx/Input/TerminalInputSourceOptions.cs | 8 | TimeSpan EscFlushTimeout | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/TerminalInputSourceOptions.cs | 11 | TimeSpan PasteAbortTimeout | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/TerminalInputSourceOptions.cs | 15 | TimeSpan? ResizePollInterval | YES | LOW |
| Harbor.Tui.ConsoleEx/Input/TerminalInputSourceOptions.cs | 20 | int ReadBufferSize | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/TerminalInputSourceOptions.cs | 22 | TerminalInputSourceOptions Default | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/TerminalInputStream.cs | 17 | class TerminalInputStream | YES | MED |
| Harbor.Tui.ConsoleEx/Input/TerminalInputStream.cs | 20 | public static Stream Open() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 16 | class UnixTermiosModeController | YES | MED |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 43 | bool IsRaw | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 48 | public void Enter() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 88 | public void Restore() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 109 | uint CIflag | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 110 | uint COflag | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 111 | uint CCflag | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 112 | uint CLflag | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 113 | byte CLine | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 114 | ControlCharacters ControlCharacters | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 119 | uint CIspeed | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/UnixTermiosModeController.cs | 120 | uint COspeed | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/WindowsVtModeController.cs | 14 | class WindowsVtModeController | YES | MED |
| Harbor.Tui.ConsoleEx/Input/WindowsVtModeController.cs | 16 | bool IsRaw | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Input/WindowsVtModeController.cs | 18 | public void Enter() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Input/WindowsVtModeController.cs | 30 | public void Restore() => IsRaw = false; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 18 | class EscapeSequenceParser | YES | MED |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 65 | int MalformedSequenceCount | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 66 | int IgnoredSequenceCount | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 69 | bool IsAwaitingPasteClose | YES | LOW |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 73 | int NestedPasteMarkerCount | YES | LOW |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 77 | public void Parse(ReadOnlySpan<byte> bytes) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 90 | public void FlushPendingEscape() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 107 | public void DrainEvents(List<InputEvent> destination) | YES | MED |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 117 | public bool TryTakeEvent(out InputEvent evt) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 133 | public void Reset() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 147 | public void ClearEvents() | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Parsing/EscapeSequenceParser.cs | 1011 | public void AbortPendingPaste() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Parsing/ParserOptions.cs | 4 | class ParserOptions | YES | MED |
| Harbor.Tui.ConsoleEx/Parsing/ParserOptions.cs | 18 | int MaxParamsBytes | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Parsing/ParserOptions.cs | 19 | int MaxIntermediatesBytes | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Parsing/ParserOptions.cs | 20 | int MaxPasteBytes | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Parsing/ParserOptions.cs | 21 | int MaxStringBytes | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Parsing/ParserOptions.cs | 23 | ParserOptions Default | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Parsing/ParserState.cs | 8 | enum ParserState | YES | LOW |
| Harbor.Tui.ConsoleEx/Parsing/Utf8IncrementalDecoder.cs | 6 | enum Utf8DecodeStatus | YES | LOW |
| Harbor.Tui.ConsoleEx/Parsing/Utf8IncrementalDecoder.cs | 28 | struct Utf8IncrementalDecoder | YES | LOW |
| Harbor.Tui.ConsoleEx/Parsing/Utf8IncrementalDecoder.cs | 38 | public void Reset() | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Parsing/Utf8IncrementalDecoder.cs | 44 | public Utf8DecodeStatus DecodeStep(byte b, out Rune rune) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 22 | class AnsiWriter | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 77 | public void BeginFrame() | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 92 | public async ValueTask EndFrameAsync(CancellationToken cancellationToken = default) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 111 | public void MoveTo(int x, int y) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 130 | public void SetStyle(in CellStyle target) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 202 | public void ResetStyle() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 213 | public void PutRune(Rune rune) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 222 | public void PutRuneWidth(Rune rune, int advance) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 230 | public void WriteText(ReadOnlySpan<char> text) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 249 | public void WriteStyledText(ReadOnlySpan<char> text, in CellStyle style, bool resetAfter = true) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 262 | public void Raw(string sequence) => AppendAscii(sequence); | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 265 | public void MoveUpToColumnStart(int lines) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 288 | public void EraseFromCursorDown() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 296 | public void EmitEraseInDisplay(int mode) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 307 | public void EraseEntireLine() | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 315 | public void CarriageReturn() | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 323 | public void WriteLineBreak() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 335 | public void HideCursor() => AppendAscii("\x1B[?25l"); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 337 | public void ShowCursor() => AppendAscii("\x1B[?25h"); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 340 | public void InvalidateCursorPosition() | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/AnsiWriter.cs | 347 | public async ValueTask FlushAsync(CancellationToken cancellationToken = default) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 13 | struct Cell | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 19 | int Rune | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 20 | uint Fg | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 21 | uint Bg | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 22 | ushort Flags | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 23 | byte Width | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 35 | Cell Blank | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 38 | Cell WideTail | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 40 | public static Cell From(Rune rune, in CellStyle style) => new( | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 57 | public static Cell BlankAt(byte width) => width switch | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 63 | public bool Equals(Cell other) => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 66 | public override bool Equals(object? obj) => obj is Cell other && Equals(other); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 68 | public override int GetHashCode() => HashCode.Combine(Rune, Fg, Bg, Flags, Width); | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/Cell.cs | 73 | public override string ToString() => Width == WSkip | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 8 | enum StyleAttr | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 28 | struct PackedColor | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 33 | uint Value | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 38 | PackedColor Default | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 41 | internal static PackedColor FromRaw(uint value) => new(value); | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 44 | public static PackedColor Indexed(byte index) => new((uint)index + 1); | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 47 | public static PackedColor Rgb(byte r, byte g, byte b) => | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 63 | public bool Equals(PackedColor other) => Value == other.Value; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 64 | public override bool Equals(object? obj) => obj is PackedColor other && Equals(other); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 65 | public override int GetHashCode() => (int)Value; | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 68 | public override string ToString() => IsDefault ? "default" | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 78 | struct CellStyle | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 80 | PackedColor Fg | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 81 | PackedColor Bg | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 82 | StyleAttr Attrs | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 92 | CellStyle Plain | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 96 | public bool Equals(CellStyle other) => Fg == other.Fg && Bg == other.Bg && Attrs == other.Attrs; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 97 | public override bool Equals(object? obj) => obj is CellStyle other && Equals(other); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/CellStyle.cs | 98 | public override int GetHashCode() => HashCode.Combine(Fg, Bg, Attrs); | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/ComposerController.cs | 7 | enum ComposerAction | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/ComposerController.cs | 28 | class ComposerController | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/ComposerController.cs | 30 | PromptBuffer Buffer | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/ComposerController.cs | 33 | public ComposerAction HandleKey(in KeyEvent key) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/DiffEngine.cs | 22 | class DiffEngine | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/DiffEngine.cs | 38 | public void FrameHint(in Rect damage) => _hints.Add(damage); | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/DiffEngine.cs | 41 | public void ClearHints() => _hints.Clear(); | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/DiffEngine.cs | 44 | internal long HintArea() | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/DiffEngine.cs | 60 | public void Flush(ScreenBuffer next, AnsiWriter writer) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/DiffEngine.cs | 150 | public bool FrontMatches(ScreenBuffer next) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/ITerminalBackend.cs | 9 | interface ITerminalBackend | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/InlineSession.cs | 14 | class InlineSession | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/InlineSession.cs | 25 | public void SetLiveLines(int lines) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/InlineSession.cs | 35 | public void EraseLiveRegion() | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/InlineSession.cs | 62 | public int WriteFinalizedBlock(ReadOnlySpan<char> text, int width, CellStyle? style = null) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 6 | enum SplitDir | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 20 | class Panel | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 29 | string Id | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 30 | Size Min | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 31 | int Priority | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 34 | Rect Rect | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 36 | bool Focused | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 38 | public abstract void Paint(ScreenBuffer buffer); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 41 | internal int MinAlong(SplitDir dir) => dir == SplitDir.Horizontal ? Min.Width : Min.Height; | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 45 | record struct | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 47 | class SplitNode | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 54 | Panel? Leaf | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 56 | SplitDir Dir | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 59 | float Ratio | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 61 | byte GapSize | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 63 | SplitNode? A | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 64 | SplitNode? B | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 66 | public int MinAlong(SplitDir dir) => Leaf is not null | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 70 | public IEnumerable<Panel> Panels() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 96 | class LayoutTree | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 107 | public void AddRoot(Panel panel) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 116 | public void Split(string panelId, SplitDir dir, float ratio, Panel newPanel, byte gap = 1) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 129 | public void Remove(string panelId) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 144 | public void Solve(int width, int height) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 349 | class BorderPanel | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 360 | string Title | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/LayoutTree.cs | 362 | public override void Paint(ScreenBuffer buffer) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 7 | enum EditOutcomeKind | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 27 | record struct | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 31 | public static EditOutcome Cursor() => new(EditOutcomeKind.CursorOnly, 0, 0); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 33 | public static EditOutcome Text(int start, int end, bool movedCursor) => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 47 | class PromptBuffer | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 78 | public string SnapshotText() => new(_buf, 0, _length); | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 81 | public string TakeText() | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 89 | public void Clear() | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 97 | public EditOutcome Insert(Rune rune) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 117 | public EditOutcome InsertText(string text) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 131 | public EditOutcome Backspace() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 146 | public EditOutcome DeleteForward() | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 161 | public EditOutcome DeleteToLineStart() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 177 | public EditOutcome DeleteWordBackward() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 209 | public EditOutcome MoveLeft() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 220 | public EditOutcome MoveRight() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 231 | public EditOutcome MoveToLineStart() { _cursor = LineStartOf(_cursor); return EditOutcome.Cursor(); } | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 232 | public EditOutcome MoveToLineEnd() { _cursor = LineEndOf(_cursor); return EditOutcome.Cursor(); } | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 233 | public EditOutcome MoveToStart() { _cursor = 0; return EditOutcome.Cursor(); } | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 234 | public EditOutcome MoveToEnd() { _cursor = _length; return EditOutcome.Cursor(); } | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 237 | public EditOutcome MoveUp() | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 254 | public EditOutcome MoveDown() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 274 | public int LineIndexOf(int offset) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 288 | public int LineStartOf(int offset) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 299 | public int LineEndOf(int offset) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 311 | internal static int DisplayCells(ReadOnlySpan<char> slice) | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/PromptBuffer.cs | 334 | internal int ClampToCells(int start, int end, int fallback, int cells) | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/PromptRenderer.cs | 9 | class PromptRenderer | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/PromptRenderer.cs | 15 | public static int MeasureLineCount(in PromptBuffer buffer) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptRenderer.cs | 35 | public static int Render(AnsiWriter writer, in PromptBuffer buffer, int widthCells, string? placeholder = null) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/PromptViewport.cs | 12 | struct PromptViewport | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/PromptViewport.cs | 15 | int Start | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/PromptViewport.cs | 20 | public static PromptViewport ScrollIntoView(ReadOnlySpan<char> line, int caretInLine, int widthCells) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/Rect.cs | 4 | record struct | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/Rect.cs | 9 | public bool Contains(int x, int y) => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/Rect.cs | 12 | public Rect Intersect(Rect other) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 17 | class ScreenBuffer | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 36 | int Cols | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 37 | int Rows | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 42 | internal bool IsRowHashValid(int y) => _rowHashValid[y]; | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 47 | public ref Cell At(int x, int y) => ref _cells[(y * Cols) + x]; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 49 | public Cell Get(int x, int y) => _cells[(y * Cols) + x]; | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 58 | public void Resize(int cols, int rows) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 105 | public void BlankAll() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 111 | public void InvalidateAll() => Array.Clear(_rowHashValid, 0, Rows); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 113 | public void MarkRowDirty(int y) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 123 | public void FillAll(in Cell cell) => Fill(new Rect(0, 0, Cols, Rows), in cell); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 131 | public void Fill(Rect rect, in Cell cell) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 175 | public bool SetRune(int x, int y, Rune rune, in CellStyle style) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 219 | public void SetText(int x, int y, ReadOnlySpan<char> text, in CellStyle style) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 247 | public bool SetStyleAt(int x, int y, in CellStyle style) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 268 | public ulong RowHashCode(int y) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/ScreenBuffer.cs | 280 | internal void AdoptRowHash(ScreenBuffer source, int y) | YES | LOW |
| Harbor.Tui.ConsoleEx/Rendering/StdoutBackend.cs | 8 | class StdoutBackend | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/StdoutBackend.cs | 14 | public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/TextWrap.cs | 12 | class TextWrap | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/TextWrap.cs | 15 | public static void WrapTo(ReadOnlySpan<char> text, int width, List<string> output) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/TextWrap.cs | 41 | public static void WrapDocument(ReadOnlySpan<char> text, int width, List<string> output) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Rendering/UnicodeWidth.cs | 16 | class UnicodeWidth | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/UnicodeWidth.cs | 100 | public static int Width(Rune r) | YES | MED |
| Harbor.Tui.ConsoleEx/Rendering/UnicodeWidth.cs | 117 | public static int Width(ReadOnlySpan<char> text) | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 29 | class ChatScreenBridge | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 53 | long StartedMs | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 68 | IDisposable Subscription | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 74 | public ValueTask AcceptAsync(AgentEvent evt, CancellationToken ct = default) => HandleEvent(evt, ct); | YES | HIGH |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 79 | public void Dispose() | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 89 | public void NotifyLocalUserMessage() => _replayedMessages++; | YES | HIGH |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 92 | public void Tick(long nowMs) | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 196 | internal void ReplayHistory(IReadOnlyList<AgentMessage> messages) | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 247 | internal void Incoming(string delta) | YES | LOW |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 298 | internal void FlushStreamNow() | YES | LOW |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 355 | internal static string? TryExtractDiff(ToolResult result) | YES | LOW |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 365 | internal static string Summarize(JsonElement args) | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 378 | public void AppendSystemLine(string text) | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/ChatScreenBridge.cs | 387 | public void Dispose() => Subscription.Dispose(); | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Streaming/CommitTickPacer.cs | 4 | enum DrainPlanKind | YES | LOW |
| Harbor.Tui.ConsoleEx/Streaming/CommitTickPacer.cs | 14 | record struct | YES | LOW |
| Harbor.Tui.ConsoleEx/Streaming/CommitTickPacer.cs | 25 | class CommitTickPacer | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/CommitTickPacer.cs | 36 | bool IsCatchUp | YES | LOW |
| Harbor.Tui.ConsoleEx/Streaming/CommitTickPacer.cs | 45 | public DrainPlanKind Decide(QueueSnapshot snap, long nowMs) | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/InlineAgentStreamBridge.cs | 14 | class InlineAgentStreamBridge | YES | HIGH |
| Harbor.Tui.ConsoleEx/Streaming/InlineAgentStreamBridge.cs | 39 | int Width | YES | LOW |
| Harbor.Tui.ConsoleEx/Streaming/InlineAgentStreamBridge.cs | 41 | IDisposable Subscription | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Streaming/InlineAgentStreamBridge.cs | 81 | public void Tick(long nowMs) | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/InlineAgentStreamBridge.cs | 91 | public void FinishStream() | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/InlineAgentStreamBridge.cs | 124 | public int RenderLiveRegion(string? placeholder = null) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Streaming/InlineAgentStreamBridge.cs | 150 | public ValueTask FlushAsync(CancellationToken cancellationToken = default) => | YES | HIGH |
| Harbor.Tui.ConsoleEx/Streaming/InlineAgentStreamBridge.cs | 196 | public void Dispose() => Subscription.Dispose(); | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Streaming/ScreenSession.cs | 14 | class ScreenSession | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/ScreenSession.cs | 38 | int CurrentCols | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Streaming/ScreenSession.cs | 39 | int CurrentRows | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Streaming/ScreenSession.cs | 43 | public void CheckAutoSize() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Streaming/ScreenSession.cs | 56 | public void Resize(int cols, int rows) | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/ScreenSession.cs | 83 | public void BeginFrame() | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/ScreenSession.cs | 94 | public async ValueTask FlushFrameAsync(CancellationToken cancellationToken = default) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Streaming/StreamBlock.cs | 12 | class StreamBlock | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/StreamBlock.cs | 30 | int RevealedChars | YES | LOW |
| Harbor.Tui.ConsoleEx/Streaming/StreamBlock.cs | 35 | int LinesConsumed | YES | LOW |
| Harbor.Tui.ConsoleEx/Streaming/StreamBlock.cs | 40 | bool IsFinalized | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Streaming/StreamBlock.cs | 44 | public void AppendDelta(string delta) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Streaming/StreamBlock.cs | 59 | public void Complete() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Streaming/StreamBlock.cs | 83 | public IReadOnlyList<string> Tick(long nowMs) | YES | MED |
| Harbor.Tui.ConsoleEx/Streaming/StreamBlock.cs | 114 | public ReadOnlySpan<char> PartialTail() | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/AssistantMarkdownBlock.cs | 12 | class AssistantMarkdownBlock | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/AssistantMarkdownBlock.cs | 26 | public BlockMeasure Measure(int width) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/AssistantMarkdownBlock.cs | 32 | public int CheapEstimate(int width) => BlockMath.EstimateLines(_source, Math.Max(1, width)); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/AssistantMarkdownBlock.cs | 34 | public void Paint(in BlockPaintContext ctx) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/AssistantMarkdownBlock.cs | 45 | public string RawText() => _source; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/AssistantMarkdownBlock.cs | 47 | internal static void PaintLine(ScreenBuffer buffer, int x, int y, MdLine line) | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/AssistantMarkdownBlock.cs | 58 | internal static CellStyle StyleFor(MdStyle style) => style switch | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 12 | class WrappedText | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 26 | public ReadOnlyMemory<string> GetLines(int width) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 41 | class BlockMath | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 44 | public static int EstimateLines(string source, int width) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 66 | class UserBlock | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 79 | public BlockMeasure Measure(int width) => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 82 | public int CheapEstimate(int width) => BlockMath.EstimateLines(_text.Source, Math.Max(1, BodyWidth(width))); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 84 | public void Paint(in BlockPaintContext ctx) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 107 | public string RawText() => Prefix + _text.Source; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 113 | class SystemBlock | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 125 | public BlockMeasure Measure(int width) => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 128 | public int CheapEstimate(int width) => BlockMath.EstimateLines(_text.Source, Math.Max(1, width)); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 130 | public void Paint(in BlockPaintContext ctx) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/BasicBlocks.cs | 141 | public string RawText() => _text.Source; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/ChatBlock.cs | 10 | record struct | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ChatBlock.cs | 12 | public static BlockMeasure Exact(int lines) => new(lines, lines, true); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/ChatBlock.cs | 14 | public static BlockMeasure Estimate(int min, int max) => new(min, max, false); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/ChatBlock.cs | 25 | struct BlockPaintContext | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ChatBlock.cs | 34 | ScreenBuffer Buffer | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ChatBlock.cs | 37 | Rect Rect | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ChatBlock.cs | 40 | long Tick | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ChatBlock.cs | 49 | interface IChatBlock | YES | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/ChatPalette.cs | 10 | class ChatPalette | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/ChatScreenLayout.cs | 10 | class ComposerPanel | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/ChatScreenLayout.cs | 21 | Rendering.ComposerController Composer | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ChatScreenLayout.cs | 23 | string? Placeholder | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ChatScreenLayout.cs | 25 | public override void Paint(ScreenBuffer buffer) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/ChatScreenLayout.cs | 75 | class StatusPanel | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/ChatScreenLayout.cs | 85 | StatusViewModel Vm | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ChatScreenLayout.cs | 88 | long Tick | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ChatScreenLayout.cs | 90 | public override void Paint(ScreenBuffer buffer) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/ChatScreenLayout.cs | 133 | record ChatScreen | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ChatScreenLayout.cs | 139 | public static ChatScreen Build( | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 7 | enum DiffLineKind | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 17 | record struct | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 25 | class UnifiedDiffParser | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 28 | public static bool LooksLikeDiff(ReadOnlySpan<char> text) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 36 | public static IReadOnlyList<DiffLine> Parse(string diffText) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 139 | class DiffBlock | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 157 | string? Path | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 168 | public BlockMeasure Measure(int width) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 174 | public int CheapEstimate(int width) => BlockMath.EstimateLines(_diffText, Math.Max(8, width)); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 176 | public void Paint(in BlockPaintContext ctx) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 230 | internal static string Gutter(DiffLine dl) => dl.Kind switch | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 237 | public string RawText() => _diffText; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 249 | class DiffNumberExtensions | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 251 | public static string OldNumberString(this DiffLine dl) => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/DiffBlock.cs | 254 | public static string NewNumberString(this DiffLine dl) => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 3 | enum MdBlockKind | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 16 | struct MdBlock | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 18 | MdBlockKind Kind | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 19 | int Start | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 20 | int End | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 21 | bool Complete | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 24 | int Level | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 29 | enum LineKind | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 45 | class MarkdownBlockParser | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 47 | public static List<MdBlock> Parse(ReadOnlySpan<char> source) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 178 | public static LineKind Classify(ReadOnlySpan<char> trimmedLine) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 224 | public static int HeadingLevel(ReadOnlySpan<char> trimmedLine) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MarkdownBlockParser.cs | 240 | public static bool IsListItem(ReadOnlySpan<char> trimmedLine, out int markerWidth) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MdModel.cs | 4 | enum MdStyle | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MdModel.cs | 23 | record struct | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MdModel.cs | 26 | class MdLine | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/MdModel.cs | 32 | IReadOnlyList<MdSpan> Spans | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/StreamingMarkdownRenderer.cs | 6 | record struct | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/StreamingMarkdownRenderer.cs | 22 | class StreamingMarkdownRenderer | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/StreamingMarkdownRenderer.cs | 41 | public void Push(ReadOnlySpan<char> chunk) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/StreamingMarkdownRenderer.cs | 52 | public void Complete() => _complete = true; | YES | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/StreamingMarkdownRenderer.cs | 55 | public IReadOnlyList<MdLine> GetLines() | YES | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/StreamingMarkdownRenderer.cs | 63 | public MdLine LineAt(int index) => | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/StreamingMarkdownRenderer.cs | 67 | public bool RenderTail(int width) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/StreamingMarkdownRenderer.cs | 162 | internal static List<MdLine> RenderRange(string sourceText, int start, int end, int width) | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/StreamingMarkdownRenderer.cs | 220 | public static List<MdSpan> ScanInline(ReadOnlySpan<char> line) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/Markdown/StreamingMarkdownRenderer.cs | 287 | internal static void AddWrapped(List<MdLine> output, IReadOnlyList<MdSpan> spans, int width) | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/SpinnerStrip.cs | 6 | enum SpinnerRhythm | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/SpinnerStrip.cs | 21 | class SpinnerStrip | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/SpinnerStrip.cs | 43 | public static ReadOnlySpan<char> Frame(long monotonicTick, SpinnerRhythm rhythm = SpinnerRhythm.Working) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/SpinnerStrip.cs | 53 | public static string FrameString(long monotonicTick, SpinnerRhythm rhythm = SpinnerRhythm.Working) => rhythm switch | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/SpinnerStrip.cs | 60 | public static ReadOnlySpan<char> AsciiFrame(long monotonicTick) => | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/StatusSegmentBar.cs | 6 | enum StatusAccent | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/StatusSegmentBar.cs | 22 | record struct | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/StatusSegmentBar.cs | 25 | enum StatusBarMode | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/StatusSegmentBar.cs | 34 | class SegWidth | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/StatusSegmentBar.cs | 36 | public static int Of(ReadOnlySpan<char> text) => UnicodeWidth.Width(text); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/StatusSegmentBar.cs | 44 | class StatusBarLayout | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/StatusSegmentBar.cs | 48 | public static int Fit(Span<StatusSeg> segs, int width) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/StatusSegmentBar.cs | 96 | public static int TotalWidth(ReadOnlySpan<StatusSeg> segs) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 12 | class StatusViewModel | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 15 | string Model | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 18 | string? Cost | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 21 | string? Tokens | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 23 | StatusBarMode Mode | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 25 | int? ContextTokensUsed | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 27 | int ContextWindow | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 30 | public bool TryGetContextTokens(out int used) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 36 | public void SetContext(int usedTokens, int windowTokens) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 42 | public void ClearContext() => ContextTokensUsed = null; | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 45 | public void SetUsage(long inputTokens, long outputTokens, decimal? costUsd) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 52 | public int BuildSegments(Span<StatusSeg> workspace) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 110 | internal static string ContextBar(double ratio) | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 116 | internal static string FormatCount(long v) => v switch | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 125 | class StatusBarWidget | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 127 | public static CellStyle StyleOf(StatusAccent accent) => accent switch | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/StatusViewModel.cs | 137 | public static void Paint(ScreenBuffer buffer, Rect rect, ReadOnlySpan<StatusSeg> segs) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/StreamingMarkdownBlock.cs | 12 | class StreamingMarkdownBlock | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/StreamingMarkdownBlock.cs | 47 | public void Push(ReadOnlySpan<char> chunk) => _renderer.Push(chunk); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/StreamingMarkdownBlock.cs | 49 | public void Complete() => _renderer.Complete(); | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/StreamingMarkdownBlock.cs | 51 | public override string ToString() => $"stream({_renderer.LineCount} lines)"; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/StreamingMarkdownBlock.cs | 53 | public BlockMeasure Measure(int width) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/StreamingMarkdownBlock.cs | 59 | public int CheapEstimate(int width) => Math.Max(1, _renderer.LineCount); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/StreamingMarkdownBlock.cs | 61 | public void Paint(in BlockPaintContext ctx) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/StreamingMarkdownBlock.cs | 71 | public string RawText() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 4 | enum LayoutOutcome | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 30 | class TimelineLayoutCache | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 36 | int ExactH | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 37 | int EstH | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 38 | bool Measured | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 40 | public static Slot Estimated(int est) => new(-1, Math.Max(1, est), false); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 41 | public static Slot ExactMeasured(int h) => new(h, h, true); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 63 | public IChatBlock BlockAt(int index) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 73 | public void Append(IChatBlock block) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 88 | public bool EvictFirst() | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 118 | public void MarkHeightsDirty(int fromIndex) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 139 | public void Replace(int index, IChatBlock block) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 152 | public void PinAnchor(long scrollTopY) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 170 | public LayoutOutcome PrepareLayout(int width, int viewportH, long scrollY) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 205 | public long RestoreAnchor() | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 224 | public int EntryAtY(long y) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 249 | public (int First, int Last) VisibleRange(long scrollY, int viewportH) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 266 | public long BlockTop(int index) => _virtual[index]; | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineLayoutCache.cs | 268 | public int EffectiveHeight(int index) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineRing.cs | 12 | class TimelineRing | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/TimelineRing.cs | 27 | long BudgetBytes | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/TimelineRing.cs | 31 | long UsedBytes | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/TimelineRing.cs | 45 | public void Append(IChatBlock block) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 7 | enum ToolCallStatus | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 15 | record struct | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 22 | class ToolResultBody | YES | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 32 | string Output | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 33 | bool IsError | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 34 | TimeSpan Duration | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 35 | string? DiffText | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 37 | public static string FormatDuration(TimeSpan d) => d.TotalMilliseconds switch | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 50 | class ToolCallBlock | YES | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 67 | ToolCallInfo Info | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 74 | int MaxBodyLines | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 84 | public void Complete(ToolResultBody body) | YES | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 95 | public BlockMeasure Measure(int width) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 110 | public int CheapEstimate(int width) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 125 | public void Paint(in BlockPaintContext ctx) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 264 | public string RawText() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 283 | class DiffRenderer | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 285 | public static int CountLines(string diffText) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/ToolCallBlock.cs | 299 | public static void RenderPlain(string diffText, ScreenBuffer buffer, int x, int y, int maxRows) | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 11 | class VirtualizedChatTimeline | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 18 | long BudgetBytes | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 25 | long ScrollY | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 28 | bool FollowTail | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 31 | long CurrentTick | YES | LOW |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 33 | public IChatBlock BlockAt(int index) => _cache.BlockAt(index); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 35 | public void Append(IChatBlock block) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 63 | public void ReplaceLast(IChatBlock block) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 80 | public void Replace(IChatBlock existing, IChatBlock replacement) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 97 | public void MarkLastDirty() => _cache.MarkHeightsDirty(Math.Max(0, _cache.Count - 1)); | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 99 | public void ScrollUp(int lines) => ScrollBy(-lines); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 101 | public void ScrollDown(int lines) => ScrollBy(lines); | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 103 | public void ScrollBy(int lines) | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 113 | public void PageUp(int viewportHeight) => ScrollBy(-Math.Max(1, viewportHeight - 1)); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 115 | public void PageDown(int viewportHeight) => ScrollBy(Math.Max(1, viewportHeight - 1)); | **NO** | HIGH |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 117 | public void ScrollToTop() | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 124 | public void ScrollToEnd(int viewportHeight) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 135 | public LayoutOutcome PrepareFrame(int width, int viewportH) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 171 | public void Paint(ScreenBuffer buffer, Rect rect) | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 198 | public (int First, int Last) VisibleRange(int viewportH) => _cache.VisibleRange(ScrollY, viewportH); | **NO** | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 202 | class ChatTimelinePanel | YES | MED |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 209 | VirtualizedChatTimeline Timeline | **NO** | LOW |
| Harbor.Tui.ConsoleEx/Widgets/VirtualizedChatTimeline.cs | 211 | public override void Paint(ScreenBuffer buffer) | **NO** | MED |

## Project: Harbor.Tui.Notifications

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 28 | class NotificationTuiRenderer | YES | MED |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 43 | ITuiRenderContext Context | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 46 | public override Task<Result> InitializeAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 53 | public override async Task RenderAsync(AgentEvent @event, CancellationToken ct = default) | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 95 | public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default) | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 103 | public override Task<Result> WriteAsync(string text, CancellationToken ct = default) | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 107 | public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default) | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 111 | public override Task<Result> ClearAsync(CancellationToken ct = default) | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 131 | interface INotificationBackend | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 134 | string Name | YES | LOW |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 140 | public void Notify(string title, string body, bool isError); | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 144 | class LinuxNotifySendBackend | YES | MED |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 153 | public void Notify(string title, string body, bool isError) | **NO** | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 181 | class MacOsascriptBackend | YES | MED |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 190 | public void Notify(string title, string body, bool isError) | **NO** | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 217 | class WindowsToastBackend | YES | MED |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 226 | public void Notify(string title, string body, bool isError) | **NO** | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 247 | class NullNotificationBackend | YES | MED |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 250 | public void Notify(string title, string body, bool isError) { } | **NO** | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 254 | class NotificationRenderContext | YES | MED |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 264 | public void Write(string text) { } | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 266 | public void WriteLine(string? text = null) { } | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 268 | public void WriteColored(string text, TuiColor foreground, TuiColor? background = null) { } | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 270 | public void WriteStyled(string text, TuiStyle style) { } | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 272 | public void SetCursorPosition(int row, int col) { } | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 274 | public void ClearLine() { } | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 276 | public void Clear() { } | YES | HIGH |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 278 | public void HideCursor() { } | YES | MED |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 280 | public void ShowCursor() { } | YES | MED |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 282 | public void EnterAlternateScreen() { } | YES | MED |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 284 | public void ExitAlternateScreen() { } | YES | MED |
| Harbor.Tui.Notifications/NotificationTuiRenderer.cs | 286 | public void Flush() { } | YES | HIGH |

## Project: Harbor.Tui.Plain

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 12 | class PlainTuiRenderer | YES | MED |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 24 | ITuiRenderContext Context | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 35 | public override Task RenderAsync(AgentEvent @event, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 86 | public override Task<Result<string>> ReadLineAsync(string prompt, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 94 | public override Task<Result> WriteAsync(string text, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 100 | public override Task<Result> WriteLineAsync(string? text = null, CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 106 | public override Task<Result> ClearAsync(CancellationToken ct = default) | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 112 | public override void Dispose() | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 120 | class PlainRenderContext | **NO** | MED |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 132 | public void Write(string text) => _writer.Write(text); | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 133 | public void WriteLine(string? text = null) => _writer.WriteLine(text ?? string.Empty); | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 134 | public void WriteColored(string text, TuiColor foreground, TuiColor? background = null) => _writer.Write(text); | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 135 | public void WriteStyled(string text, TuiStyle style) => _writer.Write(text); | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 136 | public void SetCursorPosition(int row, int col) { } | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 137 | public void ClearLine() { } | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 138 | public void Clear() { } | **NO** | HIGH |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 139 | public void HideCursor() { } | **NO** | MED |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 140 | public void ShowCursor() { } | **NO** | MED |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 141 | public void EnterAlternateScreen() { } | **NO** | MED |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 142 | public void ExitAlternateScreen() { } | **NO** | MED |
| Harbor.Tui.Plain/PlainTuiRenderer.cs | 143 | public void Flush() => _writer.Flush(); | **NO** | HIGH |

## Project: Harbor.Ui.Framework.Abstractions

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Ui.Framework.Abstractions/Configuration/ICommonConfigReader.cs | 29 | interface ICommonConfigReader | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Configuration/ICommonConfigReader.cs | 40 | public Task<(string? ProviderId, string? ModelId)?> TryReadProviderModelAsync( | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/DiagnosticEntry.cs | 10 | record DiagnosticEntry | YES | LOW |
| Harbor.Ui.Framework.Abstractions/Diagnostics/DiagnosticsPanelLoggerProvider.cs | 26 | class DiagnosticsPanelLoggerProvider | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/DiagnosticsPanelLoggerProvider.cs | 50 | public ILogger CreateLogger(string categoryName) => | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/DiagnosticsPanelLoggerProvider.cs | 54 | public void Dispose() | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/DiagnosticsPanelLoggerProvider.cs | 68 | class DiagnosticsPanelLogger | YES | MED |
| Harbor.Ui.Framework.Abstractions/Diagnostics/DiagnosticsPanelLoggerProvider.cs | 88 | public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None; | YES | MED |
| Harbor.Ui.Framework.Abstractions/Diagnostics/DiagnosticsPanelLoggerProvider.cs | 115 | public void Dispose() { } | **NO** | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/DiagnosticsPanelLoggerProvider.cs | 125 | class DiagnosticsPanelLoggerExtensions | YES | MED |
| Harbor.Ui.Framework.Abstractions/Diagnostics/DiagnosticsPanelLoggerProvider.cs | 131 | public static ILoggerFactory AddDiagnosticsPanel(this ILoggerFactory factory, IDiagnosticsPanel panel) | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/IDiagnosticsPanel.cs | 31 | interface IDiagnosticsPanel | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/IDiagnosticsPanel.cs | 39 | public void Log(LogLevel level, string category, string message); | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/IDiagnosticsPanel.cs | 46 | public IReadOnlyList<DiagnosticEntry> GetRecent(int max = 100); | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/IDiagnosticsPanel.cs | 51 | public void Clear(); | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/InMemoryDiagnosticsPanel.cs | 20 | class InMemoryDiagnosticsPanel | YES | MED |
| Harbor.Ui.Framework.Abstractions/Diagnostics/InMemoryDiagnosticsPanel.cs | 59 | public void Log(LogLevel level, string category, string message) | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/InMemoryDiagnosticsPanel.cs | 78 | public IReadOnlyList<DiagnosticEntry> GetRecent(int max = 100) | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Diagnostics/InMemoryDiagnosticsPanel.cs | 102 | public void Clear() | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Navigation/IContentHost.cs | 12 | interface IContentHost | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Navigation/IShellChrome.cs | 7 | interface IShellChrome | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Navigation/IWorkspaceCommands.cs | 8 | interface IWorkspaceCommands | YES | HIGH |
| Harbor.Ui.Framework.Abstractions/Navigation/OverlayIds.cs | 10 | class OverlayIds | YES | MED |

## Project: Harbor.Ui.Framework.Projection

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 53 | class DefaultUiProjector | YES | MED |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 58 | public UiScreenModel Project(UiState state) | YES | MED |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 318 | ImmutableArray<ChatLine> Lines | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 319 | ImmutableArray<UiRenderedLine> BaseRendered | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 320 | ImmutableArray<UiBlock> BaseBlocks | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 323 | bool IsStreaming | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 324 | string? ThinkBuf | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 325 | string? TextBuf | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 326 | ImmutableArray<UiRenderedLine> TailRendered | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 327 | ImmutableArray<UiBlock> TailBlocks | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 334 | string? InputText | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 335 | bool IsAgentRunning | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 336 | bool ShouldQuit | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 337 | FocusMode Focus | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 338 | CostSnapshot Cost | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 339 | int TotalLines | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 340 | int ViewportLines | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 341 | int ScrollOffset | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/DefaultUiProjector.cs | 399 | public static ImmutableArray<UiRenderedLine> ExtractRenderedLines(UiScreenModel screen) | YES | HIGH |
| Harbor.Ui.Framework.Projection/Projection/IUiProjector.cs | 5 | interface IUiProjector | **NO** | HIGH |
| Harbor.Ui.Framework.Projection/Projection/IUiViewport.cs | 5 | interface IUiViewport | **NO** | HIGH |
| Harbor.Ui.Framework.Projection/Projection/StatusProjector.cs | 8 | class StatusProjector | **NO** | MED |
| Harbor.Ui.Framework.Projection/Projection/StatusProjector.cs | 10 | public static UiStatusBarModel ProjectStatusBar(UiState state) | **NO** | MED |
| Harbor.Ui.Framework.Projection/Projection/StatusProjector.cs | 74 | public static string ProjectFooter(UiState state) | **NO** | MED |
| Harbor.Ui.Framework.Projection/Projection/StyledSpan.cs | 5 | record struct | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/StyledSpan.cs | 15 | record struct | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiLineKind.cs | 3 | enum UiLineKind | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiRenderedLine.cs | 6 | record UiRenderedLine | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 6 | record UiHeaderModel | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 16 | record UiTranscriptModel | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 21 | record UiBlock | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 23 | record UiMessageBlock | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 29 | enum MessageRenderPhase | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 36 | enum ToolCallStatus | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 44 | enum UiSpanStyle | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 57 | record UiInputModel | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 63 | record UiStatusBarModel | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 66 | record UiStatusSegment | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 72 | enum Alignment | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Projection/UiScreenModel.cs | 79 | record UiScreenModel | **NO** | LOW |
| Harbor.Ui.Framework.Projection/Rendering/ChatStreamingPresenter.cs | 9 | class ChatStreamingPresenter | YES | MED |
| Harbor.Ui.Framework.Projection/Rendering/ChatStreamingPresenter.cs | 27 | public SessionStatus DeriveStatus(UiState state) | YES | MED |

## Project: Harbor.Ui.Framework.Reducers

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Ui.Framework.Reducers/AppReducer.cs | 25 | class AppReducer | YES | MED |
| Harbor.Ui.Framework.Reducers/AppReducer.cs | 30 | public static AppState Reduce(AgentEvent @event, AppState state) => @event switch | YES | HIGH |
| Harbor.Ui.Framework.Reducers/AppStore.cs | 10 | class AppStore | YES | HIGH |
| Harbor.Ui.Framework.Reducers/AppStore.cs | 18 | event EventHandler<AppState>? StateChanged | YES | MED |
| Harbor.Ui.Framework.Reducers/AppStore.cs | 23 | public void Dispatch(AgentEvent @event) | YES | HIGH |
| Harbor.Ui.Framework.Reducers/ChatViewReducer.cs | 21 | class ChatViewReducer | YES | MED |
| Harbor.Ui.Framework.Reducers/ChatViewReducer.cs | 27 | public static ChatViewState Reduce(AgentEvent @event, ChatViewState state) => @event switch | YES | HIGH |
| Harbor.Ui.Framework.Reducers/ChromeReducer.cs | 22 | class ChromeReducer | YES | MED |
| Harbor.Ui.Framework.Reducers/ChromeReducer.cs | 27 | public static ChromeViewState Reduce(AgentEvent @event, ChromeViewState state) => @event switch | YES | HIGH |
| Harbor.Ui.Framework.Reducers/SessionsReducer.cs | 23 | class SessionsReducer | YES | MED |
| Harbor.Ui.Framework.Reducers/SessionsReducer.cs | 29 | public static SessionsViewState Reduce(AgentEvent @event, SessionsViewState state) => @event switch | YES | HIGH |

## Project: Harbor.Ui.Framework.Services

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Ui.Framework.Services/EventBusAppStoreDispatcher.cs | 10 | class EventBusAppStoreDispatcher | YES | HIGH |
| Harbor.Ui.Framework.Services/EventBusAppStoreDispatcher.cs | 25 | public void Start() | YES | HIGH |
| Harbor.Ui.Framework.Services/EventBusAppStoreDispatcher.cs | 37 | public ValueTask DisposeAsync() | YES | HIGH |
| Harbor.Ui.Framework.Services/IRenderEngine.cs | 5 | interface IRenderEngine | **NO** | HIGH |
| Harbor.Ui.Framework.Services/Overlays/OverlayController.cs | 13 | class OverlayController | YES | MED |
| Harbor.Ui.Framework.Services/Overlays/OverlayController.cs | 27 | bool HasOverlay | **NO** | LOW |
| Harbor.Ui.Framework.Services/Overlays/OverlayController.cs | 29 | public void Register(string id, Action<bool> setter) | **NO** | HIGH |
| Harbor.Ui.Framework.Services/Overlays/OverlayController.cs | 35 | public void Open(string id) | **NO** | HIGH |
| Harbor.Ui.Framework.Services/Overlays/OverlayController.cs | 43 | public void Close(string id) | **NO** | HIGH |
| Harbor.Ui.Framework.Services/Overlays/OverlayController.cs | 50 | public bool CloseTop() | **NO** | HIGH |
| Harbor.Ui.Framework.Services/Overlays/OverlayController.cs | 65 | public void Dispose() | **NO** | HIGH |
| Harbor.Ui.Framework.Services/Services/GitService.cs | 9 | class GitService | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/GitService.cs | 19 | public GitSessionInfo GetGitStatus(string directory) | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/GitSessionInfo.cs | 6 | record GitSessionInfo | YES | LOW |
| Harbor.Ui.Framework.Services/Services/GitSessionInfo.cs | 9 | GitSessionInfo Empty | YES | LOW |
| Harbor.Ui.Framework.Services/Services/IDialogService.cs | 8 | interface IDialogService | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IDialogService.cs | 12 | public Task<bool> ConfirmAsync( | YES | MED |
| Harbor.Ui.Framework.Services/Services/IDialogService.cs | 21 | public Task<string?> PromptAsync( | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IDialogService.cs | 28 | public Task AlertAsync( | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IDispatcherAdapter.cs | 8 | interface IDispatcherAdapter | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IDispatcherAdapter.cs | 11 | public void Post(Action action); | YES | MED |
| Harbor.Ui.Framework.Services/Services/IDispatcherAdapter.cs | 17 | public void Bind(UiStore store); | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IDispatcherAdapter.cs | 20 | public void Unbind(UiStore store); | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IDispatcherAdapter.cs | 26 | event EventHandler<UiState>? StateChanged | YES | MED |
| Harbor.Ui.Framework.Services/Services/IFilePicker.cs | 8 | interface IFilePicker | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IFilePicker.cs | 15 | public Task<IReadOnlyList<string>> PickFilesAsync( | YES | MED |
| Harbor.Ui.Framework.Services/Services/IFilePicker.cs | 25 | public Task<string?> PickSaveFileAsync( | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IFilePicker.cs | 34 | public Task<string?> PickFolderAsync( | YES | MED |
| Harbor.Ui.Framework.Services/Services/IOverlayStack.cs | 11 | interface IOverlayStack | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IOverlayStack.cs | 34 | class OverlayStackService | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IOverlayStack.cs | 44 | event Action<string?>? Popped | **NO** | MED |
| Harbor.Ui.Framework.Services/Services/IOverlayStack.cs | 46 | public void Push(string id) | **NO** | MED |
| Harbor.Ui.Framework.Services/Services/IOverlayStack.cs | 54 | public string? PopTop() | **NO** | MED |
| Harbor.Ui.Framework.Services/Services/IThemeService.cs | 6 | interface IThemeService | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IThemeService.cs | 9 | string Current | YES | LOW |
| Harbor.Ui.Framework.Services/Services/IThemeService.cs | 12 | bool IsDark | YES | LOW |
| Harbor.Ui.Framework.Services/Services/IThemeService.cs | 15 | public void Apply(string theme); | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IThemeService.cs | 18 | public void ApplyDark(); | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IThemeService.cs | 21 | public void ApplyLight(); | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IThemeService.cs | 24 | public void Toggle(); | YES | MED |
| Harbor.Ui.Framework.Services/Services/IThemeService.cs | 27 | public void ApplyHds(string theme); | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IThemeService.cs | 30 | public void SetThemeVariant(bool isDark); | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IToastService.cs | 6 | interface IToastService | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/IToastService.cs | 9 | public void Show(string message, ToastKind kind = ToastKind.Info); | YES | MED |
| Harbor.Ui.Framework.Services/Services/IToastService.cs | 12 | event EventHandler<ToastNotification>? ToastAdded | YES | MED |
| Harbor.Ui.Framework.Services/Services/IToastService.cs | 16 | enum ToastKind | YES | LOW |
| Harbor.Ui.Framework.Services/Services/IToastService.cs | 25 | record ToastNotification | YES | LOW |
| Harbor.Ui.Framework.Services/Services/SessionStatusTracker.cs | 17 | class SessionStatusTracker | YES | MED |
| Harbor.Ui.Framework.Services/Services/SessionStatusTracker.cs | 38 | public SessionStatus Get(string sessionId) => | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/SessionStatusTracker.cs | 48 | public void Set(string sessionId, SessionStatus status) | YES | HIGH |
| Harbor.Ui.Framework.Services/Services/SessionStatusTracker.cs | 61 | public void NotifyMessageCount(string sessionId, int count) => MessageCountChanged?.Invoke(sessionId, count); | YES | HIGH |

## Project: Harbor.Ui.Framework.Sessions

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Ui.Framework.Sessions/Sessions/IChatViewBinder.cs | 9 | interface IChatViewBinder | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/IChatViewBinder.cs | 19 | public void Rebind(UiStore store); | YES | MED |
| Harbor.Ui.Framework.Sessions/Sessions/ISessionManager.cs | 9 | interface ISessionManager | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionContext.cs | 11 | class SessionContext | YES | MED |
| Harbor.Ui.Framework.Sessions/Sessions/SessionContext.cs | 20 | Session Session | YES | LOW |
| Harbor.Ui.Framework.Sessions/Sessions/SessionContext.cs | 23 | UiStore Store | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionContext.cs | 26 | SessionStatus Status | YES | LOW |
| Harbor.Ui.Framework.Sessions/Sessions/SessionContext.cs | 29 | string? GitBranch | YES | LOW |
| Harbor.Ui.Framework.Sessions/Sessions/SessionContext.cs | 32 | bool GitIsDirty | YES | LOW |
| Harbor.Ui.Framework.Sessions/Sessions/SessionContext.cs | 41 | bool StoreWasHydrated | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs | 30 | class SessionFactory | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs | 55 | public async Task<(string? ProviderId, string? ModelId)> ResolveProviderModelFromConfigAsync() | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs | 86 | public async Task<AgentDefinition> ResolveAgentDefinitionAsync(string? agentName, string? providerId, string? modelId) | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs | 108 | public async Task<Session?> CreateDefaultAsync() | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs | 145 | public async Task<Session?> CreateNewAsync( | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs | 176 | public async Task<Session?> CreateBranchAsync(Session source) | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionFactory.cs | 203 | public static (ChatRole role, string text) MessageToChatLine(AgentMessage msg) | YES | MED |
| Harbor.Ui.Framework.Sessions/Sessions/SessionGitTracker.cs | 12 | class SessionGitTracker | YES | MED |
| Harbor.Ui.Framework.Sessions/Sessions/SessionGitTracker.cs | 18 | public GitSessionInfo Get(string sessionId) | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionGitTracker.cs | 34 | public void Refresh(string sessionId, string directory, GitService? gitService) | YES | MED |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 46 | class SessionManager | YES | MED |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 101 | SessionContext? ActiveContext | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 133 | public SessionContext? GetContext(string sessionId) => | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 137 | public SessionStatus GetStatus(string sessionId) => _statusTracker.Get(sessionId); | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 140 | public void SetStatus(string sessionId, SessionStatus status) => | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 146 | public void NotifyMessageCount(string sessionId, int count) => | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 150 | public GitSessionInfo GetGitInfo(string sessionId) => | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 154 | public void RefreshGitInfo(string sessionId, string directory) => | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 162 | public async Task EnsureDefaultSessionAsync() | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 184 | public async Task RebindFromCommonConfigAsync() | YES | MED |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 221 | public async Task<Session?> NewSessionAsync(string? agentName = null, string? providerId = null, string? modelId = null,... | YES | MED |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 242 | public async Task<bool> OpenSessionAsync(string sessionId) | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 291 | public async Task<Session?> BranchActiveAsync() | YES | MED |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 305 | public async Task<bool> DeleteSessionAsync(string sessionId) | YES | HIGH |
| Harbor.Ui.Framework.Sessions/Sessions/SessionManager.cs | 337 | public async Task<bool> RenameSessionAsync(string sessionId, string newTitle) | YES | MED |
| Harbor.Ui.Framework.Sessions/Sessions/SessionSwitcher.cs | 27 | class SessionSwitcher | YES | MED |
| Harbor.Ui.Framework.Sessions/Sessions/SessionSwitcher.cs | 59 | public async Task<bool> OpenAsync(Session session, UiStore targetStore) | YES | HIGH |

## Project: Harbor.Ui.Framework.State

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Ui.Framework.State/AppState.cs | 23 | record AppState | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 28 | ImmutableArray<ChatLine> Lines | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 31 | ActiveMessage Active | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 34 | bool IsStreaming | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 37 | bool IsThinking | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 40 | string Status | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 43 | CostSnapshot Cost | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 46 | string Model | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 49 | string Provider | YES | HIGH |
| Harbor.Ui.Framework.State/AppState.cs | 52 | string AgentName | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 55 | bool IsAgentRunning | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 60 | bool WasRunning | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 63 | bool ShouldQuit | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 68 | InputModel Input | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 71 | FocusMode Focus | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 76 | int ScrollOffset | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 79 | int ViewportLines | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 82 | int TotalLines | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 108 | string? FocusedPanelId | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 111 | ImmutableArray<string> RegisteredPanelIds | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 117 | string ActiveDrawerTab | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 122 | string StreamingBuffer | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 125 | string ThinkingBuffer | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 131 | ChunkedBuffer PendingStreamText | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 137 | ChunkedBuffer PendingStreamThink | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 142 | ChromeState? Chrome | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 149 | record ChatState | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 152 | ImmutableArray<ChatLine> Lines | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 155 | bool IsStreaming | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 158 | bool IsThinking | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 161 | bool IsAgentRunning | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 164 | string StreamingBuffer | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 167 | string StatusMessage | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 170 | ImmutableArray<ToolCall> ToolCalls | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 173 | double PullProgress | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 176 | long PullOffset | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 179 | bool CanLoadOlder | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 182 | bool ShowPullIndicator | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 185 | double ContentScale | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 191 | record ChromeState | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 194 | SessionId? ActiveSessionId | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 197 | ImmutableStack<Route> NavigationStack | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 200 | Modal? ActiveModal | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 203 | ImmutableArray<Toast> Toasts | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 209 | record Route | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 213 | record Chat | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 216 | record Settings | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 219 | record AgentLog | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 222 | record ProviderPicker | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 225 | record Onboarding | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 231 | record Modal | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 237 | record Confirm | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 242 | record Alert | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 252 | record Toast | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 255 | enum ToastSeverity | YES | LOW |
| Harbor.Ui.Framework.State/AppState.cs | 269 | record ToolCall | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 16 | record ChatViewState | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 19 | ImmutableArray<ChatLineViewModel> Lines | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 22 | ImmutableArray<ToolCallViewModel> ToolCalls | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 25 | bool IsStreaming | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 28 | bool IsThinking | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 31 | bool IsAgentRunning | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 34 | string StreamingBuffer | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 40 | ChunkedBuffer PendingStreaming | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 43 | string StatusMessage | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 46 | double PullProgress | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 49 | double PullOffset | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 52 | bool CanLoadOlder | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 55 | bool ShowPullIndicator | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 58 | double ContentScale | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 67 | record ChatLineViewModel | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 70 | DateTime? TimestampUtc | YES | LOW |
| Harbor.Ui.Framework.State/ChatViewState.cs | 88 | record ToolCallViewModel | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 21 | record ChromeViewState | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 24 | SessionId? ActiveSessionId | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 27 | ImmutableStack<Route> NavigationStack | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 30 | Modal? ActiveModal | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 33 | ImmutableArray<Toast> Toasts | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 38 | public ChromeViewState PushRoute(Route route) => this with { NavigationStack = NavigationStack.Push(route) }; | YES | MED |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 43 | public ChromeViewState PopRoute() => this with { NavigationStack = NavigationStack.Pop() }; | YES | MED |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 48 | public ChromeViewState ShowModal(Modal modal) => this with { ActiveModal = modal }; | YES | MED |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 53 | public ChromeViewState DismissModal() => this with { ActiveModal = null }; | YES | MED |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 58 | public ChromeViewState AddToast(Toast toast) => this with { Toasts = Toasts.Add(toast) }; | YES | HIGH |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 63 | record Route | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 67 | record Chat | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 70 | record Settings | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 73 | record AgentLog | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 76 | record ProviderPicker | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 79 | record Onboarding | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 85 | record Modal | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 91 | record Confirm | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 96 | record Alert | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 106 | record Toast | YES | LOW |
| Harbor.Ui.Framework.State/ChromeViewState.cs | 109 | enum ToastSeverity | YES | LOW |
| Harbor.Ui.Framework.State/ChunkedBuffer.cs | 22 | class ChunkedBuffer | YES | MED |
| Harbor.Ui.Framework.State/ChunkedBuffer.cs | 37 | ImmutableStack<string> ChunksReversed | YES | LOW |
| Harbor.Ui.Framework.State/ChunkedBuffer.cs | 40 | int Length | YES | LOW |
| Harbor.Ui.Framework.State/ChunkedBuffer.cs | 49 | public ChunkedBuffer Append(string delta) | YES | MED |
| Harbor.Ui.Framework.State/ChunkedBuffer.cs | 60 | public string Materialize() | YES | MED |
| Harbor.Ui.Framework.State/Panels/IPanelProvider.cs | 32 | interface IPanelProvider | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/IPanelProvider.cs | 35 | string Id | YES | LOW |
| Harbor.Ui.Framework.State/Panels/IPanelProvider.cs | 38 | string Title | YES | LOW |
| Harbor.Ui.Framework.State/Panels/IPanelProvider.cs | 41 | TuiPanelPlacement DefaultPlacement | YES | LOW |
| Harbor.Ui.Framework.State/Panels/IPanelProvider.cs | 48 | int DefaultSize | YES | LOW |
| Harbor.Ui.Framework.State/Panels/IPanelProvider.cs | 60 | public object? Build(PanelContext ctx); | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/IPanelProvider.cs | 73 | public bool OnKey(UiKey key, PanelContext ctx); | YES | MED |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 12 | record struct | YES | LOW |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 17 | public IReadOnlyList<IPanelProvider> GetVisible() | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 32 | public IReadOnlyList<IPanelProvider> GetVisibleByPlacement(TuiPanelPlacement placement) | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 51 | public TuiPanelState GetState(string id) => | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 55 | public int GetSize(string id) => | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 82 | class PanelRegistry | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 102 | public Result Register(IPanelProvider panel) | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 134 | public Result Unregister(string id) | YES | MED |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 158 | public IPanelProvider? Get(string id) | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 172 | public PanelRegistryView View(UiState state) | YES | MED |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 185 | interface IPanelRegistry | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 189 | IReadOnlyList<IPanelProvider> All | YES | LOW |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 191 | public Result Register(IPanelProvider panel); | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 194 | public Result Unregister(string id); | YES | MED |
| Harbor.Ui.Framework.State/Panels/IPanelRegistry.cs | 197 | public IPanelProvider? Get(string id); | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/ITuiPanelPlugin.cs | 32 | interface ITuiPanelPlugin | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/ITuiPanelPlugin.cs | 41 | public void RegisterPanels(IPanelRegistry registry); | YES | HIGH |
| Harbor.Ui.Framework.State/Panels/PanelContext.cs | 12 | record PanelContext | YES | LOW |
| Harbor.Ui.Framework.State/Panels/TuiPanel.cs | 6 | enum TuiPanelPlacement | YES | LOW |
| Harbor.Ui.Framework.State/Panels/TuiPanel.cs | 32 | enum TuiPanelState | YES | LOW |
| Harbor.Ui.Framework.State/Panels/TuiPanel.cs | 58 | record TuiPanel | YES | LOW |
| Harbor.Ui.Framework.State/SessionsViewState.cs | 21 | record SessionsViewState | YES | LOW |
| Harbor.Ui.Framework.State/SessionsViewState.cs | 24 | ImmutableArray<SessionInfo> Sessions | YES | LOW |
| Harbor.Ui.Framework.State/SessionsViewState.cs | 27 | SessionId? ActiveSessionId | YES | LOW |
| Harbor.Ui.Framework.State/SessionsViewState.cs | 30 | bool IsLoading | YES | LOW |
| Harbor.Ui.Framework.State/SessionsViewState.cs | 41 | record SessionInfo | YES | LOW |
| Harbor.Ui.Framework.State/State/AsyncData.cs | 10 | record struct | YES | LOW |
| Harbor.Ui.Framework.State/State/AsyncData.cs | 17 | public AsyncData<T> ToLoading() => Status is AsyncStatus.Success | **NO** | HIGH |
| Harbor.Ui.Framework.State/State/AsyncData.cs | 21 | public static AsyncData<T> Success(T value) => new(AsyncStatus.Success, value); | **NO** | MED |
| Harbor.Ui.Framework.State/State/AsyncData.cs | 22 | public static AsyncData<T> None() => new(AsyncStatus.None); | **NO** | MED |
| Harbor.Ui.Framework.State/State/AsyncData.cs | 23 | public static AsyncData<T> Failed(string e) => new(AsyncStatus.Error, default, e); | **NO** | HIGH |
| Harbor.Ui.Framework.State/State/AsyncData.cs | 25 | public static AsyncData<T> From(Result<T> r) => r.IsSuccess ? Success(r.Value) : Failed(r.Error); | **NO** | MED |
| Harbor.Ui.Framework.State/State/AsyncData.cs | 32 | enum AsyncStatus | YES | LOW |
| Harbor.Ui.Framework.State/State/AsyncFeed.cs | 11 | class AsyncFeed | YES | MED |
| Harbor.Ui.Framework.State/State/AsyncFeed.cs | 18 | AsyncData<T> Current | **NO** | LOW |
| Harbor.Ui.Framework.State/State/AsyncFeed.cs | 19 | event Action<AsyncData<T>>? Changed | **NO** | MED |
| Harbor.Ui.Framework.State/State/AsyncFeed.cs | 31 | public async Task RefreshAsync() | **NO** | MED |
| Harbor.Ui.Framework.State/State/AsyncFeed.cs | 61 | public void Dispose() | **NO** | HIGH |
| Harbor.Ui.Framework.State/State/ChatAction.cs | 8 | enum ChatAction | YES | LOW |
| Harbor.Ui.Framework.State/State/ChatAction.cs | 56 | enum FocusMode | YES | LOW |
| Harbor.Ui.Framework.State/State/ChatAction.cs | 69 | class ChatCommands | YES | MED |
| Harbor.Ui.Framework.State/State/ChatKeyMap.cs | 8 | class ChatKeyMap | YES | MED |
| Harbor.Ui.Framework.State/State/ChatKeyMap.cs | 61 | public ChatAction Resolve(UiKey key) | YES | HIGH |
| Harbor.Ui.Framework.State/State/ChatKeyMap.cs | 75 | public Entry Get(ChatAction action) => _byAction[action]; | YES | HIGH |
| Harbor.Ui.Framework.State/State/ChatKeyMap.cs | 78 | record struct | YES | LOW |
| Harbor.Ui.Framework.State/State/ChatKeyMap.cs | 80 | public bool Matches(UiKey key) | **NO** | MED |
| Harbor.Ui.Framework.State/State/ChatKeyMap.cs | 85 | record Entry | YES | LOW |
| Harbor.Ui.Framework.State/State/InputModel.cs | 9 | record InputModel | YES | LOW |
| Harbor.Ui.Framework.State/State/InputModel.cs | 18 | public InputModel Append(char c) => | **NO** | MED |
| Harbor.Ui.Framework.State/State/InputModel.cs | 21 | public InputModel Backspace() => | **NO** | MED |
| Harbor.Ui.Framework.State/State/InputModel.cs | 24 | public InputModel Clear() => this with { Text = string.Empty, HistoryIndex = -1 }; | **NO** | HIGH |
| Harbor.Ui.Framework.State/State/InputModel.cs | 26 | public InputModel SetText(string text) => this with { Text = text, HistoryIndex = -1 }; | **NO** | HIGH |
| Harbor.Ui.Framework.State/State/InputModel.cs | 29 | public (InputModel Next, string? Submitted) Consume() | YES | MED |
| Harbor.Ui.Framework.State/State/InputModel.cs | 37 | public InputModel NavigateUp() | **NO** | MED |
| Harbor.Ui.Framework.State/State/InputModel.cs | 44 | public InputModel NavigateDown() | **NO** | HIGH |
| Harbor.Ui.Framework.State/State/InputModel.cs | 59 | record InputMsg | YES | LOW |
| Harbor.Ui.Framework.State/State/InputModel.cs | 63 | public static InputModel Update(InputModel state, InputMsg msg) => msg switch | YES | MED |
| Harbor.Ui.Framework.State/State/InputModel.cs | 82 | record Char | **NO** | LOW |
| Harbor.Ui.Framework.State/State/InputModel.cs | 84 | record Backspace | **NO** | LOW |
| Harbor.Ui.Framework.State/State/InputModel.cs | 86 | record Clear | **NO** | LOW |
| Harbor.Ui.Framework.State/State/InputModel.cs | 88 | record HistoryUp | **NO** | LOW |
| Harbor.Ui.Framework.State/State/InputModel.cs | 90 | record HistoryDown | **NO** | LOW |
| Harbor.Ui.Framework.State/State/InputModel.cs | 92 | record Autocomplete | **NO** | LOW |
| Harbor.Ui.Framework.State/State/InputModel.cs | 94 | record Submit | **NO** | LOW |
| Harbor.Ui.Framework.State/State/ShellStatus.cs | 4 | class ShellStatus | **NO** | MED |
| Harbor.Ui.Framework.State/State/TuiEffectHost.cs | 26 | class TuiEffectHost | YES | HIGH |
| Harbor.Ui.Framework.State/State/TuiEffectHost.cs | 48 | public void RebindStore(UiStore newStore) | **NO** | MED |
| Harbor.Ui.Framework.State/State/TuiEffectHost.cs | 57 | public void Run(TuiEffect effect) | **NO** | HIGH |
| Harbor.Ui.Framework.State/State/UiKey.cs | 6 | enum UiKeyCode | YES | LOW |
| Harbor.Ui.Framework.State/State/UiKey.cs | 46 | enum KeyModifierSet | YES | LOW |
| Harbor.Ui.Framework.State/State/UiKey.cs | 62 | record struct | YES | LOW |
| Harbor.Ui.Framework.State/State/UiKey.cs | 66 | public bool Has(KeyModifierSet mod) => Mods.HasFlag(mod); | **NO** | MED |
| Harbor.Ui.Framework.State/State/UiKey.cs | 68 | public static UiKey ForChar(char c, KeyModifierSet mods = KeyModifierSet.None) | **NO** | MED |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 11 | record UiMsg | YES | LOW |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 15 | record Agent | YES | LOW |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 20 | record KeyInput | YES | LOW |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 24 | record Viewport | YES | LOW |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 28 | record HistoryMeasured | YES | LOW |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 36 | record TogglePanel | YES | LOW |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 44 | record FocusPanel | YES | LOW |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 50 | record CyclePanelFocus | YES | LOW |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 59 | record ResizePanel | YES | LOW |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 68 | record ScrollResetToTail | YES | LOW |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 76 | record ScrollClamp | YES | LOW |
| Harbor.Ui.Framework.State/State/UiMsg.cs | 88 | record SeedPanels | YES | LOW |
| Harbor.Ui.Framework.State/State/UiReducer.cs | 23 | class UiReducer | YES | MED |
| Harbor.Ui.Framework.State/State/UiReducer.cs | 42 | public static UiState Reduce(UiState state, AgentEvent @event) => @event switch | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiReducer.cs | 243 | public static (UiState State, TuiEffect Effect) Update(UiState state, UiMsg msg) => msg switch | YES | MED |
| Harbor.Ui.Framework.State/State/UiReducer.cs | 270 | public static UiState TogglePanel(UiState state, string id) | YES | MED |
| Harbor.Ui.Framework.State/State/UiReducer.cs | 287 | public static UiState FocusPanel(UiState state, string? id) | YES | MED |
| Harbor.Ui.Framework.State/State/UiReducer.cs | 316 | public static UiState CycleFocus(UiState state) | YES | MED |
| Harbor.Ui.Framework.State/State/UiReducer.cs | 333 | public static UiState ResizePanel(UiState state, string id, int delta) | YES | MED |
| Harbor.Ui.Framework.State/State/UiState.cs | 11 | enum ChatRole | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 31 | record struct | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 37 | record ActiveMessage | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 50 | record struct | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 68 | record UiState | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 71 | ImmutableArray<ChatLine> Lines | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 74 | ActiveMessage Active | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 80 | ChunkedBuffer PendingStreamText | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 86 | ChunkedBuffer PendingStreamThink | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 89 | bool IsStreaming | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 92 | string Status | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 95 | CostSnapshot Cost | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 98 | string Model | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 101 | string Provider | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiState.cs | 104 | string AgentName | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 107 | bool IsAgentRunning | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 115 | bool WasRunning | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 118 | bool ShouldQuit | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 121 | InputModel Input | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 124 | FocusMode Focus | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 130 | int ScrollOffset | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 133 | int ViewportLines | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 136 | int TotalLines | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 170 | string? FocusedPanelId | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 177 | ImmutableArray<string> RegisteredPanelIds | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 181 | ImmutableArray<SessionInfo> Sessions | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 184 | SessionId? ActiveSessionId | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 187 | bool IsLoading | YES | LOW |
| Harbor.Ui.Framework.State/State/UiState.cs | 193 | public UiState AddLine(ChatRole role, string text, string? toolCallId = null) => | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiState.cs | 197 | public UiState SetLine(int index, ChatRole role, string text) | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiState.cs | 207 | public UiState SetInput(InputModel input) => this with { Input = input }; | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiState.cs | 210 | public UiState SetFocus(FocusMode focus) => this with { Focus = focus }; | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiState.cs | 217 | public UiState ClearTranscript() => this with | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiState.cs | 232 | public UiState SetScroll(int offset) | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiStore.cs | 8 | record TuiEffect | YES | LOW |
| Harbor.Ui.Framework.State/State/UiStore.cs | 11 | record None | YES | LOW |
| Harbor.Ui.Framework.State/State/UiStore.cs | 15 | record PromptAgent | YES | LOW |
| Harbor.Ui.Framework.State/State/UiStore.cs | 19 | record RunSlash | YES | LOW |
| Harbor.Ui.Framework.State/State/UiStore.cs | 22 | record AbortAgent | YES | LOW |
| Harbor.Ui.Framework.State/State/UiStore.cs | 25 | record QuitApp | YES | LOW |
| Harbor.Ui.Framework.State/State/UiStore.cs | 32 | interface ITuiEffectRunner | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiStore.cs | 35 | public void Run(TuiEffect effect); | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiStore.cs | 44 | class UiStore | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiStore.cs | 72 | event EventHandler<UiStateChangedEventArgs>? Changed | YES | MED |
| Harbor.Ui.Framework.State/State/UiStore.cs | 78 | public void Dispatch(AgentEvent @event) | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiStore.cs | 100 | public TuiEffect Dispatch(UiMsg msg) | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiStore.cs | 128 | internal void Transition(Func<UiState, UiState> reducer) | YES | LOW |
| Harbor.Ui.Framework.State/State/UiStore.cs | 144 | public void BindSession(string model, string provider, string agentName) => Transition(s => s with { Model = model, Prov... | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiStore.cs | 147 | public void Reset() => Transition(_ => new UiState()); | YES | HIGH |
| Harbor.Ui.Framework.State/State/UiStore.cs | 151 | class UiStateChangedEventArgs | YES | MED |
| Harbor.Ui.Framework.State/State/UiStore.cs | 159 | UiState State | YES | HIGH |
| Harbor.Ui.Framework.State/StreamingSync.cs | 23 | class StreamingSync | YES | MED |
| Harbor.Ui.Framework.State/StreamingSync.cs | 40 | public static bool ShouldFlush(int syncedLength, int pendingLength) | YES | HIGH |
| Harbor.Ui.Framework.State/StreamingSync.cs | 54 | public static string Concat(string prefix, ChunkedBuffer pending) | YES | MED |

## Project: Harbor.Ui.Framework.ViewModels

| File | Line | Member | Has XML doc? | Priority |
|------|------|--------|--------------|----------|
| Harbor.Ui.Framework.ViewModels/Animation/CostAnimator.cs | 11 | class CostAnimator | YES | MED |
| Harbor.Ui.Framework.ViewModels/Animation/CostAnimator.cs | 28 | decimal DisplayCost | **NO** | LOW |
| Harbor.Ui.Framework.ViewModels/Animation/CostAnimator.cs | 29 | bool IsRunning | **NO** | LOW |
| Harbor.Ui.Framework.ViewModels/Animation/CostAnimator.cs | 32 | event Action? Tick | **NO** | MED |
| Harbor.Ui.Framework.ViewModels/Animation/CostAnimator.cs | 34 | public void Start(decimal baseCost) | **NO** | HIGH |
| Harbor.Ui.Framework.ViewModels/Animation/CostAnimator.cs | 42 | public void Stop() | **NO** | HIGH |
| Harbor.Ui.Framework.ViewModels/Animation/CostAnimator.cs | 48 | public void Advance() | **NO** | MED |
| Harbor.Ui.Framework.ViewModels/Animation/CostAnimator.cs | 61 | public void Dispose() | **NO** | HIGH |
| Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs | 28 | class StatusMappers | YES | MED |
| Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs | 37 | public static string StatusToBrushKey(string? statusText) => statusText switch | YES | MED |
| Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs | 49 | public static string ToolCallStatusToBrushKey(ToolCallStatus status) => status switch | YES | HIGH |
| Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs | 61 | public static string ToolCallStatusToPill(ToolCallStatus status) => status switch | YES | HIGH |
| Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs | 73 | public static string SessionStatusToText(SessionStatus status) => status switch | YES | MED |
| Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs | 86 | public static string SessionStatusToBrushKey(SessionStatus status) => status switch | YES | MED |
| Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs | 100 | public static string DurationToText(TimeSpan duration) => duration.TotalMilliseconds < 1 | YES | MED |
| Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs | 112 | public static string TimeAgo(DateTime? utc) | YES | MED |
| Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs | 128 | public static string TokensToCompact(long tokens) | YES | MED |
| Harbor.Ui.Framework.ViewModels/Converters/StatusMappers.cs | 140 | public static string CostToUsd(decimal costUsd) => | YES | MED |
| Harbor.Ui.Framework.ViewModels/ViewModels/ChatLineViewModel.cs | 31 | record ChatLineViewModel | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/ChatLineViewModel.cs | 41 | DateTime? TimestampUtc | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/DiffViewModel.cs | 12 | class DiffViewModel | YES | MED |
| Harbor.Ui.Framework.ViewModels/ViewModels/DiffViewModel.cs | 35 | ObservableCollection<DiffRowViewModel> Rows | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/DiffViewModel.cs | 61 | record DiffRowViewModel | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionItemViewModel.cs | 9 | class SessionItemViewModel | YES | MED |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionItemViewModel.cs | 43 | string Id | **NO** | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionItemViewModel.cs | 44 | string Title | **NO** | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionItemViewModel.cs | 45 | string Agent | **NO** | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionItemViewModel.cs | 46 | string Model | **NO** | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionItemViewModel.cs | 47 | string ProviderId | **NO** | HIGH |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionItemViewModel.cs | 48 | DateTimeOffset UpdatedAt | **NO** | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionRowViewModel.cs | 23 | class SessionRowViewModel | YES | MED |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionRowViewModel.cs | 74 | string Id | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionRowViewModel.cs | 77 | string Title | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionRowViewModel.cs | 80 | string Agent | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionRowViewModel.cs | 83 | string ModelName | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionRowViewModel.cs | 86 | string ProviderId | YES | HIGH |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionRowViewModel.cs | 89 | DateTimeOffset UpdatedAt | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionRowViewModel.cs | 92 | string Workdir | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionRowViewModel.cs | 95 | string Mode | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/SessionRowViewModel.cs | 98 | decimal? CostTotal | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/StoreSubscriberViewModel.cs | 9 | class StoreSubscriberViewModel | **NO** | HIGH |
| Harbor.Ui.Framework.ViewModels/ViewModels/StoreSubscriberViewModel.cs | 35 | public void Apply(UiState s) | **NO** | HIGH |
| Harbor.Ui.Framework.ViewModels/ViewModels/StoreSubscriberViewModel.cs | 44 | public void Reset() => _has = false; | **NO** | HIGH |
| Harbor.Ui.Framework.ViewModels/ViewModels/StoreSubscriberViewModel.cs | 93 | public virtual void Dispose() | **NO** | HIGH |
| Harbor.Ui.Framework.ViewModels/ViewModels/TokenUsageViewModel.cs | 21 | class TokenUsageViewModel | YES | MED |
| Harbor.Ui.Framework.ViewModels/ViewModels/TokenUsageViewModel.cs | 44 | ObservableCollection<TokenUsageBarViewModel> Bars | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/TokenUsageViewModel.cs | 50 | ObservableCollection<double> RecentOutputTokens | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/TokenUsageViewModel.cs | 62 | public void RecordUsage(UiState state) | YES | MED |
| Harbor.Ui.Framework.ViewModels/ViewModels/TokenUsageViewModel.cs | 116 | public void Reset() | YES | HIGH |
| Harbor.Ui.Framework.ViewModels/ViewModels/TokenUsageViewModel.cs | 136 | public void Clear() => Reset(); | YES | HIGH |
| Harbor.Ui.Framework.ViewModels/ViewModels/TokenUsageViewModel.cs | 140 | record TokenUsageBarViewModel | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/ToolCallStatus.cs | 8 | enum ToolCallStatus | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/ToolCallViewModel.cs | 33 | class ToolCallViewModel | YES | HIGH |
| Harbor.Ui.Framework.ViewModels/ViewModels/ToolCallViewModel.cs | 73 | string Id | YES | LOW |
| Harbor.Ui.Framework.ViewModels/ViewModels/ToolCallViewModel.cs | 115 | public void Complete(ToolCallStatus status, string resultPreview, TimeSpan duration) | YES | HIGH |
