using Harbor.Telemetry;

namespace Harbor.Telemetry.Tests;

/// <summary>
///     #1741: the redaction gate is the ONE place that decides what may leave
///     the process. These are its rules, pinned one by one — a gate that
///     nobody tested is a gate that quietly stopped gating.
/// </summary>
public class TelemetryRedactionTests
{
    /// <summary>A key that names a credential redacts its value, whatever the value is.</summary>
    [Test]
    public async Task ASensitiveKey_RedactsWhateverTheValueIs()
    {
        await Assert.That(TelemetryRedaction.Apply("api_key", "plain-looking-value"))
            .IsEqualTo(TelemetryRedaction.Redacted);
        await Assert.That(TelemetryRedaction.Apply("Authorization", "Bearer abc"))
            .IsEqualTo(TelemetryRedaction.Redacted);
        await Assert.That(TelemetryRedaction.Apply("client_secret", "abc"))
            .IsEqualTo(TelemetryRedaction.Redacted);
        await Assert.That(TelemetryRedaction.IsSensitiveKey("x-api-key")).IsTrue();
        await Assert.That(TelemetryRedaction.IsSensitiveKey("apiKey")).IsTrue();
    }

    /// <summary>
    ///     A credential SHAPE is redacted even under a key that does not name
    ///     it — a key-based rule alone would leak the moment a caller invents
    ///     a new attribute name.
    /// </summary>
    [Test]
    public async Task ASecretShapedValue_IsRedactedUnderABenignKey()
    {
        await Assert.That(TelemetryRedaction.Apply("note", "sk-abcdef0123456789"))
            .IsEqualTo(TelemetryRedaction.Redacted);
        await Assert.That(TelemetryRedaction.Apply("note", "ghp_abcdefghijklmnop"))
            .IsEqualTo(TelemetryRedaction.Redacted);
        await Assert.That(TelemetryRedaction.Apply("note", "Bearer abcdef"))
            .IsEqualTo(TelemetryRedaction.Redacted);
        await Assert.That(TelemetryRedaction.Apply("note", "api_key=abcdef"))
            .IsEqualTo(TelemetryRedaction.Redacted);
        await Assert.That(TelemetryRedaction.LooksLikeSecret("sk-abcdef0123456789")).IsTrue();
    }

    /// <summary>
    ///     The tags Harbor actually uses stay visible — a gate that redacts
    ///     everything is a gate that makes the sink useless.
    /// </summary>
    [Test]
    public async Task TheTagsHarborUses_StayVisible()
    {
        foreach (string key in new[]
                 {
                     TelemetryTagNames.SessionId,
                     TelemetryTagNames.Agent,
                     TelemetryTagNames.Turn,
                     TelemetryTagNames.ToolName,
                     TelemetryTagNames.ToolStatus,
                     TelemetryTagNames.Model,
                     TelemetryTagNames.LlmTokenType,
                     TelemetryTagNames.EventBusDispatchDurationMs,
                     TelemetryTagNames.DispatchQuantile,
                 })
        {
            await Assert.That(TelemetryRedaction.IsSensitiveKey(key)).IsFalse()
                .Because($"{key} is a tag Harbor publishes and must stay readable");
        }

        await Assert.That(TelemetryRedaction.Apply(TelemetryTagNames.SessionId, "0f8fad5b"))
            .IsEqualTo("0f8fad5b");
        await Assert.That(TelemetryRedaction.Apply(TelemetryTagNames.LlmTokenType, "prompt"))
            .IsEqualTo("prompt");
    }

    /// <summary>
    ///     An unknown type is summarised by its type name, never rendered: an
    ///     arbitrary object graph may hold anything.
    /// </summary>
    [Test]
    public async Task AnUnknownType_IsSummarizedNotRendered()
    {
        string rendered = TelemetryRedaction.Apply("payload", new SecretHolder { ApiKey = "sk-abcdef0123456789" });

        await Assert.That(rendered).IsEqualTo("[SecretHolder]");
        await Assert.That(rendered).DoesNotContain("sk-abcdef0123456789");
    }

    /// <summary>A control character cannot smuggle a field separator into the next record.</summary>
    [Test]
    public async Task ControlCharacters_AreEscaped()
    {
        string rendered = TelemetryRedaction.Apply("note", "a\nb\"c");

        await Assert.That(rendered).IsEqualTo("a\\nb\\\"c");
        await Assert.That(rendered).DoesNotContain("\n");
    }

    /// <summary>An over-long value is capped rather than written out in full.</summary>
    [Test]
    public async Task AnOverLongValue_IsCapped()
    {
        string rendered = TelemetryRedaction.Apply("note", new string('x', 4096), 32);

        await Assert.That(rendered.Length).IsEqualTo(32);
    }

    /// <summary>A name is sanitized, not redacted: a metric called llm.tokens must stay legible.</summary>
    [Test]
    public async Task AName_IsSanitizedNotRedacted()
    {
        await Assert.That(TelemetryRedaction.SanitizeName("llm.tokens")).IsEqualTo("llm.tokens");
        await Assert.That(TelemetryRedaction.SanitizeName("bad\nname")).IsEqualTo("bad_name");
        await Assert.That(TelemetryRedaction.SanitizeName(string.Empty)).IsEqualTo("unnamed");
    }

    /// <summary>The value holder that must never be rendered.</summary>
    private sealed class SecretHolder
    {
        public string ApiKey { get; init; } = "sk-abcdef0123456789";
    }
}
