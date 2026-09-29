using Harbor.Abstractions.Models.Identifiers;
using Harbor.Application.Configuration;

namespace Harbor.Config.Tests;

/// <summary>
///     Issue #599: the default model id is spelled once, and every consumer derives
///     from that one spelling.
/// </summary>
/// <remarks>
///     <para>
///         The defect was not duplication for its own sake. <c>ConfigSections.cs</c>
///         declared the default as a <c>const string</c> and then, seven lines later,
///         hardcoded a second copy recovered by <c>"tencent/hy3:free".Split('/')[1]</c> —
///         and that expression yields <c>"hy3:free"</c>. The two copies disagreed, and
///         the disagreement was user-visible: <see cref="HarborConfig.Model" /> reads
///         the typed half, so a default install answered <c>kilocode/hy3:free</c> while
///         the constant promised <c>kilocode/tencent/hy3:free</c> — and
///         <c>ToRaw()</c> persisted the truncated one into <c>config.json</c>.
///     </para>
///     <para>
///         Which copy was right is settled by <c>providers/kilocode.json</c>, the file
///         that declares <c>"defaultModel": "tencent/hy3:free"</c> for the gateway
///         (and from which <see cref="ProviderPresets" /> projects, since #580). The
///         bare <c>Split('/')[1]</c> copy was the wrong one and the live one; the
///         constant agreed with the provider and was only ever reached on the
///         <c>Model is null</c> path. So <c>tencent/hy3:free</c> wins, and the count-2
///         split inside <c>ModelRef.TryParse</c> is the only thing allowed to take a
///         model id apart.
///     </para>
/// </remarks>
public class DefaultModelIdentityTests
{
    /// <summary>
    ///     The decision, recorded. Changing this is a deliberate act, and the next
    ///     value has to be changed in <c>providers/kilocode.json</c> with it —
    ///     <see cref="FallbackModel_MatchesTheKilocodeProviderPreset" /> fails otherwise.
    /// </summary>
    private const string ExpectedDefaultModel = "kilocode/tencent/hy3:free";

    [Test]
    public async Task FallbackModel_IsTheRecordedDefault()
    {
        await Assert.That(IdentityConfig.FallbackModel).IsEqualTo(ExpectedDefaultModel);
    }

    [Test]
    public async Task FallbackModel_HasTheProviderSlashModelShape()
    {
        var parsed = ModelRef.TryParse(IdentityConfig.FallbackModel);

        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.ProviderId.Value)
            .IsEqualTo(IdentityConfig.FallbackProvider);
    }

    /// <summary>
    ///     The two copies, reconciled. This is the exact assertion that failed before
    ///     the fix: the typed default rendered <c>kilocode/hy3:free</c> while the
    ///     constant said <c>kilocode/tencent/hy3:free</c>.
    /// </summary>
    [Test]
    public async Task Default_Model_ToString_Equals_FallbackModel()
    {
        await Assert.That(IdentityConfig.Default.Model).IsNotNull();
        await Assert.That(IdentityConfig.Default.Model!.ToString())
            .IsEqualTo(IdentityConfig.FallbackModel);
    }

    /// <summary>
    ///     The truncation guard. <c>"tencent/hy3:free".Split('/')[1]</c> kept
    ///     <c>hy3:free</c> and dropped a segment; the model id of a gateway model is
    ///     routinely multi-segment (<c>kilo-auto/free</c>), so a bare
    ///     <c>Split('/')[1]</c> on this constant would quietly mangle any of them.
    /// </summary>
    [Test]
    public async Task Default_Model_KeepsEverySegmentOfTheDeclaredModelId()
    {
        string declared = IdentityConfig.FallbackModel;
        int firstSlash = declared.IndexOf('/');
        string modelHalf = firstSlash < 0 ? declared : declared[(firstSlash + 1)..];

        ModelRef actual = IdentityConfig.Default.Model!;

        await Assert.That(actual.ModelId).IsEqualTo(modelHalf);
        // Explicit shape assertion, so the case above fails with a readable diff:
        // the declared model half is multi-segment, therefore so must the typed one.
        await Assert.That(modelHalf.Contains("/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(actual.ModelId.Contains("/", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    ///     The constant is the C# spelling of what <c>providers/kilocode.json</c>
    ///     declares. That JSON is the provider's own contract and the source
    ///     <see cref="ProviderPresets" /> projects (#580), so this is what stops the
    ///     remaining copy from drifting away from the provider it names.
    /// </summary>
    [Test]
    public async Task FallbackModel_MatchesTheKilocodeProviderPreset()
    {
        var preset = ProviderPresets.Find(IdentityConfig.FallbackProvider);

        await Assert.That(preset).IsNotNull();
        await Assert.That(IdentityConfig.FallbackModel)
            .IsEqualTo(preset!.Id + "/" + preset.DefaultModel);
    }

    /// <summary>
    ///     What a default install actually shows: the legacy string property is what
    ///     <c>ToRaw()</c> persists and what the CLI <c>model</c> command prints.
    /// </summary>
    [Test]
    public async Task HarborConfig_Default_AnswersTheSameStringOnBothPaths()
    {
        HarborConfig config = HarborConfig.Default;

        await Assert.That(config.Model).IsEqualTo(IdentityConfig.FallbackModel);
        await Assert.That(config.EffectiveModel).IsEqualTo(IdentityConfig.FallbackModel);
    }

    /// <summary>
    ///     The "same unset state, two answers" half of the defect: an explicitly
    ///     cleared <c>Identity.Model</c> used to answer with the constant while the
    ///     pre-filled default answered with the truncated copy. Both are the same state
    ///     now, so both must answer identically.
    /// </summary>
    [Test]
    public async Task ClearedModel_AnswersTheSameStringAsThePreFilledDefault()
    {
        HarborConfig config = HarborConfig.Default;
        string preFilled = config.Model;

        // A rejected value clears the field — the state the issue called "unset".
        // Before the fix this answered with the constant while `preFilled` answered
        // with the truncated copy, so one "unset" state had two string answers.
        await Assert.That(config.TrySetModel("no-slash-model").IsFailure).IsTrue();
        await Assert.That(config.Identity.Model).IsNull();
        await Assert.That(config.Model).IsEqualTo(IdentityConfig.FallbackModel);
        await Assert.That(config.Model).IsEqualTo(preFilled);

        // …and the re-populated state agrees with it rather than diverging.
        await Assert.That(config.TrySetModel(IdentityConfig.FallbackModel).IsSuccess).IsTrue();
        await Assert.That(config.Model).IsEqualTo(IdentityConfig.FallbackModel);
        await Assert.That(config.EffectiveModel).IsEqualTo(IdentityConfig.FallbackModel);
    }
}
