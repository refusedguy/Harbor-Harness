using System.Text.Json.Serialization;

namespace Harbor.Providers.OpenAiCompatible;

/// <summary>
///     AOT-safe JsonSerializerContext for the OpenAI-compatible provider config
///     types. Used by ProviderConfig.LoadFromFile and ProviderRegistration
///     to avoid IL2026/IL3050 warnings when trimming/NativeAOT is enabled.
/// </summary>
[JsonSerializable(typeof(ProviderConfig))]
[JsonSerializable(typeof(ModelMapping))]
[JsonSerializable(typeof(ModelInfo))]
internal sealed partial class OpenAiCompatibleJsonContext : JsonSerializerContext
{
}
