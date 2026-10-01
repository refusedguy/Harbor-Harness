using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Harbor.App.Avalonia;
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
///     Deliberately minimal: no <c>Window.Show</c>, no layout pass, no render
///     timer — bindings evaluate synchronously when <c>DataContext</c> is set
///     on the UI thread, so realized containers are not needed. Keeps clear
///     of the known Avalonia 12 headless flakes (see
///     <c>ViewInflationTests</c> known-issue notes).
/// </remarks>
[NotInParallel("avalonia-headless")]
public class DiffViewBindingTests
{
    [Test]
    [Retry(3)]
    public async Task DiffView_ResolvesFrameworkBindings()
    {
        // Everything the view needs is read INSIDE the dispatch (the visual tree
        // only exists on the UI thread); every assertion is made OUTSIDE it.
        // `Dispatch(async () => …)` binds to `Dispatch(Action)` — there is no
        // `Func<Task>` overload — so the body detached at its first `await Assert`
        // and none of the six checks below could fail the test (#972, #766; see
        // AvaloniaDispatchAsyncVoidRule).
        List<string> texts = [];
        string? leftText = null;
        string? rightText = null;
        ICommand? boundComputeCommand = null;
        ICommand? expectedComputeCommand = null;
        object? rowSource = null;
        ObservableCollection<DiffRowViewModel>? expectedRows = null;
        int rowItemCount = -1;
        int computedRowCount = -1;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch((System.Action)(() =>
        {
            var vm = new SideBySideDiffViewModel(NullLogger<SideBySideDiffViewModel>.Instance)
            {
                LeftText = "line1\nline-left",
                RightText = "line1\nline-right",
            };
            vm.ComputeCommand.Execute(null);

            var view = new DiffView { DataContext = vm };

            // LeftText/RightText → the two input boxes (empty on silent no-resolve).
            texts = view.GetVisualDescendants()
                .OfType<TextBox>()
                .Select(b => b.Text)
                .ToList();
            leftText = vm.LeftText;
            rightText = vm.RightText;

            // ComputeCommand → Compute button (null on silent no-resolve).
            var computeButton = view.GetVisualDescendants()
                .OfType<Button>()
                .Single(b => Equals(b.Content, "Compute"));
            boundComputeCommand = computeButton.Command;
            expectedComputeCommand = vm.ComputeCommand;

            // Rows → row list (null source / zero items on silent no-resolve).
            var rows = view.GetVisualDescendants().OfType<ItemsControl>().Single();
            rowSource = rows.ItemsSource;
            rowItemCount = rows.Items.Count;
            expectedRows = vm.Rows;
            computedRowCount = vm.Rows.Count;
        }), CancellationToken.None);

        await Assert.That(texts.Contains(leftText!)).IsTrue();
        await Assert.That(texts.Contains(rightText!)).IsTrue();
        await Assert.That(boundComputeCommand).IsSameReferenceAs(expectedComputeCommand);
        await Assert.That(rowSource).IsSameReferenceAs(expectedRows);
        await Assert.That(rowItemCount).IsEqualTo(expectedRows!.Count);
        await Assert.That(computedRowCount).IsEqualTo(2);
    }
}
