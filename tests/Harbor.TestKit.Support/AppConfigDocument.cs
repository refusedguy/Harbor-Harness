// AppConfigDocument.cs — the app's `config.json` document as a test fixture
// (#1822).
//
// WHY IT IS HERE AND NOT IN A TEST PROJECT
// ----------------------------------------
// Three test projects write this document: `Harbor.App.Avalonia.Tests` and
// `Harbor.E2E.App.Avalonia` (twice — the UI driver and the component base).
// The document is a FIXTURE, not a DTO of a test's own, and the source
// generator registers types by name — so a shared fixture wants a shared
// home. `Harbor.TestKit.Support` is that home: it reaches
// `Harbor.Abstractions` alone, so the fixture costs a consumer no product
// assembly.
//
// WHY IT IS NOT THE PRODUCT'S CONFIG TYPE
// ---------------------------------------
// The product reads `config.json` through `ConfigJson.CommonConfigInfo` (an
// internal type of `Harbor.Hosting`, camelCase, indented, nulls omitted,
// with immutable-collection converters). Writing THAT type from a test would
// change the document the test writes: more members, indented, and with the
// nulls dropped. A test that writes a config file and then boots the app
// against it must write the file it means to write — so the fixture pins the
// seven members the tests actually put on disk, in the order and with the
// spellings they have always used.

using System.Text.Json.Serialization;

namespace Harbor.TestKit;

/// <summary>
///     The <c>config.json</c> document a test writes before booting the app.
/// </summary>
public sealed record AppConfigDocument(
    [property: JsonPropertyName("configVersion")] string ConfigVersion,
    [property: JsonPropertyName("onboardingCompleted")] bool OnboardingCompleted,
    [property: JsonPropertyName("storageBackend")] string StorageBackend,
    [property: JsonPropertyName("logLevel")] string LogLevel,
    [property: JsonPropertyName("defaultProvider")] string DefaultProvider,
    [property: JsonPropertyName("defaultModel")] string DefaultModel,
    [property: JsonPropertyName("defaultAgent")] string DefaultAgent);
