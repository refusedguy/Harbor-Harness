using Microsoft.Extensions.Logging;
namespace Harbor.Abstractions.Tools;

public interface IToolFactory
{
    public ITool CreateTool(ILoggerFactory loggerFactory);
}
