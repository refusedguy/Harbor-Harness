using Harbor.Application.Configuration;
namespace Harbor.Config.Tests;
/// <summary>
///     #195 (immutability batch): config-section <c>Default</c> singletons are
///     fresh instances per access, so no shared-mutable process-wide global
///     can leak through them. Record value equality is preserved.
/// </summary>
public class ConfigDefaultsTests
{
    [Test]
    public async Task Default_ReturnsFreshInstance_PerAccess()
    {
        await Assert.That(ReferenceEquals(IdentityConfig.Default, IdentityConfig.Default)).IsFalse();
        await Assert.That(ReferenceEquals(ToolingConfig.Default, ToolingConfig.Default)).IsFalse();
        await Assert.That(ReferenceEquals(CostConfig.Default, CostConfig.Default)).IsFalse();
        await Assert.That(ReferenceEquals(CompactionConfig.Default, CompactionConfig.Default)).IsFalse();
        await Assert.That(ReferenceEquals(CellForgeUiConfig.Default, CellForgeUiConfig.Default)).IsFalse();
        await Assert.That(ReferenceEquals(PresentationConfig.Default, PresentationConfig.Default)).IsFalse();
        await Assert.That(ReferenceEquals(RunLimitsConfig.Default, RunLimitsConfig.Default)).IsFalse();
        await Assert.That(ReferenceEquals(ProviderConfigEntry.Default, ProviderConfigEntry.Default)).IsFalse();
    }

    [Test]
    public async Task PresentationDefault_CellForge_NotSharedAcrossInstances()
    {
        var first = PresentationConfig.Default;
        var second = PresentationConfig.Default;

        await Assert.That(ReferenceEquals(first.CellForge, second.CellForge)).IsFalse();
        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task IdentityDefault_EqualsFallbackSelection()
    {
        var identity = IdentityConfig.Default;

        await Assert.That(identity.EffectiveProvider.Value).IsEqualTo(IdentityConfig.FallbackProvider);
        await Assert.That(identity.EffectiveAgent.Value).IsEqualTo(IdentityConfig.FallbackAgent);
    }
}
