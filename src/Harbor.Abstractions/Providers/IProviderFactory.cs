using Harbor.Abstractions.Models.Identifiers;
using Microsoft.Extensions.Logging;
namespace Harbor.Abstractions.Providers;

public interface IProviderFactory
{
    public ProviderId ProviderId { get; }
    public ILlmClient CreateClient(ILoggerFactory loggerFactory);
}
