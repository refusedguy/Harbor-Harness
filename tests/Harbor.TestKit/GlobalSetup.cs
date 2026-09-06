using TUnit.Core;
using TUnit.Core.Enums;
using TUnit.Mocks;

namespace Harbor.TestKit;

/// <summary>Global test configuration for TUnit.</summary>
public static class GlobalSetup
{
    /// <summary>Set strict mock behavior for all TUnit mocks.</summary>
    [Before(HookType.TestDiscovery)]
    public static void Configure(BeforeTestDiscoveryContext ctx) => ctx.Settings.Mocks.DefaultMode = MockBehavior.Strict;
}
