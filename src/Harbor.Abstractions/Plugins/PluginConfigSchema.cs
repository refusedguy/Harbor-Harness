// PluginConfigSchema.cs: the vocabulary of a plugin's own configuration section (#1734).
//
// WHY THIS FILE EXISTS
// --------------------
// A plugin in Harbor had NO configuration of its own. `PluginContext.Configuration`
// was the HOST's `IConfiguration`, so a plugin could read the host's settings but
// could not say "I have a section called `plugins.web-search`", could not declare
// what its settings are, and could not publish a default for them. Every setting
// a plugin wanted had to be hard-coded in the plugin's own code.
//
// This file is the vocabulary that fixes the first half of that: the SHAPE of a
// plugin's configuration section, and the shape of a value inside it. It is data,
// not behaviour, and it is deliberately reflection-free: a schema is built from
// these types by the manifest reader (which parses JSON), so it works under
// NativeAOT and can be compared, printed and stored.
//
// WHAT IS DELIBERATELY NOT HERE
// -----------------------------
// There is no `PluginConfig` base class and no attribute-driven binder. A schema
// that is derived from a plugin's CLR types would need reflection at run time to
// read the types back (#626), and would tie the configuration surface of a
// plugin to the assembly it happens to be compiled into. The schema travels as
// DATA next to the plugin (its manifest), and the host validates the user's
// section against that data before the plugin starts.
//
// THE TWO SHAPES
// --------------
//   * `PluginConfigSchema`: what a plugin DECLARES, the fields it has, their
//     types, their defaults, their ranges and their descriptions.
//   * `PluginConfigValue`: one VALUE inside a section, a scalar carried as
//     text, or a list of strings.
//
// A value carries its type as a tag next to the payload rather than in a
// separate union per type, because the number of types is small and fixed, and
// one shape keeps the host's parsing code to a single switch. The payload is
// text for every scalar type: JSON has one number type, and the schema is what
// says whether a number is an integer or a real, so the text is the only place
// the distinction can live without a second payload slot per type.

using System.Globalization;

namespace Harbor.Abstractions.Plugins;

/// <summary>
///     The type of one setting in a plugin's configuration section.
/// </summary>
public enum PluginConfigFieldType
{
    /// <summary>A text value.</summary>
    String = 0,

    /// <summary>A whole number.</summary>
    Integer = 1,

    /// <summary>A fractional number.</summary>
    Number = 2,

    /// <summary>A true/false value.</summary>
    Boolean = 3,

    /// <summary>A list of text values.</summary>
    StringList = 4,
}

/// <summary>
///     One value inside a plugin's configuration section.
/// </summary>
/// <remarks>
///     <para>
///         The value is a tagged union: <see cref="Type" /> says how to read the
///         payload. A scalar is carried in <see cref="Text" /> (in the invariant
///         culture, so a value written on one machine reads the same on
///         another), and a list is carried in <see cref="Items" />.
///     </para>
///     <para>
///         The accessors are total. A value that came out of the host's
///         validation always parses, because the validator checks the payload
///         against the declared type before the value is handed to anybody; the
///         accessors return the type's zero value for a payload that does not
///         parse, so a hand-built value cannot throw in a plugin's face.
///     </para>
/// </remarks>
public sealed class PluginConfigValue
{
    /// <summary>
    ///     How to read the payload.
    /// </summary>
    public PluginConfigFieldType Type { get; init; } = PluginConfigFieldType.String;

    /// <summary>
    ///     The scalar payload: the text itself for
    ///     <see cref="PluginConfigFieldType.String" />, the invariant
    ///     representation of a number or a boolean for the other scalar types.
    ///     <see langword="null" /> for a list.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    ///     The list payload. <see langword="null" /> for every scalar type.
    /// </summary>
    public IReadOnlyList<string>? Items { get; init; }

    /// <summary>The value as text. An empty string when there is no scalar payload.</summary>
    /// <returns>The scalar payload.</returns>
    public string AsString() => Text ?? string.Empty;

    /// <summary>The value as a whole number. Zero when the payload does not parse.</summary>
    /// <returns>The parsed integer.</returns>
    public long AsInteger()
        => long.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : 0L;

    /// <summary>The value as a fractional number. Zero when the payload does not parse.</summary>
    /// <returns>The parsed number.</returns>
    public double AsNumber()
        => double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0d;

    /// <summary>The value as a boolean. False when the payload does not parse.</summary>
    /// <returns>The parsed boolean.</returns>
    public bool AsBoolean() => bool.TryParse(Text, out bool value) && value;

    /// <summary>The value as a list of strings. An empty list when there is no list payload.</summary>
    /// <returns>The list payload.</returns>
    public IReadOnlyList<string> AsStringList() => Items ?? Array.Empty<string>();

    /// <summary>
    ///     The value as it is shown to a human: the payload for a scalar, the
    ///     comma-joined items for a list.
    /// </summary>
    /// <returns>The display form.</returns>
    public override string ToString() => Type switch
    {
        PluginConfigFieldType.StringList => Items is { Count: > 0 } ? string.Join(", ", Items) : "(empty)",
        _ => Text ?? string.Empty,
    };
}

/// <summary>
///     One setting a plugin declares in its configuration section.
/// </summary>
public sealed class PluginConfigField
{
    /// <summary>
    ///     The setting's name, as it is written in the configuration file
    ///     (<c>plugins.&lt;plugin&gt;.&lt;name&gt;</c>).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The type of the setting.</summary>
    public PluginConfigFieldType Type { get; init; } = PluginConfigFieldType.String;

    /// <summary>
    ///     The value the setting takes when the user does not set it.
    ///     <see langword="null" /> when the plugin declares no default, which
    ///     makes the setting REQUIRED: the user has to give it a value.
    /// </summary>
    public PluginConfigValue? Default { get; init; }

    /// <summary>
    ///     One line describing the setting, shown next to it in the generated
    ///     settings form.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    ///     The lowest value the setting accepts, for a numeric setting.
    ///     <see langword="null" /> when the setting has no lower bound.
    /// </summary>
    public double? Minimum { get; init; }

    /// <summary>
    ///     The highest value the setting accepts, for a numeric setting.
    ///     <see langword="null" /> when the setting has no upper bound.
    /// </summary>
    public double? Maximum { get; init; }

    /// <summary>
    ///     The values the setting accepts, for a setting with a fixed set of
    ///     them. <see langword="null" /> when the setting accepts any value of
    ///     its type.
    /// </summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    /// <summary>
    ///     Whether the setting holds a secret. A secret is never shown in
    ///     clear in the generated settings form.
    /// </summary>
    public bool IsSecret { get; init; }
}

/// <summary>
///     What a plugin declares about its own configuration section: the settings
///     it has, their types, their defaults and their ranges.
/// </summary>
public sealed class PluginConfigSchema
{
    /// <summary>
    ///     The declared settings, in the order the plugin declared them.
    /// </summary>
    public IReadOnlyList<PluginConfigField> Fields { get; init; } = Array.Empty<PluginConfigField>();

    /// <summary>
    ///     The setting with this name, or <see langword="null" /> when the
    ///     plugin does not declare it.
    /// </summary>
    /// <param name="name">The setting's name.</param>
    /// <returns>The declared setting, or <see langword="null" />.</returns>
    public PluginConfigField? Find(string name)
    {
        if (name is null)
        {
            return null;
        }

        for (int i = 0; i < Fields.Count; i++)
        {
            if (string.Equals(Fields[i].Name, name, StringComparison.Ordinal))
            {
                return Fields[i];
            }
        }

        return null;
    }
}
