using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Abstractions;
using TUnit.Assertions;

namespace Harbor.Plugins.Abstractions.Tests;

/// <summary>
///     #1734: a plugin declares its settings as data, the host checks the
///     user's section against that declaration, and the declaration generates
///     the settings form. These tests cover the three halves.
/// </summary>
public sealed class PluginConfigTests
{
    // ── The schema reader ──────────────────────────────────────────────────────

    /// <summary>
    ///     A manifest's <c>config</c> object becomes a schema: the settings,
    ///     their types, their defaults, their ranges and their descriptions.
    /// </summary>
    [Test]
    public async Task SchemaReader_ReadsTheDeclaredSettings()
    {
        Result<PluginConfigSchema> schema = Read("""
            {
              "endpoint": { "type": "string", "default": "https://api.example.com" },
              "maxResults": { "type": "integer", "default": 5, "minimum": 1, "maximum": 50,
                              "description": "How many results a search returns." },
              "apiKey": { "type": "string", "secret": true },
              "verbose": { "type": "boolean", "default": false },
              "tags": { "type": "stringList", "default": [] },
              "mode": { "type": "string", "default": "fast", "allowed": ["fast", "thorough"] }
            }
            """);

        await Assert.That(schema.IsSuccess).IsTrue();
        PluginConfigSchema value = schema.Value;
        await Assert.That(value.Fields.Count).IsEqualTo(6);

        PluginConfigField? maxResults = value.Find("maxResults");
        await Assert.That(maxResults).IsNotNull();
        await Assert.That(maxResults!.Type).IsEqualTo(PluginConfigFieldType.Integer);
        await Assert.That(maxResults.Default!.AsInteger()).IsEqualTo(5L);
        await Assert.That(maxResults.Minimum).IsEqualTo(1d);
        await Assert.That(maxResults.Maximum).IsEqualTo(50d);
        await Assert.That(maxResults.Description).IsEqualTo("How many results a search returns.");

        await Assert.That(value.Find("apiKey")!.IsSecret).IsTrue();
        await Assert.That(value.Find("apiKey")!.Default).IsNull()
            .Because("a setting with no default is a setting the user has to give a value");
        await Assert.That(value.Find("verbose")!.Type).IsEqualTo(PluginConfigFieldType.Boolean);
        await Assert.That(value.Find("tags")!.Type).IsEqualTo(PluginConfigFieldType.StringList);
        await Assert.That(value.Find("mode")!.AllowedValues!).Contains("thorough");
        await Assert.That(value.Find("nothing")).IsNull();
    }

    /// <summary>
    ///     A type the host does not know is refused, and the message names the
    ///     setting. Reading it as a string would give the user a setting that
    ///     behaves differently from the one the plugin meant.
    /// </summary>
    [Test]
    public async Task SchemaReader_UnknownType_RefusesAndNamesTheSetting()
    {
        Result<PluginConfigSchema> schema = Read("""{ "port": { "type": "interger" } }""");

        await Assert.That(schema.IsFailure).IsTrue();
        await Assert.That(schema.Error).Contains("port");
        await Assert.That(schema.Error).Contains("interger");
    }

    /// <summary>
    ///     A key the host does not know is refused, and the message names the
    ///     setting and the key: a typo in a schema is a setting the plugin
    ///     meant and the host dropped.
    /// </summary>
    [Test]
    public async Task SchemaReader_UnknownKey_RefusesAndNamesTheSetting()
    {
        Result<PluginConfigSchema> schema = Read("""{ "port": { "type": "integer", "min": 1 } }""");

        await Assert.That(schema.IsFailure).IsTrue();
        await Assert.That(schema.Error).Contains("port");
        await Assert.That(schema.Error).Contains("min");
    }

    /// <summary>
    ///     A default that is not of the declared type is refused: the schema
    ///     would promise a number and hand the plugin text.
    /// </summary>
    [Test]
    public async Task SchemaReader_DefaultOfTheWrongType_RefusesAndNamesTheSetting()
    {
        Result<PluginConfigSchema> schema = Read("""{ "port": { "type": "integer", "default": "8080" } }""");

        await Assert.That(schema.IsFailure).IsTrue();
        await Assert.That(schema.Error).Contains("port");
    }

    /// <summary>
    ///     A setting declared twice is refused: two declarations of one setting
    ///     are two truths, and the second one is the one nobody reads.
    /// </summary>
    [Test]
    public async Task SchemaReader_SettingDeclaredTwice_Refuses()
    {
        Result<PluginConfigSchema> schema = Read("""{ "port": { "type": "integer" }, "port": { "type": "string" } }""");

        await Assert.That(schema.IsFailure).IsTrue();
        await Assert.That(schema.Error).Contains("port");
    }

    // ── The validator ──────────────────────────────────────────────────────────

