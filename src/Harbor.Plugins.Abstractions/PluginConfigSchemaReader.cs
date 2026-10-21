// PluginConfigSchemaReader.cs: reads the settings a plugin declares in its
// manifest into a schema (#1734).
//
// WHAT THE MANIFEST LOOKS LIKE
// ----------------------------
// A plugin that wants settings ships a `config` object next to the other
// manifest fields, and the object maps a setting name to the setting's shape:
//
//   {
//     "schemaVersion": 1,
//     "name": "web-search",
//     "config": {
//       "endpoint": { "type": "string", "default": "https://api.example.com" },
//       "maxResults": { "type": "integer", "default": 5, "minimum": 1, "maximum": 50,
//                       "description": "How many results a search returns." },
//       "apiKey": { "type": "string", "secret": true }
//     }
//   }
//
// The object is the SCHEMA, and the schema is data: the host reads it without
// compiling the plugin, so it can tell the user what a plugin wants before the
// plugin runs, and it can refuse a plugin whose schema it cannot understand.
//
// WHY A SCHEMA IS REFUSED RATHER THAN GUESSED AT
// ----------------------------------------------
// A manifest that says `"type": "interger"` is a plugin that asked for a
// setting whose shape the host does not know. Reading it as a string would
// give the user a setting that behaves differently from the one the plugin
// meant; ignoring it would give the plugin a setting that is not there. Both
// are worse than saying so. So an unreadable schema refuses the plugin, and
// the message names the setting and the key that is wrong.
//
// A setting with NO "default" is a setting the user must give a value: there
// is nothing to fall back to, so the host says so instead of handing the
// plugin an empty value it would have to guess at.

using System.Text.Json;
using Harbor.Abstractions.Plugins;

namespace Harbor.Plugins.Abstractions;

/// <summary>
///     Reads the configuration schema a plugin declares in its manifest.
/// </summary>
public static class PluginConfigSchemaReader
{
    /// <summary>
    ///     The keys a setting may declare. A key outside this list is a typo,
    ///     and a typo is refused rather than ignored.
    /// </summary>
    private static readonly string[] KnownKeys =
        ["type", "default", "description", "minimum", "maximum", "allowed", "secret"];

