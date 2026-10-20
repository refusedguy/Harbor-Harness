using Harbor.Abstractions.Sessions;

namespace Harbor.TestKit;

/// <summary>Stub system prompt builder returning a fixed string.</summary>
public sealed class StubSystemPromptBuilder : ISystemPromptBuilder
{
    public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
        => Task.FromResult("stub-system-prompt");
}
