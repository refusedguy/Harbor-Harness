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

    /// <summary>Required string: Failure "Missing or empty '&lt;name&gt;'." when absent/empty.</summary>
    public static Result<string> RequireString(JsonElement args, string name)
    {
        string? value = GetString(args, name);
        return Result.SuccessIf(!string.IsNullOrEmpty(value), $"Missing or empty '{name}'.")
            .Map(() => value!);
    }

    /// <summary>Required int: Failure when absent/not a number.</summary>
    public static Result<int> RequireInt(JsonElement args, string name)
    {
        int? value = GetInt(args, name);
        return Result.SuccessIf(value.HasValue, $"Missing or non-numeric '{name}'.")
            .Map(() => value!.Value);
    }
}
