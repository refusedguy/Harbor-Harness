namespace Harbor.Application.Agents;

/// <summary>
///     Pure tool-table helpers extracted from <see cref="AgentLoop" /> ([G4]):
///     sizing a <c>ToolDefinition</c> array without LINQ allocations.
///     Stateless and side-effect free.
/// </summary>
internal static class ToolTableBuilder
{
    /// <summary>
    ///     Build the ToolDefinition array directly, avoiding the LINQ Select().ToList() allocation
    ///     (which allocates a delegate + iterator + List).
    /// </summary>
    internal static ToolDefinition[] BuildToolDefinitions(IReadOnlyList<ToolDescriptor> tools)
    {
        if (tools.Count == 0)
        {
            return Array.Empty<ToolDefinition>();
        }

        var result = new ToolDefinition[tools.Count];
        for (int i = 0; i < tools.Count; i++)
        {
            var t = tools[i];
            result[i] = new ToolDefinition(t.Name.Value, t.Description, t.Schema);
        }
        return result;
    }
}
