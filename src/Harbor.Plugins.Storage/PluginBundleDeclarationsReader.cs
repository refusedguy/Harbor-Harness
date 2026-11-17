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
// still caught. The reads are chained, so a failure short-circuits to the host
// with the first bad entry instead of leaving a partial list behind.

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
    public static Result<IReadOnlyList<PluginSkillDeclaration>> ReadSkills(JsonElement element, string origin) =>
        ReadArray(element, "skills", origin)
            .Bind(items => ReadSkillEntries(items, origin));

    /// <summary>
    ///     Read the <c>hooks</c> array.
    /// </summary>
    /// <param name="element">The field value from the manifest.</param>
    /// <param name="origin">The manifest file name, used in the messages.</param>
    /// <returns>The declared hook files, or a failure naming the bad entry.</returns>
    public static Result<IReadOnlyList<PluginHookDeclaration>> ReadHooks(JsonElement element, string origin) =>
        ReadArray(element, "hooks", origin)
            .Bind(items => ReadHookEntries(items, origin));

    /// <summary>
    ///     Read the <c>mcpServers</c> array.
    /// </summary>
    /// <param name="element">The field value from the manifest.</param>
    /// <param name="origin">The manifest file name, used in the messages.</param>
    /// <returns>The declared servers, or a failure naming the bad entry.</returns>
    public static Result<IReadOnlyList<PluginMcpServerDeclaration>> ReadMcpServers(JsonElement element, string origin) =>
        ReadArray(element, "mcpServers", origin)
            .Bind(items => ReadMcpServerEntries(items, origin));

    /// <summary>
    ///     A field this build reads as a list of declarations.
    /// </summary>
    private static Result<JsonElement.ArrayEnumerator> ReadArray(JsonElement element, string field, string origin) =>
        element.ValueKind == JsonValueKind.Array
            ? Result.Success(element.EnumerateArray())
            : Result.Failure<JsonElement.ArrayEnumerator>(
                $"The plugin manifest '{origin}' declares '{field}' that is not an array.");

    /// <summary>
    ///     An entry must be an object; a string where a declaration belongs is a
    ///     broken declaration, not a shorthand path.
    /// </summary>
    private static Result<JsonElement> ReadObject(JsonElement item, string label, string origin) =>
        item.ValueKind == JsonValueKind.Object
            ? Result.Success(item)
            : Result.Failure<JsonElement>(
                $"The plugin manifest '{origin}' declares '{label}' that is not an object.");

    /// <summary>
    ///     Read one skill. Absent optional fields stay unsaid; a bad one fails
    ///     the whole read.
    /// </summary>
    private static Result<PluginSkillDeclaration> ReadSkillEntry(
        JsonElement item,
        string label,
        string origin) =>
        from obj in ReadObject(item, label, origin)
        from path in ReadRequiredString(obj, "path", label, origin)
        from name in ReadOptionalString(obj, "name", label, origin)
        from description in ReadOptionalString(obj, "description", label, origin)
        select new PluginSkillDeclaration
        {
            Path = path,
            Name = name.HasValue ? name.Value : null,
            Description = description.HasValue ? description.Value : null,
        };

    /// <summary>
    ///     Read one hook file.
    /// </summary>
    private static Result<PluginHookDeclaration> ReadHookEntry(
        JsonElement item,
        string label,
        string origin) =>
        from obj in ReadObject(item, label, origin)
        from path in ReadRequiredString(obj, "path", label, origin)
        from hookEvent in ReadOptionalString(obj, "event", label, origin)
        select new PluginHookDeclaration
        {
            Path = path,
            Event = hookEvent.HasValue ? hookEvent.Value : null,
        };

    /// <summary>
    ///     Read one MCP server declaration.
    /// </summary>
    private static Result<PluginMcpServerDeclaration> ReadMcpServerEntry(
        JsonElement item,
        string label,
        string origin) =>
        from obj in ReadObject(item, label, origin)
        from name in ReadRequiredString(obj, "name", label, origin)
        from command in ReadRequiredString(obj, "command", label, origin)
        from args in ReadStringArray(obj, "args", label, origin)
        from environment in ReadEnvironment(obj, label, origin)
        select new PluginMcpServerDeclaration
        {
            Name = name,
            Command = command,
            Args = args,
            Environment = environment,
        };

    /// <summary>
    ///     Read all skill entries; one bad entry fails the whole list.
    /// </summary>
    private static Result<IReadOnlyList<PluginSkillDeclaration>> ReadSkillEntries(
        JsonElement.ArrayEnumerator items,
        string origin)
    {
        Result<IReadOnlyList<PluginSkillDeclaration>> accumulated =
            Result.Success<IReadOnlyList<PluginSkillDeclaration>>(Array.Empty<PluginSkillDeclaration>());

        int index = 0;
        foreach (JsonElement item in items)
        {
            string label = $"skills[{index++}]";
            accumulated =
                from list in accumulated
                from skill in ReadSkillEntry(item, label, origin)
                from next in AppendSkill(list, skill, origin)
                select next;
        }

        return accumulated;
    }

    /// <summary>
    ///     Add one skill, refusing a second declaration of the same path. The
    ///     paths are few, so the linear check keeps the reader obvious.
    /// </summary>
    private static Result<IReadOnlyList<PluginSkillDeclaration>> AppendSkill(
        IReadOnlyList<PluginSkillDeclaration> skills,
        PluginSkillDeclaration skill,
        string origin)
    {
        foreach (PluginSkillDeclaration existing in skills)
        {
            if (string.Equals(existing.Path, skill.Path, StringComparison.Ordinal))
                return Result.Failure<IReadOnlyList<PluginSkillDeclaration>>(
                    $"The plugin manifest '{origin}' declares the skill path '{skill.Path}' twice.");
        }

        var next = new List<PluginSkillDeclaration>(skills.Count + 1);
        next.AddRange(skills);
        next.Add(skill);
        return Result.Success<IReadOnlyList<PluginSkillDeclaration>>(next);
    }

    /// <summary>
    ///     Read all hook entries; one bad entry fails the whole list.
    /// </summary>
    private static Result<IReadOnlyList<PluginHookDeclaration>> ReadHookEntries(
        JsonElement.ArrayEnumerator items,
        string origin)
    {
        Result<IReadOnlyList<PluginHookDeclaration>> accumulated =
            Result.Success<IReadOnlyList<PluginHookDeclaration>>(Array.Empty<PluginHookDeclaration>());

        int index = 0;
        foreach (JsonElement item in items)
        {
            string label = $"hooks[{index++}]";
            accumulated =
                from list in accumulated
                from hook in ReadHookEntry(item, label, origin)
                from next in AppendHook(list, hook, origin)
                select next;
        }

        return accumulated;
    }

    /// <summary>
    ///     Add one hook, refusing a second declaration of the same file and
    ///     event pair.
    /// </summary>
    private static Result<IReadOnlyList<PluginHookDeclaration>> AppendHook(
        IReadOnlyList<PluginHookDeclaration> hooks,
        PluginHookDeclaration hook,
        string origin)
    {
        string identity = hook.Path + "\u0000" + (hook.Event ?? string.Empty);
        foreach (PluginHookDeclaration existing in hooks)
        {
            if (string.Equals(existing.Path + "\u0000" + (existing.Event ?? string.Empty), identity,
                    StringComparison.Ordinal))
                return Result.Failure<IReadOnlyList<PluginHookDeclaration>>(
                    $"The plugin manifest '{origin}' declares the hook '{hook.Path}' for the event "
                    + $"'{hook.Event}' twice.");
        }

        var next = new List<PluginHookDeclaration>(hooks.Count + 1);
        next.AddRange(hooks);
        next.Add(hook);
        return Result.Success<IReadOnlyList<PluginHookDeclaration>>(next);
    }

    /// <summary>
    ///     Read all MCP server entries; one bad entry fails the whole list.
    /// </summary>
    private static Result<IReadOnlyList<PluginMcpServerDeclaration>> ReadMcpServerEntries(
        JsonElement.ArrayEnumerator items,
        string origin)
    {
        Result<IReadOnlyList<PluginMcpServerDeclaration>> accumulated =
            Result.Success<IReadOnlyList<PluginMcpServerDeclaration>>(Array.Empty<PluginMcpServerDeclaration>());

        int index = 0;
        foreach (JsonElement item in items)
        {
            string label = $"mcpServers[{index++}]";
            accumulated =
                from list in accumulated
                from server in ReadMcpServerEntry(item, label, origin)
                from next in AppendServer(list, server, origin)
                select next;
        }

        return accumulated;
    }

    /// <summary>
    ///     Add one server, refusing a second declaration of the same name. A
    ///     server name is a key the host would have to disambiguate otherwise.
    /// </summary>
    private static Result<IReadOnlyList<PluginMcpServerDeclaration>> AppendServer(
        IReadOnlyList<PluginMcpServerDeclaration> servers,
        PluginMcpServerDeclaration server,
        string origin)
    {
        foreach (PluginMcpServerDeclaration existing in servers)
        {
            if (string.Equals(existing.Name, server.Name, StringComparison.Ordinal))
                return Result.Failure<IReadOnlyList<PluginMcpServerDeclaration>>(
                    $"The plugin manifest '{origin}' declares the MCP server '{server.Name}' twice.");
        }

        var next = new List<PluginMcpServerDeclaration>(servers.Count + 1);
        next.AddRange(servers);
        next.Add(server);
        return Result.Success<IReadOnlyList<PluginMcpServerDeclaration>>(next);
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
        return string.IsNullOrWhiteSpace(text)
            ? Result.Failure<string>(
                $"The plugin manifest '{origin}' declares '{fullLabel}' that is empty.")
            : Result.Success(text);
    }

    /// <summary>
    ///     Read a key that may be absent, but must be a string when present. The
    ///     absent case is a <see cref="Maybe{T}" /> with no value, never a
    ///     <c>Result&lt;string?&gt;</c> — that type would hide "absent" inside
    ///     "succeeded" and leave the caller no branch to write.
    /// </summary>
    private static Result<Maybe<string>> ReadOptionalString(
        JsonElement entry,
        string key,
        string label,
        string origin)
    {
        string fullLabel = label + "." + key;
        if (!entry.TryGetProperty(key, out JsonElement value))
            return Result.Success(Maybe<string>.None);

        if (value.ValueKind != JsonValueKind.String)
            return Result.Failure<Maybe<string>>(
                $"The plugin manifest '{origin}' declares '{fullLabel}' that is not a string.");

        return Result.Success(Maybe<string>.From(value.GetString()));
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