    /// <summary>
    ///     A setting the user did not set takes the default the schema
    ///     published, so the plugin reads the same value either way.
    /// </summary>
    [Test]
    public async Task Validator_UnsetSetting_TakesTheDeclaredDefault()
    {
        PluginConfigSchema schema = Schema("""
            { "maxResults": { "type": "integer", "default": 5 } }
            """);

        Result<IReadOnlyDictionary<string, PluginConfigValue>> resolved =
            PluginConfigValidator.Validate(schema, Section("{}"), "plugins.web-search");

        await Assert.That(resolved.IsSuccess).IsTrue();
        await Assert.That(resolved.Value["maxResults"].AsInteger()).IsEqualTo(5L);
    }

    /// <summary>
    ///     A setting the schema declares no default for has to be given a
    ///     value: there is nothing to fall back to.
    /// </summary>
    [Test]
    public async Task Validator_RequiredSettingMissing_RefusesAndNamesTheSetting()
    {
        PluginConfigSchema schema = Schema("""{ "apiKey": { "type": "string" } }""");

        Result<IReadOnlyDictionary<string, PluginConfigValue>> resolved =
            PluginConfigValidator.Validate(schema, Section("{}"), "plugins.web-search");

        await Assert.That(resolved.IsFailure).IsTrue();
        await Assert.That(resolved.Error).Contains("plugins.web-search.apiKey");
    }

    /// <summary>
    ///     A setting the schema does not declare is an error, and the message
    ///     names it: a user who cannot see which setting is wrong cannot fix
    ///     it.
    /// </summary>
    [Test]
    public async Task Validator_UnknownSetting_RefusesAndNamesTheSetting()
    {
        PluginConfigSchema schema = Schema("""{ "maxResults": { "type": "integer", "default": 5 } }""");

        Result<IReadOnlyDictionary<string, PluginConfigValue>> resolved =
            PluginConfigValidator.Validate(schema, Section("""{ "prot": 8080 }"""), "plugins.web-search");

        await Assert.That(resolved.IsFailure).IsTrue();
        await Assert.That(resolved.Error).Contains("plugins.web-search.prot");
        await Assert.That(resolved.Error).Contains("maxResults");
    }

    /// <summary>
    ///     A value of the wrong type is refused, and the message says what was
    ///     expected. A port written as text is not the number 8080.
    /// </summary>
    [Test]
    public async Task Validator_WrongType_RefusesAndNamesTheSetting()
    {
        PluginConfigSchema schema = Schema("""{ "maxResults": { "type": "integer", "default": 5 } }""");

        Result<IReadOnlyDictionary<string, PluginConfigValue>> resolved =
            PluginConfigValidator.Validate(schema, Section("""{ "maxResults": "10" }"""), "plugins.web-search");

        await Assert.That(resolved.IsFailure).IsTrue();
        await Assert.That(resolved.Error).Contains("plugins.web-search.maxResults");
        await Assert.That(resolved.Error).Contains("whole number");
    }

    /// <summary>
    ///     A value outside the declared range is refused, and the message names
    ///     the range the setting accepts.
    /// </summary>
    [Test]
    public async Task Validator_OutOfRange_RefusesAndNamesTheRange()
    {
        PluginConfigSchema schema = Schema("""
            { "maxResults": { "type": "integer", "default": 5, "minimum": 1, "maximum": 50 } }
            """);

        Result<IReadOnlyDictionary<string, PluginConfigValue>> resolved =
            PluginConfigValidator.Validate(schema, Section("""{ "maxResults": 100 }"""), "plugins.web-search");

        await Assert.That(resolved.IsFailure).IsTrue();
        await Assert.That(resolved.Error).Contains("plugins.web-search.maxResults");
        await Assert.That(resolved.Error).Contains("50");
    }

    /// <summary>
    ///     A value outside the declared set is refused, and the message lists
    ///     the values the setting accepts.
    /// </summary>
    [Test]
    public async Task Validator_NotAnAllowedValue_RefusesAndNamesTheSetting()
    {
        PluginConfigSchema schema = Schema("""
            { "mode": { "type": "string", "default": "fast", "allowed": ["fast", "thorough"] } }
            """);

        Result<IReadOnlyDictionary<string, PluginConfigValue>> resolved =
            PluginConfigValidator.Validate(schema, Section("""{ "mode": "slow" }"""), "plugins.web-search");

        await Assert.That(resolved.IsFailure).IsTrue();
        await Assert.That(resolved.Error).Contains("plugins.web-search.mode");
        await Assert.That(resolved.Error).Contains("thorough");
    }

    /// <summary>
    ///     A section that is not an object is refused, and the message names
    ///     the section.
    /// </summary>
    [Test]
    public async Task Validator_SectionIsNotAnObject_RefusesAndNamesTheSection()
    {
        PluginConfigSchema schema = Schema("""{ "maxResults": { "type": "integer", "default": 5 } }""");

        Result<IReadOnlyDictionary<string, PluginConfigValue>> resolved =
            PluginConfigValidator.Validate(schema, Section("\"nonsense\""), "plugins.web-search");

        await Assert.That(resolved.IsFailure).IsTrue();
        await Assert.That(resolved.Error).Contains("plugins.web-search");
    }

