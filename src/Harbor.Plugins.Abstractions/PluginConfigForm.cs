// PluginConfigForm.cs: the settings form a plugin's schema generates (#1734).
//
// WHAT THIS IS
// ------------
// A plugin that declares its settings has, in effect, described a settings
// page. The host does not need a page of its own for every plugin: the
// schema says what the settings are, what they are called, what they may be
// and what they default to, and a form is exactly that information arranged
// for a human.
//
// This file is that arrangement. It is DATA, not a widget: one row per
// declared setting, carrying the declared type, the description, the default,
// the value the user actually has, the range the value must fall in and the
// list of values the setting accepts. A CLI prints it, a desktop form renders
// it, and neither of them has to know how a plugin's schema is shaped.
//
// WHY THE DEFAULT AND THE CURRENT VALUE ARE SEPARATE
// --------------------------------------------------
// They answer different questions. The default is what the plugin declares,
// and it is what the user gets back if they clear the setting. The current
// value is what the user has. A form that showed only the current value would
// hide what the plugin falls back to; a form that showed only the default
// would hide what the user chose. So the row carries both.
//
// SECRETS
// -------
// A setting the plugin marked as a secret is not shown in clear. The row
// carries the flag, and it is the renderer that decides what to put on the
// screen: the row never carries the secret's text twice.

using Harbor.Abstractions.Plugins;

namespace Harbor.Plugins.Abstractions;

/// <summary>
///     One row of a plugin's settings form: one declared setting, with
///     everything a renderer needs to show it.
/// </summary>
public sealed class PluginConfigFormRow
{
    /// <summary>The setting's name, as it is written in the configuration file.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The type of the setting.</summary>
    public PluginConfigFieldType Type { get; init; }

    /// <summary>One line describing the setting, when the plugin wrote one.</summary>
    public string? Description { get; init; }

    /// <summary>The value the plugin falls back to when the user sets nothing.</summary>
    public PluginConfigValue? Default { get; init; }

    /// <summary>
    ///     The value the setting has right now: the one the user wrote, or the
    ///     default when the user wrote none.
    /// </summary>
    public PluginConfigValue? Current { get; init; }

    /// <summary>The lowest value the setting accepts, for a numeric setting.</summary>
    public double? Minimum { get; init; }

    /// <summary>The highest value the setting accepts, for a numeric setting.</summary>
    public double? Maximum { get; init; }

    /// <summary>The values the setting accepts, when it accepts a fixed set.</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    /// <summary>Whether the setting holds a secret and must not be shown in clear.</summary>
    public bool IsSecret { get; init; }
}

/// <summary>
///     Builds the settings form a plugin's schema generates.
/// </summary>
public static class PluginConfigForm
{
    /// <summary>
    ///     Build the form for a schema.
    /// </summary>
    /// <param name="schema">The schema the plugin declared.</param>
    /// <param name="current">
    ///     The values the section resolves to. <see langword="null" /> when the
    ///     user configured nothing, in which case every row shows the declared
    ///     default as its current value.
    /// </param>
    /// <returns>One row per declared setting, in the order the plugin declared them.</returns>
    public static IReadOnlyList<PluginConfigFormRow> Build(
        PluginConfigSchema schema,
        IReadOnlyDictionary<string, PluginConfigValue>? current)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var rows = new List<PluginConfigFormRow>(schema.Fields.Count);
        foreach (PluginConfigField field in schema.Fields)
        {
            PluginConfigValue? value = null;
            if (current is not null && current.TryGetValue(field.Name, out PluginConfigValue? found))
            {
                value = found;
            }

            rows.Add(new PluginConfigFormRow
            {
                Name = field.Name,
                Type = field.Type,
                Description = field.Description,
                Default = field.Default,
                Current = value ?? field.Default,
                Minimum = field.Minimum,
                Maximum = field.Maximum,
                AllowedValues = field.AllowedValues,
                IsSecret = field.IsSecret,
            });
        }

        return rows;
    }
}
