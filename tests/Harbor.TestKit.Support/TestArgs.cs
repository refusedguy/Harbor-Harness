using System.Text.Json;

namespace Harbor.TestKit;

/// <summary>
///     The JSON argument elements the tool tests hand to a tool.
/// </summary>
/// <remarks>
///     <para>
///         Every tool takes its arguments as a <see cref="JsonElement" />, and
///         every suite that calls a tool had to build one. The four builders here
///         are the four ways the tree builds one: from key/value pairs, from a
///         JSON string, from an arbitrary payload, and as the empty object.
///     </para>
///     <para>
///         They are four methods and not one with a flag because they are four
///         different shapes, and an overload set that hides the difference behind
///         <c>object</c> binds differently: a call with a tuple argument would
///         pick an <c>object</c> parameter over a tuple one and quietly change
///         what the tool receives.
///     </para>
/// </remarks>
public static class TestArgs
{
    public static JsonElement FromPairs(params (string key, string value)[] pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach ((string k, string v) in pairs)
            dict[k] = v;
        return JsonDocument.Parse(JsonSerializer.Serialize(dict, TestJsonContext.Default.DictionaryStringObject)).RootElement.Clone();
    }

    public static JsonElement FromJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static JsonElement FromPayload<T>(T payload) =>
        JsonDocument.Parse(TestJson.Serialize(payload)).RootElement.Clone();

    public static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
}
