using System.Text;
using Harbor.TestKit;
using Harbor.Telemetry;
using TUnit.Core;

namespace Harbor.Telemetry.Tests;

/// <summary>
///     #2685 T03: a generated-secret property over the telemetry export path.
///     <para>
///         The existing exporter test proves ONE credential under TWO keys.
///         That is an example, not a property: a regression that drops a whole
///         family of credential prefixes (a new provider key, a GitHub token
///         flavor) leaves every example green. This suite enumerates every
///         known pattern family in <c>TelemetryRedaction</c>, grows each into
///         several reproducible secrets, and asserts the same invariant at
///         both layers: the gate withholds the value, and the sink never emits
///         it. The span path is covered separately because it reaches the sink
///         through the activity listener rather than through
///         <see cref="TelemetryTextExporter.TryExport" />.
///     </para>
/// </summary>
[NotInParallel("telemetry")]
public class RedactionPropertyTests
{
    /// <summary>
    ///     Mirrors the private <c>SecretPrefixes</c> table in
    ///     <see cref="TelemetryRedaction" />. Kept as a census in the test so a
    ///     family cannot be dropped from production without this list going
    ///     stale and the failure being a visible one.
    /// </summary>
    private static readonly string[] SecretPrefixes =
    [
        "sk-", "sk_", "sk-ant-", "sk-proj-",
        "ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat_",
        "glpat-", "xoxb-", "xoxp-", "xoxa-", "xoxr-", "xokp-",
        "AKIA", "ASIA", "AIza", "ya29.", "eyJ",
        "hf_", "npm_", "pypi-", "dop_v1_", "klo_", "r8_", "rsk_", "SG.",
        "-----BEGIN", "Bearer ", "Basic ",
    ];

    /// <summary>Keys that are sensitive by name, whatever the value underneath.</summary>
    private static readonly string[] SensitiveKeys =
    [
        "api_key", "apiKey", "x-api-key", "authorization", "client_secret",
        "access_token", "refresh_token", "password", "credential",
        "private_key", "session_key", "cookie", "bearer",
    ];

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int SuffixLength = 24;

    /// <summary>The seeds this suite walks in an ordinary run.</summary>
    private static readonly IReadOnlyList<int> Seeds = TestSeeds.Values(1103, 2207, 3319);

    /// <summary>One generated credential per known family per seed.</summary>
    private static List<string> GeneratedSecrets()
    {
        var secrets = new List<string>(SecretPrefixes.Length * Seeds.Count);
        for (int i = 0; i < Seeds.Count; i++)
        {
            var rng = new Random(Seeds[i]);
            foreach (string prefix in SecretPrefixes)
            {
                secrets.Add(prefix + Suffix(rng, SuffixLength));
            }
        }

        return secrets;
    }

    /// <summary>Alphanumeric-only suffix: no escape sequence can hide a leak.</summary>
    private static string Suffix(Random rng, int length)
    {
        var text = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            text.Append(Alphabet[rng.Next(Alphabet.Length)]);
        }

        return text.ToString();
    }

    /// <summary>A sink whose writer is ours and which never flushes on a timer.</summary>
    private static TelemetryExportOptions Options() => new()
    {
        Target = TelemetryExportTarget.Console,
        FlushInterval = null,
    };

    /// <summary>The gate recognizes every generated family under a benign key.</summary>
    [Test]
    public async Task EveryKnownPattern_TheGateWithholdsIt()
    {
        List<string> secrets = GeneratedSecrets();

        foreach (string secret in secrets)
        {
            await Assert.That(TelemetryRedaction.LooksLikeSecret(secret)).IsTrue();
            await Assert.That(TelemetryRedaction.Apply("note", secret))
                .IsEqualTo(TelemetryRedaction.Redacted);
        }

        // Vacuity guard: a generator that silently yields nothing must fail.
        await Assert.That(secrets.Count).IsEqualTo(SecretPrefixes.Length * Seeds.Count);
    }

    /// <summary>No generated credential survives the exporter's writer path.</summary>
    [Test]
    public async Task EveryKnownPattern_TheSinkNeverEmitsIt()
    {
        List<string> secrets = GeneratedSecrets();
        var output = new StringWriter();
        using var exporter = new TelemetryTextExporter(Options(), new CapturingLogger(), output);

        foreach (string secret in secrets)
        {
            bool accepted = exporter.TryExport(
                "activity",
                "redaction.property",
                [new KeyValuePair<string, object?>("note", secret)]);
            await Assert.That(accepted).IsTrue();
        }

        exporter.Flush();
        string text = output.ToString();

        await Assert.That(text).Contains(TelemetryRedaction.Redacted);
        foreach (string secret in secrets)
        {
            await Assert.That(text).DoesNotContain(secret);
        }
    }

    /// <summary>A sensitive key withholds a generated value even if it looks benign.</summary>
    [Test]
    public async Task EverySensitiveKey_TheGateWithholdsItsValue()
    {
        for (int i = 0; i < SensitiveKeys.Length; i++)
        {
            string value = "plain-" + Suffix(new Random(i + 5000), SuffixLength);
            await Assert.That(TelemetryRedaction.IsSensitiveKey(SensitiveKeys[i])).IsTrue();
            await Assert.That(TelemetryRedaction.Apply(SensitiveKeys[i], value))
                .IsEqualTo(TelemetryRedaction.Redacted);
        }
    }

    /// <summary>The activity-listener path is gated too, not just TryExport.</summary>
    [Test]
    public async Task SecretInSpanTag_TheSinkNeverEmitsIt()
    {
        string secret = GeneratedSecrets()[0];
        var output = new StringWriter();
        using var exporter = new TelemetryTextExporter(Options(), new CapturingLogger(), output);

        using (ActivityTracer.Instance.StartSpan(
                   "redaction.property.span",
                   new KeyValuePair<string, object?>("llm.prompt", secret)))
        {
        }

        exporter.Flush();

        string text = output.ToString();
        await Assert.That(text).Contains("redaction.property.span");
        await Assert.That(text).DoesNotContain(secret);
    }
}
