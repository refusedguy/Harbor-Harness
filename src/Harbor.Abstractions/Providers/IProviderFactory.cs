using Harbor.Abstractions.Models.Identifiers;
using Microsoft.Extensions.Logging;

namespace Harbor.Abstractions.Providers;

public interface IProviderFactory
{
    ProviderId ProviderId { get; }
    ILlmClient CreateClient(ILoggerFactory loggerFactory);
}
