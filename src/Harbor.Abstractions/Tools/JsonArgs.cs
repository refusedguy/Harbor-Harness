namespace Harbor.Abstractions.Tools;

/// <summary>
///     Maybe-style JSON argument readers shared by builtin tools (ROP-A П.12):
///     the TryGetProperty-ternary that LsTool owned privately, repeated 83
///     times across 17 files, lifted next to <see cref="ITool" />. Optional
///     arguments read as nullable; required ones via
///     <see cref="RequireString" /> / <see cref="RequireInt" />.
/// </summary>
public static class JsonArgs
{
    /// <summary>String property or null when absent / not a string.</summary>
    public static string? GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>Int32 property or null when absent / not a number.</summary>
    public static int? GetInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number &&
        el.TryGetInt32(out int n)
            ? n
            : null;

    /// <summary>
    ///     Int32 property clamped to <paramref name="min" />..<paramref name="max" />;
    ///     <paramref name="defaultValue" /> when absent / not a number (ROP-A zone-1,
    ///     replaces the GetInt-ternary-Math.Clamp copy pasted across builtin tools).
    /// </summary>
    public static int GetClampedInt(JsonElement args, string name, int min, int max, int defaultValue) =>
        GetInt(args, name) is { } value ? Math.Clamp(value, min, max) : defaultValue;

    /// <summary>Boolean property value; false when absent or not a boolean.</summary>
    public static bool GetBool(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        el.GetBoolean();

    /// <summary>Boolean property with explicit default; null when absent.</summary>
    public static bool? GetBoolOrNull(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean()
            : null;

    /// <summary>
    ///     Required string: Failure "Missing or empty '&lt;name&gt;'." when absent/empty.
    ///     A thin wrapper over <see cref="JsonArgValidator.RequiredNonEmptyString" />,
    ///     the canonical required-argument reader: the empty-but-not-blank rule and
    ///     this method's message are preserved verbatim.
    /// </summary>
    public static Result<string> RequireString(JsonElement args, string name)
    {
        return JsonArgValidator.RequiredNonEmptyString(args, name, $"Missing or empty '{name}'.");
    }

    /// <summary>
    ///     Required int: Failure when absent/not a number. A thin wrapper over
    ///     <see cref="JsonArgValidator.RequiredInt" />, the canonical required-argument
    ///     reader, preserving this method's message.
    /// </summary>
    public static Result<int> RequireInt(JsonElement args, string name)
    {
        return JsonArgValidator.RequiredInt(args, name, $"Missing or non-numeric '{name}'.");
    }

    /// <summary>
    ///     Object property value, or <c>default</c> when absent / not an object —
    ///     the "absent ⇒ <c>default</c>" spelling callers use for pass-through
    ///     subtrees (e.g. MCP <c>arguments</c>).
    /// </summary>
    public static JsonElement GetObject(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object
            ? el
            : default;

    /// <summary>
    ///     First string-valued property among alias names (legacy + current
    ///     casing), or null when none of them is present as a string (issue
    ///     #1579: replaces the per-store first-of-names loops).
    /// </summary>
    public static string? GetFirstString(JsonElement args, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = GetString(args, name);
            if (value is not null)
                return value;
        }

        return null;
    }
}