    /// <summary>
    ///     Read a <c>config</c> object into a schema.
    /// </summary>
    /// <param name="element">The <c>config</c> object from the manifest.</param>
    /// <param name="origin">
    ///     The manifest the object came from, used in the messages
    ///     (the file name).
    /// </param>
    /// <returns>The schema, or a failure naming the setting that is wrong.</returns>
    public static Result<PluginConfigSchema> Read(JsonElement element, string origin)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Result.Failure<PluginConfigSchema>(
                $"The plugin manifest '{origin}' declares a 'config' that is not an object.");
        }

        var fields = new List<PluginConfigField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return Result.Failure<PluginConfigSchema>(
                    $"The plugin manifest '{origin}' declares the setting '{property.Name}' twice.");
            }

            Result<PluginConfigField> field = ReadField(property.Name, property.Value, origin);
            if (field.IsSuccess)
            {
                fields.Add(field.Value);
            }
            else
            {
                return field.ConvertFailure<PluginConfigSchema>();
            }
        }

        return Result.Success(new PluginConfigSchema { Fields = fields });
    }

    /// <summary>
    ///     Read one setting's declaration.
    /// </summary>
    /// <param name="name">The setting's name.</param>
    /// <param name="element">The setting's declaration.</param>
    /// <param name="origin">The manifest the declaration came from.</param>
    /// <returns>The declared setting, or a failure naming it.</returns>
    private static Result<PluginConfigField> ReadField(string name, JsonElement element, string origin)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Result.Failure<PluginConfigField>(
                $"The plugin manifest '{origin}' declares the setting '{name}' as something that is not an object.");
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!KnownKeys.Contains(property.Name, StringComparer.Ordinal))
            {
                return Result.Failure<PluginConfigField>(
                    $"The plugin manifest '{origin}' declares the setting '{name}' with a key this Harbor does not "
                    + $"know: '{property.Name}'. The keys are: {string.Join(", ", KnownKeys)}.");
            }
        }

        var type = PluginConfigFieldType.String;
        if (element.TryGetProperty("type", out JsonElement typeElement))
        {
            if (typeElement.ValueKind != JsonValueKind.String)
            {
                return Result.Failure<PluginConfigField>(
                    $"The plugin manifest '{origin}' declares the setting '{name}' with a 'type' that is not a string.");
            }

            string typeName = typeElement.GetString() ?? string.Empty;
            if (!TryParseType(typeName, out type))
            {
                return Result.Failure<PluginConfigField>(
                    $"The plugin manifest '{origin}' declares the setting '{name}' with a type this Harbor does not "
                    + "know: '" + typeName + "'. The types are: string, integer, number, boolean, stringList.");
            }
        }

        PluginConfigValue? defaultValue = null;
        if (element.TryGetProperty("default", out JsonElement defaultElement))
        {
            Result<PluginConfigValue> read = PluginConfigValidator.ReadValue(
                type,
                defaultElement,
                $"The plugin manifest '{origin}' declares the default of the setting '{name}'");
            if (read.IsSuccess)
            {
                defaultValue = read.Value;
            }
            else
            {
                return read.ConvertFailure<PluginConfigField>();
            }
        }

        string? description = null;
        if (element.TryGetProperty("description", out JsonElement descriptionElement))
        {
            if (descriptionElement.ValueKind != JsonValueKind.String)
            {
                return Result.Failure<PluginConfigField>(
                    $"The plugin manifest '{origin}' declares the setting '{name}' with a 'description' that is not "
                    + "a string.");
            }

            description = descriptionElement.GetString();
        }

        double? minimum = null;
        if (element.TryGetProperty("minimum", out JsonElement minimumElement))
        {
            if (minimumElement.ValueKind != JsonValueKind.Number || !minimumElement.TryGetDouble(out double min))
            {
                return Result.Failure<PluginConfigField>(
                    $"The plugin manifest '{origin}' declares the setting '{name}' with a 'minimum' that is not a "
                    + "number.");
            }

            minimum = min;
        }

        double? maximum = null;
        if (element.TryGetProperty("maximum", out JsonElement maximumElement))
        {
            if (maximumElement.ValueKind != JsonValueKind.Number || !maximumElement.TryGetDouble(out double max))
            {
                return Result.Failure<PluginConfigField>(
                    $"The plugin manifest '{origin}' declares the setting '{name}' with a 'maximum' that is not a "
                    + "number.");
            }

            maximum = max;
        }

        IReadOnlyList<string>? allowed = null;
        if (element.TryGetProperty("allowed", out JsonElement allowedElement))
        {
            if (allowedElement.ValueKind != JsonValueKind.Array)
            {
                return Result.Failure<PluginConfigField>(
                    $"The plugin manifest '{origin}' declares the setting '{name}' with an 'allowed' that is not an "
                    + "array.");
            }

            var values = new List<string>();
            foreach (JsonElement item in allowedElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return Result.Failure<PluginConfigField>(
                        $"The plugin manifest '{origin}' declares the setting '{name}' with an 'allowed' entry that "
                        + "is not a string.");
                }

                values.Add(item.GetString() ?? string.Empty);
            }

            allowed = values;
        }

        bool isSecret = false;
        if (element.TryGetProperty("secret", out JsonElement secretElement))
        {
            if (secretElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Result.Failure<PluginConfigField>(
                    $"The plugin manifest '{origin}' declares the setting '{name}' with a 'secret' that is not "
                    + "true or false.");
            }

            isSecret = secretElement.GetBoolean();
        }

        return Result.Success(new PluginConfigField
        {
            Name = name,
            Type = type,
            Default = defaultValue,
            Description = description,
            Minimum = minimum,
            Maximum = maximum,
            AllowedValues = allowed,
            IsSecret = isSecret,
        });
    }

    /// <summary>
    ///     Read a declared type name.
    /// </summary>
    /// <param name="name">The name as written in the manifest.</param>
    /// <param name="type">The type it names.</param>
    /// <returns><see langword="true" /> when the name is one this Harbor knows.</returns>
    private static bool TryParseType(string name, out PluginConfigFieldType type)
    {
        if (string.Equals(name, "string", StringComparison.OrdinalIgnoreCase))
        {
            type = PluginConfigFieldType.String;
            return true;
        }

        if (string.Equals(name, "integer", StringComparison.OrdinalIgnoreCase))
        {
            type = PluginConfigFieldType.Integer;
            return true;
        }

        if (string.Equals(name, "number", StringComparison.OrdinalIgnoreCase))
        {
            type = PluginConfigFieldType.Number;
            return true;
        }

        if (string.Equals(name, "boolean", StringComparison.OrdinalIgnoreCase))
        {
            type = PluginConfigFieldType.Boolean;
            return true;
        }

        if (string.Equals(name, "stringList", StringComparison.OrdinalIgnoreCase))
        {
            type = PluginConfigFieldType.StringList;
            return true;
        }

        type = PluginConfigFieldType.String;
        return false;
    }
}
