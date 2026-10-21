// PluginConfigValidator.cs: checks a plugin's configuration section against the
// schema the plugin declared (#1734).
//
// WHAT THIS IS
// ------------
// A plugin declares its settings in its manifest, as data. The user writes
// values under `plugins.<plugin-name>` in `~/.harbor/config.json`. This file is
// where those two meet: it reads the user's section against the declared
// schema and produces either
//
//   * a set of values that is valid by construction (every setting the user set
//     has the declared type and is inside the declared range, and every setting
//     the user did not set carries the default the schema published), or
//   * a failure that NAMES THE SETTING that is wrong.
//
// WHY THE FAILURE NAMES THE SETTING
// ---------------------------------
// A configuration error the user cannot locate is a configuration error the user
// cannot fix. "Invalid configuration" sends them looking through the file; a
// message that says `plugins.web-search.maxResults: 100 is above the highest
// allowed value 50` sends them to the one line that is wrong. So every message
// here carries the full path of the setting, the value that was wrong, and what
// was expected.
//
// WHY THE DEFAULTS ARE APPLIED HERE, AND NOT AT PARSE TIME
// ---------------------------------------------------------
// The host stores the user's section as it was written. The defaults are a
// property of the SCHEMA, not of the file: a plugin that declares a default
// gets that default whether the user wrote the setting or not, and the value
// the plugin reads is the same either way. Applying them here, where the
// schema is in hand, keeps one rule: the values a plugin receives are the
// values the user wrote, completed with the defaults, and nothing else.
//
// THE TYPES ARE CHECKED, NOT COERCED
// ---------------------------------
// A port written as `"8080"` is not the number 8080, and a limit written as
// `"50"` is not the limit 50. Coercing them would let a typo through as a
// working value, and the plugin would never know the user meant something else.
// A value that does not have the declared type is refused, and the message says
// which type was expected.

using System.Globalization;
using System.Text.Json;
using Harbor.Abstractions.Plugins;

namespace Harbor.Plugins.Abstractions;

/// <summary>
///     Validates a plugin's configuration section against the schema the plugin
///     declared, and resolves the section into the values a plugin receives.
/// </summary>
public static class PluginConfigValidator
{
    /// <summary>
    ///     Check a section against a schema and resolve it into values.
    /// </summary>
    /// <param name="schema">The schema the plugin declared.</param>
    /// <param name="section">
    ///     The user's section for this plugin. A section that was never written
    ///     is <see cref="JsonValueKind.Undefined" />, which resolves to the
    ///     declared defaults.
    /// </param>
    /// <param name="path">
    ///     The path the section is written at, used in the messages
    ///     (<c>plugins.web-search</c>).
    /// </param>
    /// <returns>
    ///     The resolved values, or a failure naming the setting that is wrong.
    /// </returns>
    public static Result<IReadOnlyDictionary<string, PluginConfigValue>> Validate(
        PluginConfigSchema schema,
        JsonElement section,
        string path)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var values = new Dictionary<string, PluginConfigValue>(StringComparer.Ordinal);

        if (section.ValueKind != JsonValueKind.Undefined && section.ValueKind != JsonValueKind.Object)
        {
            return Result.Failure<IReadOnlyDictionary<string, PluginConfigValue>>(
                $"{path}: the section must be a JSON object, but it is {Describe(section.ValueKind)}.");
        }

