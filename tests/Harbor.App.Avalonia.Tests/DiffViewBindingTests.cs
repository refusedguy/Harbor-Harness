using Avalonia.Controls;
using Avalonia.VisualTree;
using Harbor.App.Avalonia.Views;
using Harbor.Ui.Framework.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

/// <summary>
///     Regression test for issue #160: <c>DiffView.axaml</c> binds the
///     Framework side-by-side shape (<c>LeftText</c>/<c>RightText</c>/
///     <c>ComputeCommand</c>/<c>Rows</c>) while its <c>DataContext</c> was the
///     Desktop unified-diff VM (<c>Before</c>/<c>After</c>) — every binding
///     silently failed to resolve. The host now supplies the Framework VM;
///     this test pins that contract by asserting each axaml binding resolves
///     against it.
/// </summary>
/// <remarks>
///     Deliberately session-free: the boxes/button/rows are declared directly
///     in the axaml (no templates/styles needed to realize them), inflation
///     is dispatcher-free (see <c>ViewInflationTests.DiffView_Inflates</c>)
///     and plain-property bindings evaluate synchronously on
///     <c>DataContext</c> set. A previous revision booted a
///     <c>HeadlessUnitTestSession</c> here and flaked in CI
///     (<c>EnsureIsolatedApplication</c> racing a foreign UI thread) — the
///     session bought nothing, so it goes.
/// </remarks>
[NotInParallel("avalonia-headless")]
public class DiffViewBindingTests
{
    [Test]
    public async Task DiffView_ResolvesFrameworkBindings()
    {
        var vm = new DiffViewModel(NullLogger<DiffViewModel>.Instance)
        {
            LeftText = "line1\nline-left",
            RightText = "line1\nline-right",
        };
        vm.ComputeCommand.Execute(null);

        var view = new DiffView { DataContext = vm };

        // LeftText/RightText → the two input boxes (empty on silent no-resolve).
        var texts = view.GetVisualDescendants()
            .OfType<TextBox>()
            .Select(b => b.Text)
            .ToList();
        await Assert.That(texts.Contains(vm.LeftText)).IsTrue();
        await Assert.That(texts.Contains(vm.RightText)).IsTrue();

        // ComputeCommand → Compute button (null on silent no-resolve).
        var computeButton = view.GetVisualDescendants()
            .OfType<Button>()
            .Single(b => Equals(b.Content, "Compute"));
        await Assert.That(computeButton.Command).IsSameReferenceAs(vm.ComputeCommand);

        // Rows → row list (null source / zero items on silent no-resolve).
        var rows = view.GetVisualDescendants().OfType<ItemsControl>().Single();
        await Assert.That(rows.ItemsSource).IsSameReferenceAs(vm.Rows);
        await Assert.That(rows.Items.Count).IsEqualTo(vm.Rows.Count);
        await Assert.That(vm.Rows.Count).IsEqualTo(2);
    }
}
