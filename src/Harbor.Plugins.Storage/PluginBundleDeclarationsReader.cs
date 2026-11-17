// PluginBundleDeclarationsReader.cs: reads the skills, hooks and MCP servers a
// plugin declares in its manifest (#2229).
//
// WHAT IS REFUSED, AND WHY
// ------------------------
// Each of the three fields is a list. A field that is not a list, an entry that
// is not an object, a missing or empty locator, a value of the wrong type and a
// duplicate identity are all refused. The reason names the field, the index and
// the file, so a plugin author can fix the declaration without guessing.
//
// A refused declaration is a REFUSAL of the plugin, not a fallback to "declares
// nothing": a plugin that shipped a declaration the host could not read must
// not end up looking like a plugin that brought no skills. That is the quiet
// plugin loss this reader exists to prevent.
//
// Extra keys inside an entry are ignored for the same reason the top-level
// reader ignores fields it does not know: a manifest written for a newer Harbor
// still loads here. The required keys are checked, so a typo in one of those is
// still caught.

using System.Text.Json;
using Harbor.Plugins.Abstractions;

namespace Harbor.Plugins.Storage;

/// <summary>
///     Reads the non-code contributions a plugin declares in its manifest.
/// </summary>
public static class PluginBundleDeclarationsReader
{
    /// <summary>
    ///     Read the <c>skills</c> array.
    /// </summary>
    /// <param name="element">The field value from the manifest.</param>
    /// <param name="origin">The manifest file name, used in the messages.</param>
    /// <returns>The declared skills, or a failure naming the bad entry.</returns>
    public static Result<IReadOnlyList<PluginSkillDeclaration>> ReadSkills(JsonElement element, string origin)
    {
        Result<JsonElement.ArrayEnumerator> list = ReadArray(element, "skills", origin);
        if (list.IsFailure)
            return list.ConvertFailure<IReadOnlyList<PluginSkillDeclaration>>();

        var skills = new List<PluginSkillDeclaration>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement item in list.Value)
        {
            string label = $"skills[{index}]";
            if (!TryReadObject(item, label, origin, out string? objectError))
                return Result.Failure<IReadOnlyList<PluginSkillDeclaration>>(objectError!);

            Result<string> path = ReadRequiredString(item, "path", label, origin);
            if (path.IsFailure)
                return path.ConvertFailure<IReadOnlyList<PluginSkillDeclaration>>();

            if (!paths.Add(path.Value))
                return Result.Failure<IReadOnlyList<PluginSkillDeclaration>>(
                    $"The plugin manifest '{origin}' declares the skill path '{path.Value}' twice.");

            Result<string?> name = ReadOptionalString(item, "name", label, origin);
            if (name.IsFailure)
                return name.ConvertFailure<IReadOnlyList<PluginSkillDeclaration>>();

            Result<string?> description = ReadOptionalString(item, "description", label, origin);
            if (description.IsFailure)
                return description.ConvertFailure<IReadOnlyList<PluginSkillDeclaration>>();

            skills.Add(new PluginSkillDeclaration
            {
                Path = path.Value,
                Name = name.Value,
                Description = description.Value,
            });
            index++;
        }

