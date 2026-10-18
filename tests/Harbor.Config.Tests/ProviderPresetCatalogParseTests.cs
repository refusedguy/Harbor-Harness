using Harbor.Application.Configuration;

namespace Harbor.Config.Tests;

/// <summary>
///     #1505: <c>ProviderPresetCatalog.Parse</c> may not encode "not a usable
///     provider config" as a <see langword="null" /> pointer (§ROP-001) — the
///     absence travels as <see cref="CSharpFunctionalExtensions.Maybe{T}" />.
///     Parse itself is private, so these tests pin the contract through its only
///     public surface, <see cref="ProviderPresetCatalog.FromJson" />, arguing from
///     the outside in: whatever is malformed, not an object, or id-less must be
///     skipped silently, and it must not poison the valid configs around it.
/// </summary>
public class ProviderPresetCatalogParseTests
{
    // ---- malformed JSON (the former `catch (JsonException) return null` arm) ----

    [Test]
    public async Task FromJson_MalformedJson_IsSkipped()
    {
        var presets = ProviderPresetCatalog.FromJson(new[]
        {
            "{",                      // unclosed object
            "}",                      // lone closer
            "not json at all",        // bare text
            "",                       // empty payload
            """{ "id": "good" }""",   // the survivor
        });

        await Assert.That(presets.Count).IsEqualTo(1);
        await Assert.That(presets[0].Id).IsEqualTo("good");
    }

    // ---- non-object roots (the former `ValueKind != Object return null` arm) ----

    [Test]
    public async Task FromJson_NonObjectRoot_IsSkipped()
    {
        var presets = ProviderPresetCatalog.FromJson(new[]
        {
            "[]",                     // array
            "\"a bare string\"",      // string
            "42",                     // number
            "true",                   // bool
            "null",                   // JSON null
            """{ "id": "good" }""",   // the survivor
        });

        await Assert.That(presets.Count).IsEqualTo(1);
        await Assert.That(presets[0].Id).IsEqualTo("good");
    }

    // ---- no id / unusable id (the former `IsNullOrWhiteSpace return null` arm) ----

    [Test]
    public async Task FromJson_UsableIdRequired()
    {
        var presets = ProviderPresetCatalog.FromJson(new[]
        {
            """{ "displayName": "No id at all" """,   // would be malformed too — see below
            """{ "displayName": "No id here" }""",    // id absent
            """{ "id": null, "x": 1 }""",             // id JSON-null
            """{ "id": "" }""",                       // id empty
            """{ "id": "   " }""",                    // id whitespace-only
            """{ "id": 42, "x": 1 }""",               // id not a string
            """{ "id": "good" }""",                   // the survivor
        });

        await Assert.That(presets.Count).IsEqualTo(1);
        await Assert.That(presets[0].Id).IsEqualTo("good");
    }

    // ---- the broken ones must not take valid neighbours down with them ----

    [Test]
    public async Task FromJson_BrokenConfigs_DontAffectValidPeers()
    {
        const string first = """{ "id": "first", "priority": 90, "baseUrl": "http://x/v1" }""";
        const string last = """{ "id": "last", "priority": 10, "baseUrl": "http://x/v1" }""";

        // Every invalid payload sits *between* two valid ones.
        var presets = ProviderPresetCatalog.FromJson(new[]
        {
            first,
            "{", "[]", """{ "id": "" }""", "not json",
            last,
        });

        await Assert.That(presets.Count).IsEqualTo(2);
        await Assert.That(string.Join(",", presets.Select(p => p.Id))).IsEqualTo("last,first");
    }
}
