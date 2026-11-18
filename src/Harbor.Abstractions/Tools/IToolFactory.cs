using Microsoft.Extensions.Logging;

namespace Harbor.Abstractions.Tools;

/// <summary>
///     Creates the tool instances a registry hands to the agent.
/// </summary>
public interface IToolFactory
{
    /// <summary>
    ///     Creates a tool using the supplied logger factory.
    /// </summary>
    /// <param name="loggerFactory">The logger factory the tool uses for its diagnostics.</param>
    /// <returns>The constructed tool.</returns>
    ITool CreateTool(ILoggerFactory loggerFactory);
}
