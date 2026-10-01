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
///     Deliberately minimal: no <c>Window.Show</c>, no render timer, no layout
///     pass — <c>ApplyTemplate()</c> is enough to realize the tree, which is the
///     same step <c>ViewInflationTests</c> already uses for this suite. Keeps
///     clear of the known Avalonia 12 headless flakes (see
///     <c>ViewInflationTests</c> known-issue notes).
///     <para>
///         This remark used to say realized containers were NOT needed. That was
///         wrong, and it was wrong in the way that hides things: with no
///         realization the <c>Compute</c> button is not in the visual tree,
///         <c>.Single(b =&gt; b.Content == "Compute")</c> throws, and the throw
///         was discarded because the assertions ran as a detached <c>async
///         void</c> inside <c>Dispatch(Action)</c>. So the file had been green
///         while checking none of its five claims. See the note at the
///         <c>ApplyTemplate</c> call.
///     </para>
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
        List<string> buttonContents = [];
        string? leftText = null;
        string? rightText = null;
        ICommand? boundComputeCommand = null;
        ICommand? expectedComputeCommand = null;
        object? rowSource = null;
        ObservableCollection<DiffRowViewModel>? expectedRows = null;
        int listCount = -1;
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

            // The tree MUST be realized before it can be walked, and this line is
            // the reason the test is worth anything. It was missing, and the test
            // was still reporting green: `view.GetVisualDescendants()` found no
            // Compute button, `.Single(b => b.Content == "Compute")` threw
            // "Sequence contains no matching element", and — because the body was
            // an `async void` inside `Dispatch(Action)` (#972, #766) — the
            // exception was discarded with the detached continuation. So the
            // bindings this file exists to pin (#160) have never once been
            // checked. The file's own remark claimed "realized containers are not
            // needed"; that claim was the bug, and it is corrected here.
            //
            // `ApplyTemplate` is the same realization step `ViewInflationTests`
            // already uses for this suite, which is why the shape is not invented
            // for this file.
            view.ApplyTemplate();

            // LeftText/RightText → the two input boxes (empty on silent no-resolve).
            texts = view.GetVisualDescendants()
                .OfType<TextBox>()
                .Select(b => b.Text)
                .ToList();
            leftText = vm.LeftText;
            rightText = vm.RightText;

            // ComputeCommand → Compute button (null on silent no-resolve).
            // Located by index rather than `.Single(...)`, so a tree that does not
            // contain the button fails as an ASSERTION that says what buttons were
            // found. `.Single` threw an opaque "Sequence contains no matching
            // element" from inside a detached `async void`, which is how a missing
            // button went unnoticed here in the first place.
            List<Button> buttons = view.GetVisualDescendants().OfType<Button>().ToList();
            buttonContents = buttons.Select(b => b.Content?.ToString() ?? "(null)").ToList();
            int computeIndex = buttonContents.FindIndex(c => c == "Compute");
            if (computeIndex >= 0)
            {
                boundComputeCommand = buttons[computeIndex].Command;
            }

            expectedComputeCommand = vm.ComputeCommand;

            // Rows → row list (null source / zero items on silent no-resolve).
            List<ItemsControl> lists = view.GetVisualDescendants().OfType<ItemsControl>().ToList();
            listCount = lists.Count;
            if (lists.Count > 0)
            {
                rowSource = lists[0].ItemsSource;
                rowItemCount = lists[0].Items.Count;
            }

            expectedRows = vm.Rows;
            computedRowCount = vm.Rows.Count;
        }), CancellationToken.None);

        await Assert.That(buttonContents)
            .Contains("Compute")
            .Because(
                "the Compute button is declared in DiffView.axaml with Content=\"Compute\"; if the walk "
                + "cannot see it, the tree was not realized and none of the checks below mean anything. "
                + "Button contents found: " + (buttonContents.Count == 0 ? "(none)" : string.Join(" | ", buttonContents)));
        await Assert.That(texts.Contains(leftText!)).IsTrue()
            .Because(
                "LeftText binds to the left input box; a silent no-resolve leaves the box empty, which is "
                + "the #160 defect this file exists to catch. Box texts found: "
                + (texts.Count == 0 ? "(none)" : string.Join(" | ", texts)));
        await Assert.That(texts.Contains(rightText!)).IsTrue();
        await Assert.That(boundComputeCommand).IsSameReferenceAs(expectedComputeCommand)
            .Because("the Compute button's Command must be the view-model's own command, not a fresh one");
        await Assert.That(listCount).IsEqualTo(1)
            .Because("DiffView declares one ItemsControl (the row list); the walk found " + listCount);
        await Assert.That(rowSource).IsSameReferenceAs(expectedRows)
            .Because("the row list's ItemsSource is the view-model's own collection, not a copy");
        await Assert.That(rowItemCount).IsEqualTo(expectedRows!.Count);
        await Assert.That(computedRowCount).IsEqualTo(2)
            .Because("two lines per side over a four-line input, so the diff must produce two rows");
    }
}