    /// <summary>
    ///     A section the user never wrote resolves to the declared defaults,
    ///     so a plugin that declares nothing but defaults starts.
    /// </summary>
    [Test]
    public async Task Validator_NoSectionAtAll_ResolvesToTheDefaults()
    {
        PluginConfigSchema schema = Schema("""{ "maxResults": { "type": "integer", "default": 5 } }""");

        Result<IReadOnlyDictionary<string, PluginConfigValue>> resolved =
            PluginConfigValidator.Validate(schema, default, "plugins.web-search");

        await Assert.That(resolved.IsSuccess).IsTrue();
        await Assert.That(resolved.Value["maxResults"].AsInteger()).IsEqualTo(5L);
    }

    /// <summary>
    ///     A list setting keeps its items, and a boolean keeps its truth.
    /// </summary>
    [Test]
    public async Task Validator_ListAndBoolean_KeepTheirValues()
    {
        PluginConfigSchema schema = Schema("""
            {
              "tags": { "type": "stringList", "default": [] },
              "verbose": { "type": "boolean", "default": false }
            }
            """);

        Result<IReadOnlyDictionary<string, PluginConfigValue>> resolved =
            PluginConfigValidator.Validate(
                schema,
                Section("""{ "tags": ["a", "b"], "verbose": true }"""),
                "plugins.web-search");

        await Assert.That(resolved.IsSuccess).IsTrue();
        await Assert.That(resolved.Value["tags"].AsStringList().Count).IsEqualTo(2);
        await Assert.That(resolved.Value["tags"].AsStringList()[1]).IsEqualTo("b");
        await Assert.That(resolved.Value["verbose"].AsBoolean()).IsTrue();
    }

    // ── The form ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     The form a schema generates carries the default and the range, and
    ///     the value the user actually has.
    /// </summary>
    [Test]
    public async Task Form_CarriesTheDefaultTheRangeAndTheCurrentValue()
    {
        PluginConfigSchema schema = Schema("""
            {
              "maxResults": { "type": "integer", "default": 5, "minimum": 1, "maximum": 50,
                              "description": "How many results a search returns." },
              "apiKey": { "type": "string", "secret": true }
            }
            """);

        Result<IReadOnlyDictionary<string, PluginConfigValue>> resolved =
            PluginConfigValidator.Validate(
                schema,
                Section("""{ "maxResults": 10, "apiKey": "secret" }"""),
                "plugins.web-search");

        await Assert.That(resolved.IsSuccess).IsTrue()
            .Because("the section satisfies the schema: every setting has a value, and the one with no default "
                + "was given one");

        IReadOnlyList<PluginConfigFormRow> rows = PluginConfigForm.Build(schema, resolved.Value);

        await Assert.That(rows.Count).IsEqualTo(2);

        PluginConfigFormRow maxResults = rows[0];
        await Assert.That(maxResults.Name).IsEqualTo("maxResults");
        await Assert.That(maxResults.Type).IsEqualTo(PluginConfigFieldType.Integer);
        await Assert.That(maxResults.Default!.AsInteger()).IsEqualTo(5L);
        await Assert.That(maxResults.Current!.AsInteger()).IsEqualTo(10L);
        await Assert.That(maxResults.Minimum).IsEqualTo(1d);
        await Assert.That(maxResults.Maximum).IsEqualTo(50d);
        await Assert.That(maxResults.Description).IsEqualTo("How many results a search returns.");

        PluginConfigFormRow apiKey = rows[1];
        await Assert.That(apiKey.IsSecret).IsTrue();
        await Assert.That(apiKey.Default).IsNull();
    }

    /// <summary>
    ///     A form built without a section shows the declared defaults as the
    ///     current values: the user has not set anything yet, and the form
    ///     still says what the plugin will use.
    /// </summary>
    [Test]
    public async Task Form_WithoutASection_ShowsTheDefaults()
    {
        PluginConfigSchema schema = Schema("""{ "maxResults": { "type": "integer", "default": 5 } }""");

        IReadOnlyList<PluginConfigFormRow> rows = PluginConfigForm.Build(schema, null);

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Current!.AsInteger()).IsEqualTo(5L);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Result<PluginConfigSchema> Read(string json) =>
        PluginConfigSchemaReader.Read(JsonDocument.Parse(json).RootElement, "a.harbor-plugin.json");

    private static PluginConfigSchema Schema(string json)
    {
        Result<PluginConfigSchema> schema = Read(json);
        if (schema.IsFailure)
        {
            throw new InvalidOperationException($"The test schema does not read: {schema.Error}");
        }

        return schema.Value;
    }

    private static JsonElement Section(string json) => JsonDocument.Parse(json).RootElement;
}
