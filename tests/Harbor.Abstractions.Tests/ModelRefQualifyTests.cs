using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Issue #678: <see cref="ModelRef.Qualify" /> is the one function that reads
///     a model selection against a provider the caller already knows. These tests
///     pin the rule it encodes, and above all the one rule that must never be
///     "simplified" away.
/// </summary>
/// <remarks>
///     The three UI call sites this replaces each re-derived the read by hand, and
///     they did not agree with <see cref="ModelRef.TryParse" />: they left the
///     provider string raw, and they had no way to tell a redundant
///     <c>provider/</c> prefix from a slash that is part of a model id. The naive
///     unification — "a slash means the text is already qualified", which is what
///     the free-text sites want — would break the default install, and that is
///     what <see cref="MultiSegmentBareModelId_IsNotReadAsAProvider" /> exists to
///     prevent.
/// </remarks>
public class ModelRefQualifyTests
{
    [Test]
    public async Task BareModelId_TakesTheGivenProvider()
    {
        Result<ModelRef> result = ModelRef.Qualify("kilocode", "gpt-4o");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ToString()).IsEqualTo("kilocode/gpt-4o");
    }

    [Test]
    public async Task RedundantPrefixForTheSameProvider_IsDropped()
    {
        // What HARBOR_MODEL and the desktop settings screen write: the model half
        // already carries the provider it belongs to.
        Result<ModelRef> result = ModelRef.Qualify("kilocode", "kilocode/tencent/hy3:free");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ProviderId.Value).IsEqualTo("kilocode");
        await Assert.That(result.Value.ModelId).IsEqualTo("tencent/hy3:free");
    }

    [Test]
    public async Task MultiSegmentBareModelId_IsNotReadAsAProvider()
    {
        // THE regression this method exists to prevent. "tencent/hy3:free" is
        // kilocode's own DEFAULT model id (providers/kilocode.json), and a model
        // id may contain slashes. Reading its first segment as a provider would
        // turn a default install into a nonexistent "tencent" provider, so the
        // prefix is dropped only when it names the given provider.
        Result<ModelRef> result = ModelRef.Qualify("kilocode", "tencent/hy3:free");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ProviderId.Value).IsEqualTo("kilocode");
        await Assert.That(result.Value.ModelId).IsEqualTo("tencent/hy3:free");
    }

    [Test]
    public async Task TheShippedDefaultModel_SurvivesTheRoundTrip()
    {
        // IdentityConfig.FallbackModel, spelled out rather than referenced: this
        // project cannot reference Harbor.Application. DefaultModelSingleSourceTests
        // owns that constant; this test owns the RULE applied to it.
        Result<ModelRef> result = ModelRef.Qualify("kilocode", "kilocode/tencent/hy3:free");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ToString()).IsEqualTo("kilocode/tencent/hy3:free");
    }

    [Test]
    public async Task PrefixForADifferentProvider_StaysPartOfTheModelId()
    {
        // The given provider is authoritative, so a mismatch is not silently
        // re-pointed at another provider — the model id keeps every segment.
        Result<ModelRef> result = ModelRef.Qualify("kilocode", "otherprov/some-model");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ToString()).IsEqualTo("kilocode/otherprov/some-model");
    }

    [Test]
    public async Task ProviderId_IsNormalizedRatherThanPassedThrough()
    {
        // The bug this replaces handed "KiloCode" to a running Session verbatim.
        Result<ModelRef> result = ModelRef.Qualify("KiloCode", "tencent/hy3:free");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ProviderId.Value).IsEqualTo("kilocode");
    }

    [Test]
    public async Task SurroundingWhitespace_IsTrimmed()
    {
        Result<ModelRef> result = ModelRef.Qualify("kilocode", "  tencent/hy3:free  ");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ModelId).IsEqualTo("tencent/hy3:free");
    }

    [Test]
    public async Task RedundantPrefix_IsMatchedCaseInsensitively()
    {
        // ProviderId normalizes to lower case, so a mixed-case prefix is the same
        // provider and is redundant for the same reason.
        Result<ModelRef> result = ModelRef.Qualify("kilocode", "KiloCode/tencent/hy3:free");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ToString()).IsEqualTo("kilocode/tencent/hy3:free");
    }

    [Test]
    public async Task InvalidProviderId_Fails()
    {
        await Assert.That(ModelRef.Qualify("kilo code", "x").IsSuccess).IsFalse();
        await Assert.That(ModelRef.Qualify("kilo.code", "x").IsSuccess).IsFalse();
    }

    [Test]
    public async Task MissingProviderId_Fails()
    {
        await Assert.That(ModelRef.Qualify(null, "x").IsSuccess).IsFalse();
        await Assert.That(ModelRef.Qualify("", "x").IsSuccess).IsFalse();
        await Assert.That(ModelRef.Qualify("   ", "x").IsSuccess).IsFalse();
    }

    [Test]
    public async Task MissingModelId_Fails()
    {
        await Assert.That(ModelRef.Qualify("kilocode", null).IsSuccess).IsFalse();
        await Assert.That(ModelRef.Qualify("kilocode", "").IsSuccess).IsFalse();
        await Assert.That(ModelRef.Qualify("kilocode", "  ").IsSuccess).IsFalse();
    }

    [Test]
    public async Task NeverThrows()
    {
        // A Try-shaped factory on the startup path of every app: an exception here
        // would reach callers as something that looks like a crash in identity
        // resolution rather than a config problem.
        foreach (string provider in new[] { "kilocode", "Kilo Code", "", " ", null! })
        {
            foreach (string model in new[] { "x", "a/b", "a/b/c", "provider/ ", "  ", null! })
            {
                Result<ModelRef> result = ModelRef.Qualify(provider, model);
                await Assert.That(result.IsSuccess || result.IsFailure).IsTrue();
            }
        }
    }
}
