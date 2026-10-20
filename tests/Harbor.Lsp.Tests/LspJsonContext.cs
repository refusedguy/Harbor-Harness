// LspJsonContext.cs — the source-generated context for the LSP test wire (#1822).
//
// WHY IT IS PROJECT-LOCAL
// -----------------------
// The frames this project writes are the JSON-RPC frames of the Language
// Server Protocol, and the payloads are this project's own: a fake server's
// responses, a notification envelope, and the `initialize` parameters. They
// belong to the protocol this project fakes, not to the shared kit, and the
// shapes the fake server can carry (`result` is whatever the test's
// responder returned) are LSP-specific.
//
// THE OPTIONS
// -----------
// `DefaultIgnoreCondition = WhenWritingNull` is not decoration: the fake
// server writes `["params"] = parameters`, and a notification without
// parameters must OMIT the key rather than write `null`. The reflection path
// had that rule on its options object; a source-generated context carries it
// on the context instead, so the key is still omitted.
//
// `result` and `params` are `object?` on the wire, and their runtime types
// are whatever the test handed over — a dictionary, a string, an int, an
// empty `object[]`. The source generator's object converter resolves those
// runtime types THROUGH this context, so every type that can appear is
// registered here. A runtime type that is not registered is a loud
// `InvalidOperationException`, not a silently empty object.

using System.Text.Json.Serialization;

namespace Harbor.Lsp.Tests;

/// <summary>     The <c>initialize</c> parameters the client sends. </summary>
public sealed record LspInitializeParams(
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("rootUri")] string RootUri);

/// <summary>     The answer frame the eager pipe writes. </summary>
public sealed record LspResponseFrame(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("result")] LspResponseResult Result);

/// <summary>     The <c>result</c> object of the answer frame. </summary>
public sealed record LspResponseResult(
    [property: JsonPropertyName("ok")] bool Ok);

/// <summary>     The notification frame the eager pipe writes. </summary>
public sealed record LspNotificationFrame(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] LspEmptyParams Params);

/// <summary>     The empty parameter object of the notification frame. </summary>
public sealed record LspEmptyParams();

/// <summary>
///     Source-generated metadata for the JSON-RPC frames this project writes.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(string), TypeInfoPropertyName = "String")]
[JsonSerializable(typeof(int), TypeInfoPropertyName = "Int")]
[JsonSerializable(typeof(bool), TypeInfoPropertyName = "Bool")]
[JsonSerializable(typeof(object[]), TypeInfoPropertyName = "ObjectArray")]
[JsonSerializable(typeof(Dictionary<string, object?>), TypeInfoPropertyName = "DictionaryStringObject")]
[JsonSerializable(typeof(LspInitializeParams), TypeInfoPropertyName = "LspInitializeParams")]
[JsonSerializable(typeof(LspResponseFrame), TypeInfoPropertyName = "LspResponseFrame")]
[JsonSerializable(typeof(LspResponseResult), TypeInfoPropertyName = "LspResponseResult")]
[JsonSerializable(typeof(LspNotificationFrame), TypeInfoPropertyName = "LspNotificationFrame")]
[JsonSerializable(typeof(LspEmptyParams), TypeInfoPropertyName = "LspEmptyParams")]
internal sealed partial class LspJsonContext : JsonSerializerContext
{
}
