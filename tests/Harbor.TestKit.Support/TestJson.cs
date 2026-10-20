// TestJson.cs — the one place the test tree serializes a value whose type is
// only known at run time (#1822).
//
// WHY A HELPER AT ALL
// -------------------
// Most call sites name their type: `JsonSerializer.Serialize(payload,
// TestJsonContext.Default.ToolPathArgs)`. Four of them cannot — a helper that
// accepts SEVERAL argument shapes (`Args(new { path, content })` and
// `Args(new { path })` in the same file) would need one overload per shape.
//
// This helper is that one place. It resolves the type through
// `TestJsonContext`, so the type still has to be REGISTERED — an unregistered
// type is a loud `InvalidOperationException`, not a silent fallback to
// reflection.
//
// It is deliberately NOT a generic escape hatch. The lookup goes through the
// generated context and nowhere else: there is no
// `DefaultJsonTypeInfoResolver` behind it, so a type the context does not
// know cannot be serialized here at all.

using System.Text.Json;

namespace Harbor.TestKit;

/// <summary>
///     Serializes a value through <see cref="TestJsonContext" /> when the
///     concrete type is not known at the call site.
/// </summary>
public static class TestJson
{
    /// <summary>
    ///     Serializes <paramref name="value" /> through the source-generated
    ///     context.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The value's type is not registered in <see cref="TestJsonContext" />.
    /// </exception>
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, TestJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw NotRegistered<T>());

    /// <summary>
    ///     The refusal for a type the context does not know. It names the type
    ///     because "the JSON came out wrong" is the symptom; "this type was
    ///     never registered" is the cause.
    /// </summary>
    private static InvalidOperationException NotRegistered<T>() =>
        new($"{typeof(T).Name} is not registered in {nameof(TestJsonContext)}. "
            + "Add a [JsonSerializable] for it — an unregistered type cannot be "
            + "serialized without reflection, which is what this kit keeps out of the test tree.");
}
