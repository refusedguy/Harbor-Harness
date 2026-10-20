// TestJsonContext.cs — the test tree's source-generated JSON context (#1822).
//
// WHY THIS FILE EXISTS
// --------------------
// The product side of System.Text.Json already rides source generation: 21
// `JsonSerializerContext`s live under `src/` (#1565, #1779). The TEST side did
// not. Measured on `origin/dev` (this branch's base), `tests/` still carried
// 62 `JsonSerializer.Serialize/Deserialize/SerializeToElement/SerializeToUtf8Bytes`
// calls that went through the REFLECTION resolver, while 22 sibling calls in
// the same files already rode `JsonlCodecContext.Default.*`,
// `RemoteJsonContext.Default.*` or `McpJsonSerializerContext.Default.*`.
//
// The cost of the reflection path is not only CPU. A reflection call is not
// checked at compile time: a misspelled member name (`toolCallId` for
// `tool_call_id`) compiles, serializes to a DIFFERENT key, and the test then
// asserts on the wrong field and stays green. The source-generated path
// cannot do that: the type is named, the members are named, the compiler
// sees them.
//
// WHY IT LIVES IN `Harbor.TestKit.Support` AND NOWHERE ELSE
// --------------------------------------------------------
// The TestKit family is being split (#1889) into a core kit plus UI-scoped
// siblings. A JSON context must not decide that split, and it must not drag
// UI assemblies into the projects that only want a serializer:
//
//   * `Harbor.TestKit` (the core) reaches `Harbor.Ui.Framework.{Services,
//     State,ViewModels}` and `Harbor.Desktop.Abstractions` — four product
//     assemblies every consumer pays for.
//   * `Harbor.TestKit.Tui` reaches `Harbor.Tui.CellForge.Engine`.
//   * `Harbor.TestKit.Support` reaches `Harbor.Abstractions` only, so it is
//     CLOSURE-NEUTRAL: a consumer that takes this reference gains zero
//     product assemblies.
//
// The context needs nothing but BCL types and the test payloads declared
// beside it, so the closure-neutral sibling is the only home that costs a
// consumer nothing. A project that needs a payload type of its own (a mock
// server's wire shape, a protocol frame) declares a context of its own next
// to that shape — the source generator cannot see another assembly's
// internal types, so a shared context could not register them anyway.
//
// THE OPTIONS ARE THE PRODUCT'S
// -----------------------------
// The context carries camelCase, because that is the policy the PRODUCT's
// contexts carry (`JsonlCodecContext` says so in its own header: a
// context-level policy is what makes the write path and the read path agree
// on the same camelCase spelling). A test that serializes through a
// differently-spelled context than the product would prove nothing about the
// product.
//
// The payloads below name every member explicitly with `[JsonPropertyName]`,
// so the naming policy does not reach them: the emitted bytes are
// `{"path":...}`, not `{"Path":...}`.

using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;

namespace Harbor.TestKit;

/// <summary>
///     The source-generated <see cref="JsonSerializerContext" /> the test tree
///     serializes through.
/// </summary>
/// <remarks>
///     <para>
///         Every call site names a member of this context, so the type under
///         test is checked at compile time and the emitted JSON does not
///         depend on a runtime resolver. The drift control that keeps it that
///         way is <c>tools/check-test-json-reflection.py</c>.
///     </para>
///     <para>
///         The payloads this context registers are the TOOL-ARGUMENT shapes —
///         the JSON a tool receives. They live in
///         <see cref="ToolPathArgs" /> and its siblings, and each one names
///         every member explicitly so the wire shape cannot drift with a
///         naming policy.
///     </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(string), TypeInfoPropertyName = "String")]
[JsonSerializable(typeof(int), TypeInfoPropertyName = "Int32")]
[JsonSerializable(typeof(bool), TypeInfoPropertyName = "Boolean")]
[JsonSerializable(typeof(JsonElement), TypeInfoPropertyName = "JsonElement")]
[JsonSerializable(typeof(Usage), TypeInfoPropertyName = "Usage")]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "DictionaryStringString")]
[JsonSerializable(typeof(Dictionary<string, object?>), TypeInfoPropertyName = "DictionaryStringObject")]
[JsonSerializable(typeof(SortedDictionary<string, string>), TypeInfoPropertyName = "SortedDictionaryStringString")]
[JsonSerializable(typeof(ToolPathArgs), TypeInfoPropertyName = "ToolPathArgs")]
[JsonSerializable(typeof(ToolPathContentArgs), TypeInfoPropertyName = "ToolPathContentArgs")]
[JsonSerializable(typeof(ToolPathEditArgs), TypeInfoPropertyName = "ToolPathEditArgs")]
[JsonSerializable(typeof(ToolPathContentVersionArgs), TypeInfoPropertyName = "ToolPathContentVersionArgs")]
[JsonSerializable(typeof(ToolPathEditVersionArgs), TypeInfoPropertyName = "ToolPathEditVersionArgs")]
[JsonSerializable(typeof(ToolPathLimitArgs), TypeInfoPropertyName = "ToolPathLimitArgs")]
[JsonSerializable(typeof(ToolPathPatchArgs), TypeInfoPropertyName = "ToolPathPatchArgs")]
[JsonSerializable(typeof(ToolCommandArgs), TypeInfoPropertyName = "ToolCommandArgs")]
[JsonSerializable(typeof(ToolPatternPathArgs), TypeInfoPropertyName = "ToolPatternPathArgs")]
[JsonSerializable(typeof(ToolInputArgs), TypeInfoPropertyName = "ToolInputArgs")]
[JsonSerializable(typeof(AppConfigDocument), TypeInfoPropertyName = "AppConfigDocument")]
public sealed partial class TestJsonContext : JsonSerializerContext
{
}
