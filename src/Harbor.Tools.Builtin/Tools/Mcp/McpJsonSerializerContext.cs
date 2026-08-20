using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbor.Tools.Mcp;

[JsonSerializable(typeof(JsonElement))]
public sealed partial class McpJsonSerializerContext : JsonSerializerContext
{
}