        return Result.Success<IReadOnlyList<PluginSkillDeclaration>>(skills);
    }

    /// <summary>
    ///     Read the <c>hooks</c> array.
    /// </summary>
    /// <param name="element">The field value from the manifest.</param>
    /// <param name="origin">The manifest file name, used in the messages.</param>
    /// <returns>The declared hook files, or a failure naming the bad entry.</returns>
    public static Result<IReadOnlyList<PluginHookDeclaration>> ReadHooks(JsonElement element, string origin)
    {
        Result<JsonElement.ArrayEnumerator> list = ReadArray(element, "hooks", origin);
        if (list.IsFailure)
            return list.ConvertFailure<IReadOnlyList<PluginHookDeclaration>>();

        var hooks = new List<PluginHookDeclaration>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement item in list.Value)
        {
            string label = $"hooks[{index}]";
            if (!TryReadObject(item, label, origin, out string? objectError))
                return Result.Failure<IReadOnlyList<PluginHookDeclaration>>(objectError!);

            Result<string> path = ReadRequiredString(item, "path", label, origin);
            if (path.IsFailure)
                return path.ConvertFailure<IReadOnlyList<PluginHookDeclaration>>();

            Result<string?> hookEvent = ReadOptionalString(item, "event", label, origin);
            if (hookEvent.IsFailure)
                return hookEvent.ConvertFailure<IReadOnlyList<PluginHookDeclaration>>();

            string identity = path.Value + "\u0000" + (hookEvent.Value ?? string.Empty);
            if (!identities.Add(identity))
                return Result.Failure<IReadOnlyList<PluginHookDeclaration>>(
                    $"The plugin manifest '{origin}' declares the hook '{path.Value}' for the event "
                    + $"'{hookEvent.Value}' twice.");

            hooks.Add(new PluginHookDeclaration
            {
                Path = path.Value,
                Event = hookEvent.Value,
            });
            index++;
        }

        return Result.Success<IReadOnlyList<PluginHookDeclaration>>(hooks);
    }

    /// <summary>
    ///     Read the <c>mcpServers</c> array.
    /// </summary>
    /// <param name="element">The field value from the manifest.</param>
    /// <param name="origin">The manifest file name, used in the messages.</param>
    /// <returns>The declared servers, or a failure naming the bad entry.</returns>
    public static Result<IReadOnlyList<PluginMcpServerDeclaration>> ReadMcpServers(JsonElement element, string origin)
    {
        Result<JsonElement.ArrayEnumerator> list = ReadArray(element, "mcpServers", origin);
        if (list.IsFailure)
            return list.ConvertFailure<IReadOnlyList<PluginMcpServerDeclaration>>();

        var servers = new List<PluginMcpServerDeclaration>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement item in list.Value)
        {
            string label = $"mcpServers[{index}]";
            if (!TryReadObject(item, label, origin, out string? objectError))
                return Result.Failure<IReadOnlyList<PluginMcpServerDeclaration>>(objectError!);

            Result<string> name = ReadRequiredString(item, "name", label, origin);
            if (name.IsFailure)
                return name.ConvertFailure<IReadOnlyList<PluginMcpServerDeclaration>>();

            if (!names.Add(name.Value))
                return Result.Failure<IReadOnlyList<PluginMcpServerDeclaration>>(
                    $"The plugin manifest '{origin}' declares the MCP server '{name.Value}' twice.");

            Result<string> command = ReadRequiredString(item, "command", label, origin);
            if (command.IsFailure)
                return command.ConvertFailure<IReadOnlyList<PluginMcpServerDeclaration>>();

            Result<IReadOnlyList<string>> args = ReadStringArray(item, "args", label, origin);
            if (args.IsFailure)
                return args.ConvertFailure<IReadOnlyList<PluginMcpServerDeclaration>>();

            Result<IReadOnlyDictionary<string, string>> environment = ReadEnvironment(item, label, origin);
            if (environment.IsFailure)
                return environment.ConvertFailure<IReadOnlyList<PluginMcpServerDeclaration>>();

            servers.Add(new PluginMcpServerDeclaration
            {
                Name = name.Value,
                Command = command.Value,
                Args = args.Value,
                Environment = environment.Value,
            });
            index++;
        }

        return Result.Success<IReadOnlyList<PluginMcpServerDeclaration>>(servers);
    }

    /// <summary>
    ///     A field this build reads as a list of declarations.
    /// </summary>
    private static Result<JsonElement.ArrayEnumerator> ReadArray(JsonElement element, string field, string origin)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return Result.Failure<JsonElement.ArrayEnumerator>(
                $"The plugin manifest '{origin}' declares '{field}' that is not an array.");

        return Result.Success(element.EnumerateArray());
    }

    /// <summary>
    ///     An entry must be an object; a string where a declaration belongs is a
    ///     broken declaration, not a shorthand path.
    /// </summary>
    private static bool TryReadObject(
        JsonElement item,
        string label,
        string origin,
        out string? error)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            error = null;
            return true;
        }

        error = $"The plugin manifest '{origin}' declares '{label}' that is not an object.";
        return false;
    }

    /// <summary>
    ///     Read a key that must be a non-empty string.
    /// </summary>
    private static Result<string> ReadRequiredString(JsonElement entry, string key, string label, string origin)
    {
        string fullLabel = label + "." + key;
        if (!entry.TryGetProperty(key, out JsonElement value))
            return Result.Failure<string>(
                $"The plugin manifest '{origin}' declares '{fullLabel}' that is missing.");

        if (value.ValueKind != JsonValueKind.String)
            return Result.Failure<string>(
                $"The plugin manifest '{origin}' declares '{fullLabel}' that is not a string.");

        string text = value.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return Result.Failure<string>(
                $"The plugin manifest '{origin}' declares '{fullLabel}' that is empty.");

        return Result.Success(text);
    }

    /// <summary>
    ///     Read a key that may be absent, but must be a string when present.
    /// </summary>
    private static Result<string?> ReadOptionalString(JsonElement entry, string key, string label, string origin)
    {
        string fullLabel = label + "." + key;
        if (!entry.TryGetProperty(key, out JsonElement value))
            return Result.Success<string?>(null);

        if (value.ValueKind != JsonValueKind.String)
            return Result.Failure<string?>(
                $"The plugin manifest '{origin}' declares '{fullLabel}' that is not a string.");

        return Result.Success<string?>(value.GetString());
    }

    /// <summary>
    ///     Read an optional array of strings. Absent means no entries.
    /// </summary>
    private static Result<IReadOnlyList<string>> ReadStringArray(
        JsonElement entry,
        string key,
        string label,
        string origin)
    {
        string fullLabel = label + "." + key;
        if (!entry.TryGetProperty(key, out JsonElement value))
            return Result.Success<IReadOnlyList<string>>(Array.Empty<string>());

        if (value.ValueKind != JsonValueKind.Array)
            return Result.Failure<IReadOnlyList<string>>(
                $"The plugin manifest '{origin}' declares '{fullLabel}' that is not an array.");

        var items = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return Result.Failure<IReadOnlyList<string>>(
                    $"The plugin manifest '{origin}' declares an entry of '{fullLabel}' that is not a string.");

            items.Add(item.GetString() ?? string.Empty);
        }

        return Result.Success<IReadOnlyList<string>>(items);
    }

    /// <summary>
    ///     Read the optional environment map. Every value must be a string: a
    ///     number here would become a different value in the process than the
    ///     one the manifest wrote.
    /// </summary>
    private static Result<IReadOnlyDictionary<string, string>> ReadEnvironment(
        JsonElement entry,
        string label,
        string origin)
    {
        string fullLabel = label + ".env";
        if (!entry.TryGetProperty("env", out JsonElement value))
            return Result.Success<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(StringComparer.Ordinal));

        if (value.ValueKind != JsonValueKind.Object)
            return Result.Failure<IReadOnlyDictionary<string, string>>(
                $"The plugin manifest '{origin}' declares '{fullLabel}' that is not an object.");

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                return Result.Failure<IReadOnlyDictionary<string, string>>(
                    $"The plugin manifest '{origin}' declares '{fullLabel}.{property.Name}' that is not a string.");

            environment[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return Result.Success<IReadOnlyDictionary<string, string>>(environment);
    }
}
