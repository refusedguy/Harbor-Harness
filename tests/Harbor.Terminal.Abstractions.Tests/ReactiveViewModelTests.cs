using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Terminal.Abstractions.ViewModels;
using TUnit.Assertions;

namespace Harbor.Terminal.Abstractions.Tests;

/// <summary>
///     T2 reactive conventions (#1184): dirty flags, watch_*, validate_*,
///     compute_* on <see cref="ReactiveViewModel" />.
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
        [Reactive]
        private int _count;

        public override string Id => "stub";

        // compute_*: read-only derivation, never two-field-synced.
        public string Computed => $"label={Label} rows={Rows}";

        public int WatchCalls { get; private set; }

        private void Watch_Label(string newValue)
        {
            WatchCalls++;
            MarkRepaint();
        }

        partial void OnLabelChanged(string value) => Watch_Label(value);

        private static int Validate_Count(int value) => NonNegative(value);

        partial void OnCountChanged(int value)
        {
            int coerced = Validate_Count(value);
            if (coerced != value)
            {
                Count = coerced;
                return;
            }
            MarkRepaint();
        }

        partial void OnRowsChanged(int value) => MarkLayout();
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
    public async Task RepaintField_MarksRepaintOnly()
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
    public async Task Validate_CoercesNegativeToZero()
    {
        var vm = new StubVm();
        vm.Count = -5;
        await Assert.That(vm.Count).IsEqualTo(0);
        await Assert.That(vm.NeedsRepaint).IsTrue();
    }

    [Test]
    public async Task Watch_InvokedOnChange()
    {
        var vm = new StubVm();
        vm.Label = "a";
        vm.Label = "b";
        await Assert.That(vm.WatchCalls).IsEqualTo(2);
    }

    [Test]
    public async Task Compute_DerivesFromFields()
    {
        var vm = new StubVm { Label = "x", Rows = 2 };
        await Assert.That(vm.Computed).IsEqualTo("label=x rows=2");
    }
}
