namespace Harbor.Application.Hooks;

/// <summary>
///     AOT-safe serializer context for the hook config, payload, and verdict
///     DTOs. All hook JSON goes through here — no reflection fallback, so the
///     trimmer keeps every member the hooks surface touches.
/// </summary>
[JsonSerializable(typeof(HookFileConfig))]
[JsonSerializable(typeof(HookPayload))]
[JsonSerializable(typeof(HookVerdictDto))]
internal sealed partial class HookJsonContext : JsonSerializerContext
{
}
