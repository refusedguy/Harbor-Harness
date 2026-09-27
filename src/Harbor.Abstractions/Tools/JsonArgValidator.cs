namespace Harbor.Abstractions.Tools;

/// <summary>
///     Shared <see cref="ITool.ValidateArguments" /> primitives for builtin tools (#181):
///     the hand-rolled TryGetProperty+ValueKind checks copy-pasted across 19 tools,
///     lifted next to <see cref="ITool" />. Tools keep their schemas and their exact
///     error messages — only the validation mechanics dedup here.
/// </summary>
/// <remarks>
///     All helpers preserve the historical leniency of the tools: optional checks only
///     fire when the property is present with a parseable JSON kind, so a wrong-typed
///     optional (e.g. a string "depth") still passes validation and falls back to its
///     default in ExecuteAsync. Required checks fail on absent, wrong-kind, and
///     blank values (see the per-method flavor for the exact blank rule).
/// </remarks>
public static class JsonArgValidator
{
    /// <summary>
    ///     Required string: failure with <paramref name="missingMessage" /> when absent,
    ///     not a string, empty, or whitespace-only.
    /// </summary>
    public static Result<string> RequiredString(JsonElement args, string name, string missingMessage)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(el.GetString()))
            return Result.Failure<string>(missingMessage);
        return Result.Success(el.GetString()!);
    }

    /// <summary>
    ///     Required string that allows whitespace-only but not empty: failure with
    ///     <paramref name="missingMessage" /> when absent, not a string, or empty.
    ///     (grep/ripgrep patterns — a blank pattern is a degenerate but accepted regex.)
    /// </summary>
    public static Result<string> RequiredNonEmptyString(JsonElement args, string name, string missingMessage)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(el.GetString()))
            return Result.Failure<string>(missingMessage);
        return Result.Success(el.GetString()!);
    }

    /// <summary>
    ///     Required string that allows empty: failure with <paramref name="missingMessage" />
    ///     only when absent or not a string. (write content — empty means "truncate".)
    /// </summary>
    public static Result<string> RequiredStringPresent(JsonElement args, string name, string missingMessage)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return Result.Failure<string>(missingMessage);
        return Result.Success(el.GetString() ?? string.Empty);
    }

    /// <summary>
    ///     Required string with a split message: absent or not a string fails with
    ///     <paramref name="missingMessage" />, empty or whitespace-only fails with
    ///     <paramref name="blankMessage" />. (bash command.)
    /// </summary>
    public static Result<string> RequiredNonBlankString(
        JsonElement args, string name, string missingMessage, string blankMessage)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return Result.Failure<string>(missingMessage);
        string value = el.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<string>(blankMessage);
        return Result.Success(value);
    }

    /// <summary>
    ///     Required path helper: a <see cref="RequiredString" /> for the "path" argument
    ///     with the conventional message. Most file tools share it byte-for-byte.
    /// </summary>
    public static Result RequiredPath(
        JsonElement args,
        string name = "path",
        string missingMessage = "Missing or empty 'path'.") =>
        RequiredString(args, name, missingMessage);

    /// <summary>
    ///     Required int: failure with <paramref name="missingMessage" /> when absent,
    ///     not a number, or not an int32.
    /// </summary>
    public static Result<int> RequiredInt(JsonElement args, string name, string missingMessage)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number
            || !el.TryGetInt32(out int value))
            return Result.Failure<int>(missingMessage);
        return Result.Success(value);
    }

    /// <summary>
    ///     Required bool: failure with <paramref name="missingMessage" /> when absent
    ///     or not a boolean.
    /// </summary>
    public static Result<bool> RequiredBool(JsonElement args, string name, string missingMessage)
    {
        if (!args.TryGetProperty(name, out var el)
            || (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False))
            return Result.Failure<bool>(missingMessage);
        return Result.Success(el.GetBoolean());
    }

    /// <summary>
    ///     Required value from a fixed set: first the <see cref="RequiredString" /> check
    ///     with <paramref name="missingMessage" />, then membership in
    ///     <paramref name="valid" /> (compared with <paramref name="comparison" />),
    ///     otherwise failure with <paramref name="unknownMessage" /> applied to the
    ///     raw value (original casing preserved for the error text).
    /// </summary>
    public static Result<string> RequiredEnum(
        JsonElement args,
        string name,
        string missingMessage,
        IReadOnlyCollection<string> valid,
        Func<string, string> unknownMessage,
        StringComparison comparison = StringComparison.Ordinal)
    {
        Result<string> present = RequiredString(args, name, missingMessage);
        if (present.IsFailure)
            return present;
        string value = present.Value;
        foreach (string candidate in valid)
        {
            if (string.Equals(candidate, value, comparison))
                return Result.Success(value);
        }
        return Result.Failure<string>(unknownMessage(value));
    }

    /// <summary>
    ///     Optional value from a fixed set: failure with <paramref name="invalidMessage" />
    ///     only when present as a string outside <paramref name="valid" />. Absent or
    ///     wrong-typed values pass (historical leniency — see class remarks).
    /// </summary>
    public static Result OptionalEnum(
        JsonElement args,
        string name,
        IReadOnlyCollection<string> valid,
        string invalidMessage,
        StringComparison comparison = StringComparison.Ordinal)
    {
        if (args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            string value = el.GetString() ?? string.Empty;
            bool known = false;
            foreach (string candidate in valid)
            {
                if (string.Equals(candidate, value, comparison))
                {
                    known = true;
                    break;
                }
            }
            if (!known)
                return Result.Failure(invalidMessage);
        }
        return Result.Success();
    }

    /// <summary>
    ///     Optional int lower bound: failure with <paramref name="message" /> only when
    ///     present as a number that parses to int32 below <paramref name="min" />.
    /// </summary>
    public static Result OptionalIntAtLeast(JsonElement args, string name, int min, string message)
    {
        if (args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out int value) && value < min)
            return Result.Failure(message);
        return Result.Success();
    }

    /// <summary>
    ///     Optional int range: failure with <paramref name="message" /> only when present
    ///     as a number that parses to int32 outside [<paramref name="min" />,
    ///     <paramref name="max" />].
    /// </summary>
    public static Result OptionalIntInRange(JsonElement args, string name, int min, int max, string message)
    {
        if (args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out int value) && (value < min || value > max))
            return Result.Failure(message);
        return Result.Success();
    }

    /// <summary>
    ///     Optional JSON object: failure with <paramref name="message" /> only when
    ///     present and not an object. (mcp arguments/args.)
    /// </summary>
    public static Result OptionalObject(JsonElement args, string name, string message)
    {
        if (args.TryGetProperty(name, out var el) && el.ValueKind != JsonValueKind.Object)
            return Result.Failure(message);
        return Result.Success();
    }

    /// <summary>
    ///     Optional bool: failure with <paramref name="message" /> only when present
    ///     and not a boolean. (task background.)
    /// </summary>
    public static Result OptionalBool(JsonElement args, string name, string message)
    {
        if (args.TryGetProperty(name, out var el)
            && el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False)
            return Result.Failure(message);
        return Result.Success();
    }

    /// <summary>
    ///     Required number: failure with <paramref name="message" /> when absent or not
    ///     a number. (lsp line.)
    /// </summary>
    public static Result RequiredNumber(JsonElement args, string name, string message)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return Result.Failure(message);
        return Result.Success();
    }

    /// <summary>
    ///     Optional number: failure with <paramref name="message" /> only when present
    ///     and not a number. (lsp column.)
    /// </summary>
    public static Result OptionalNumber(JsonElement args, string name, string message)
    {
        if (args.TryGetProperty(name, out var el) && el.ValueKind != JsonValueKind.Number)
            return Result.Failure(message);
        return Result.Success();
    }

    /// <summary>True when the property is present and a string (any content, even empty).</summary>
    public static bool HasString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String;

    /// <summary>True when the property is present and a non-empty array. (edit edits[].)</summary>
    public static bool HasNonEmptyArray(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
        && el.GetArrayLength() > 0;
}
