using Microsoft.Extensions.Logging;

namespace Harbor.Abstractions.Tools;

public interface IToolFactory
{
    ITool CreateTool(ILoggerFactory loggerFactory);
}