        if (section.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in section.EnumerateObject())
            {
                PluginConfigField? field = schema.Find(property.Name);
                if (field is null)
                {
                    return Result.Failure<IReadOnlyDictionary<string, PluginConfigValue>>(
                        $"{path}.{property.Name}: unknown setting '{property.Name}'. "
                        + $"The plugin declares: {Declared(schema)}.");
                }

                Result<PluginConfigValue> parsed = ParseValue(field, property.Value, $"{path}.{property.Name}");
                if (parsed.IsFailure)
                {
                    return parsed.ConvertFailure<IReadOnlyDictionary<string, PluginConfigValue>>();
                }

                Result<PluginConfigValue> ranged = CheckRange(field, parsed.Value, $"{path}.{property.Name}");
                if (ranged.IsFailure)
                {
                    return ranged.ConvertFailure<IReadOnlyDictionary<string, PluginConfigValue>>();
                }

                values[property.Name] = ranged.Value;
            }
        }

        // The settings the user did not set take the default the schema published.
        // A setting with no default is REQUIRED: there is nothing to fall back
        // to, so the user has to give it a value.
        foreach (PluginConfigField field in schema.Fields)
        {
            if (values.ContainsKey(field.Name))
            {
                continue;
            }

            if (field.Default is null)
            {
                return Result.Failure<IReadOnlyDictionary<string, PluginConfigValue>>(
                    $"{path}.{field.Name}: the plugin declares no default for this setting, "
                    + "so it must be given a value.");
            }

            values[field.Name] = field.Default;
        }

        return Result.Success<IReadOnlyDictionary<string, PluginConfigValue>>(values);
    }

    /// <summary>
    ///     The settings a schema declares, as one readable list, for the message
    ///     that names an unknown setting.
    /// </summary>
    /// <param name="schema">The schema the plugin declared.</param>
    /// <returns>The declared setting names, comma separated.</returns>
    private static string Declared(PluginConfigSchema schema)
    {
        if (schema.Fields.Count == 0)
        {
            return "(none)";
        }

        var names = new List<string>(schema.Fields.Count);
        foreach (PluginConfigField field in schema.Fields)
        {
            names.Add(field.Name);
        }

        return string.Join(", ", names);
    }

    /// <summary>
    ///     Read one JSON value as a declared type.
    /// </summary>
    /// <param name="type">The declared type.</param>
    /// <param name="element">The JSON value.</param>
    /// <param name="path">The path the value is written at, for the message.</param>
    /// <returns>The typed value, or a failure naming the value.</returns>
    public static Result<PluginConfigValue> ReadValue(
        PluginConfigFieldType type,
        JsonElement element,
        string path)
    {
        switch (type)
        {
            case PluginConfigFieldType.String:
                if (element.ValueKind != JsonValueKind.String)
                {
                    return Mismatch(path, type, element.ValueKind);
                }

                return Result.Success(new PluginConfigValue
                {
                    Type = PluginConfigFieldType.String,
                    Text = element.GetString() ?? string.Empty,
                });

            case PluginConfigFieldType.Integer:
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long integer))
                {
                    return Mismatch(path, type, element.ValueKind);
                }

                return Result.Success(new PluginConfigValue
                {
                    Type = PluginConfigFieldType.Integer,
                    Text = integer.ToString(CultureInfo.InvariantCulture),
                });

            case PluginConfigFieldType.Number:
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out double number))
                {
                    return Mismatch(path, type, element.ValueKind);
                }

                return Result.Success(new PluginConfigValue
                {
                    Type = PluginConfigFieldType.Number,
                    Text = number.ToString("R", CultureInfo.InvariantCulture),
                });

            case PluginConfigFieldType.Boolean:
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return Mismatch(path, type, element.ValueKind);
                }

                return Result.Success(new PluginConfigValue
                {
                    Type = PluginConfigFieldType.Boolean,
                    Text = element.GetBoolean() ? "true" : "false",
                });

            case PluginConfigFieldType.StringList:
                if (element.ValueKind != JsonValueKind.Array)
                {
                    return Mismatch(path, type, element.ValueKind);
                }

                var items = new List<string>();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        return Result.Failure<PluginConfigValue>(
                            $"{path}: expected a list of strings, but it holds {Describe(item.ValueKind)}.");
                    }

                    items.Add(item.GetString() ?? string.Empty);
                }

                return Result.Success(new PluginConfigValue
                {
                    Type = PluginConfigFieldType.StringList,
                    Items = items,
                });

            default:
                return Result.Failure<PluginConfigValue>(
                    $"{path}: the plugin declares a setting type this Harbor does not know.");
        }
    }

    /// <summary>
    ///     Read one JSON value as the type a schema declares for a setting.
    /// </summary>
    /// <param name="field">The declared setting.</param>
    /// <param name="element">The value the user wrote.</param>
    /// <param name="path">The path of the setting, for the message.</param>
    /// <returns>The typed value, or a failure naming the setting.</returns>
    private static Result<PluginConfigValue> ParseValue(
        PluginConfigField field,
        JsonElement element,
        string path)
        => ReadValue(field.Type, element, path);

    /// <summary>
    ///     Check a value against the range and the allowed values the schema
    ///     declares for it.
    /// </summary>
    /// <param name="field">The declared setting.</param>
    /// <param name="value">The value the user wrote.</param>
    /// <param name="path">The path of the setting, for the message.</param>
    /// <returns>The value, or a failure naming the setting.</returns>
    private static Result<PluginConfigValue> CheckRange(
        PluginConfigField field,
        PluginConfigValue value,
        string path)
    {
        if (field.Type is PluginConfigFieldType.Integer or PluginConfigFieldType.Number)
        {
            double number = value.AsNumber();
            if (field.Minimum is { } minimum && number < minimum)
            {
                return Result.Failure<PluginConfigValue>(
                    $"{path}: {Format(number)} is below the lowest allowed value {Format(minimum)}.");
            }

            if (field.Maximum is { } maximum && number > maximum)
            {
                return Result.Failure<PluginConfigValue>(
                    $"{path}: {Format(number)} is above the highest allowed value {Format(maximum)}.");
            }
        }

        if (field.AllowedValues is { Count: > 0 } allowed && !Contains(allowed, value.AsString()))
        {
            return Result.Failure<PluginConfigValue>(
                $"{path}: '{value.AsString()}' is not one of the allowed values: {string.Join(", ", allowed)}.");
        }

        return Result.Success(value);
    }

    /// <summary>
    ///     Whether a list holds a value, compared exactly.
    /// </summary>
    /// <param name="values">The list to look in.</param>
    /// <param name="candidate">The value to look for.</param>
    /// <returns><see langword="true" /> when the list holds the value.</returns>
    private static bool Contains(IReadOnlyList<string> values, string candidate)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The message for a value whose type is not the declared one.
    /// </summary>
    /// <param name="path">The path of the setting.</param>
    /// <param name="expected">The declared type.</param>
    /// <param name="actual">The kind of value the user wrote.</param>
    /// <returns>The failure.</returns>
    private static Result<PluginConfigValue> Mismatch(
        string path,
        PluginConfigFieldType expected,
        JsonValueKind actual)
        => Result.Failure<PluginConfigValue>(
            $"{path}: expected {Expected(expected)}, but the value is {Describe(actual)}.");

    /// <summary>
    ///     How a declared type is named in a message.
    /// </summary>
    /// <param name="type">The declared type.</param>
    /// <returns>The name.</returns>
    private static string Expected(PluginConfigFieldType type) => type switch
    {
        PluginConfigFieldType.String => "a string",
        PluginConfigFieldType.Integer => "a whole number",
        PluginConfigFieldType.Number => "a number",
        PluginConfigFieldType.Boolean => "true or false",
        PluginConfigFieldType.StringList => "a list of strings",
        _ => "a value",
    };

    /// <summary>
    ///     How a JSON value kind is named in a message.
    /// </summary>
    /// <param name="kind">The kind of value the user wrote.</param>
    /// <returns>The name.</returns>
    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "true or false",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };

    /// <summary>
    ///     How a number is written in a message.
    /// </summary>
    /// <param name="value">The number.</param>
    /// <returns>The written form.</returns>
    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
