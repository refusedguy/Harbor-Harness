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
    // #1770: the qualified-model vectors used to be one copy-pasted method
    // each. The assert never changed — only the (provider, model) pair did.
    // Every row keeps its original test name as the case display name.
    [Test]
    [Arguments("kilocode", "gpt-4o", "kilocode/gpt-4o", DisplayName = "BareModelId_TakesTheGivenProvider")]
    // IdentityConfig.FallbackModel, spelled out rather than referenced: this
    // project cannot reference Harbor.Application. DefaultModelSingleSourceTests
    // owns that constant; this test owns the RULE applied to it.
    [Arguments("kilocode", "kilocode/tencent/hy3:free", "kilocode/tencent/hy3:free", DisplayName = "TheShippedDefaultModel_SurvivesTheRoundTrip")]
    // The given provider is authoritative, so a mismatch is not silently
    // re-pointed at another provider, the model id keeps every segment.
    [Arguments("kilocode", "otherprov/some-model", "kilocode/otherprov/some-model", DisplayName = "PrefixForADifferentProvider_StaysPartOfTheModelId")]
    // ProviderId normalizes to lower case, so a mixed-case prefix is the same
    // provider and is redundant for the same reason.
    [Arguments("kilocode", "KiloCode/tencent/hy3:free", "kilocode/tencent/hy3:free", DisplayName = "RedundantPrefix_IsMatchedCaseInsensitively")]
    public async Task Qualify_ProducesTheExpectedQualifiedModel(string provider, string model, string expected)
    {
        Result<ModelRef> result = ModelRef.Qualify(provider, model);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ToString()).IsEqualTo(expected);
    }

    [Test]
    // What HARBOR_MODEL and the desktop settings screen write: the model half
    // already carries the provider it belongs to.
    [Arguments("kilocode", "kilocode/tencent/hy3:free", "kilocode", "tencent/hy3:free", DisplayName = "RedundantPrefixForTheSameProvider_IsDropped")]
    // THE regression this method exists to prevent. "tencent/hy3:free" is
    // kilocode's own DEFAULT model id (providers/kilocode.json), and a model
    // id may contain slashes. Reading its first segment as a provider would
    // turn a default install into a nonexistent "tencent" provider, so the
    // prefix is dropped only when it names the given provider.
    [Arguments("kilocode", "tencent/hy3:free", "kilocode", "tencent/hy3:free", DisplayName = "MultiSegmentBareModelId_IsNotReadAsAProvider")]
    public async Task Qualify_SplitsProviderAndModelId(string provider, string model, string expectedProvider, string expectedModel)
    {
        Result<ModelRef> result = ModelRef.Qualify(provider, model);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ProviderId.Value).IsEqualTo(expectedProvider);
        await Assert.That(result.Value.ModelId).IsEqualTo(expectedModel);
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
