using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Terminal.Abstractions.ViewModels;
using TUnit.Assertions;

namespace Harbor.Terminal.Abstractions.Tests;

/// <summary>
///     T2 reactive conventions (#1184): dirty flags and compute_* on
///     <see cref="ReactiveViewModel" />. The On&lt;Name&gt;Changed hooks are
///     EMITTED by ReactiveHookGenerator from the [Reactive] annotations
///     (issue #1542) — the stub declares fields only, no manual partials.
/// </summary>
public partial class ReactiveViewModelTests
{
    public sealed partial class StubVm : ReactiveViewModel
    {
        [ObservableProperty]
        [Reactive]
        private string _label = string.Empty;

        [ObservableProperty]
        [Reactive(Layout = true)]
        private int _rows;

        [ObservableProperty]
        [Reactive(NonNegative = true)]
        private int _count;

        public override string Id => "stub";

        // compute_*: read-only derivation, never two-field-synced.
        public string Computed => $"label={Label} rows={Rows}";
    }

    [Test]
    public async Task Initial_IsClean()
    {
        var vm = new StubVm();
        await Assert.That(vm.IsDirty).IsFalse();
        await Assert.That(vm.NeedsRepaint).IsFalse();
        await Assert.That(vm.NeedsLayout).IsFalse();
    }

    [Test]
    public async Task ReactiveField_MarksRepaintOnly()
    {
        var vm = new StubVm();
        vm.Label = "hi";
        await Assert.That(vm.NeedsRepaint).IsTrue();
        await Assert.That(vm.NeedsLayout).IsFalse();
    }

    [Test]
    public async Task LayoutField_MarksLayoutAndRepaint()
    {
        var vm = new StubVm();
        vm.Rows = 3;
        await Assert.That(vm.NeedsLayout).IsTrue();
        await Assert.That(vm.NeedsRepaint).IsTrue();
    }

    [Test]
    public async Task ClearDirty_ResetsBothFlags()
    {
        var vm = new StubVm();
        vm.Rows = 3;
        vm.ClearDirty();
        await Assert.That(vm.IsDirty).IsFalse();
    }

    [Test]
    public async Task CoerceNegativeSet_FloorsToZero()
    {
        var vm = new StubVm();
        vm.Count = -5;
        await Assert.That(vm.Count).IsEqualTo(0);
        await Assert.That(vm.NeedsRepaint).IsTrue();
    }

    [Test]
    public async Task GeneratedHook_MarksRepaintOnEveryChange()
    {
        var vm = new StubVm();
        vm.Label = "a";
        vm.ClearDirty();
        vm.Label = "b";
        await Assert.That(vm.NeedsRepaint).IsTrue();
    }

    [Test]
    public async Task Compute_DerivesFromFields()
    {
        var vm = new StubVm { Label = "x", Rows = 2 };
        await Assert.That(vm.Computed).IsEqualTo("label=x rows=2");
    }
}
