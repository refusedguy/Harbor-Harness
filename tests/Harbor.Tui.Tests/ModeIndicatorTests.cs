using Harbor.Terminal.Abstractions.ViewModels;
using TUnit.Assertions;

namespace Harbor.Tui.Tests;

/// <summary>
///     PX5 slice 1 (#1251): the status bar carries the mode indicator — the
///     <c>agent:</c> cell follows the active agent/mode.
/// </summary>
public class ModeIndicatorTests
{
    [Test]
    public async Task StatusBar_DefaultsToCode()
    {
        var vm = new StatusBarViewModel();

        await Assert.That(vm.Agent).IsEqualTo("code");
        await Assert.That(vm.Formatted).Contains("agent: code");
    }

    [Test]
    public async Task StatusBar_ShowsSwitchedMode()
    {
        var vm = new StatusBarViewModel
        {
            Provider = "kilocode",
            Model = "hy3",
        };

        vm.Agent = "ask";

        await Assert.That(vm.Agent).IsEqualTo("ask");
        await Assert.That(vm.Formatted).Contains("agent: ask");
        await Assert.That(vm.Formatted.Contains("agent: code")).IsFalse();
    }
}
