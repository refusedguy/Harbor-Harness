using TUnit.Core;
using TUnit.Mocks;

namespace Harbor.TestKit;

public static class GlobalSetup
{
    [Before(HookType.TestDiscovery)]
    public static void Setup(BeforeTestDiscoveryContext context)
    {
        context.Settings.Mocks.DefaultMode = MockBehavior.Strict;
    }
}
