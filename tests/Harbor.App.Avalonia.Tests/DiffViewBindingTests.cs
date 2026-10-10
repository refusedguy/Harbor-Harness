using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
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
///     Deliberately minimal, and now for a reason that was MEASURED rather than
///     assumed: no <c>Window.Show</c>, no <c>ApplyTemplate</c>, no measure/arrange.
///     The walk is over the LOGICAL tree, because a <c>UserControl</c>'s axaml
///     content is a logical child from the moment <c>InitializeComponent</c> runs —
///     putting it into the VISUAL tree needs a <c>ContentPresenter</c> from the
///     templated subtree, which needs a real <c>TopLevel</c>. Keeps clear of the
///     known Avalonia 12 headless flakes (see <c>ViewInflationTests</c> notes).
///     <para>
///         This remark used to say "realized containers are not needed", which was
///         right about the conclusion and wrong about the walk: the file walked
///         <c>GetVisualDescendants()</c>, which finds nothing here, so
///         <c>.Single(b =&gt; b.Content == "Compute")</c> threw — and the throw was
///         discarded because the assertions ran as a detached <c>async void</c>
///         inside <c>Dispatch(Action)</c> (#972, #766). The file had been green
///         while checking none of its five claims. The two failed CI runs that got
///         here are recorded at the walk site.
///     </para>
/// </remarks>
[NotInParallel("avalonia-headless")]
public class DiffViewBindingTests
{
    [Test]
    [Retry(3)] // Retention(#1087): headless-Avalonia UI-thread flake; see class remarks (#972, #766).
    public async Task DiffView_ResolvesFrameworkBindings()
    {
        // Everything the view needs is read INSIDE the dispatch (the visual tree
        // only exists on the UI thread); every assertion is made OUTSIDE it.
        // `Dispatch(async () => …)` binds to `Dispatch<TResult>(Func<TResult>)` at
        // `TResult = Task` — a `Dispatch(Func<Task>)` returning `Task<Task>` — whose
        // payload this call site dropped, so the body detached at its first
        // `await Assert` and none of the six checks below could fail the test
        // (#972, #766; see AvaloniaDispatchAsyncVoidRule).
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

            // This walk is the reason the test is worth anything, and getting it
            // right took three CI runs, so the reasoning is recorded rather than
            // the conclusion alone.
            //
            // What was wrong: the file walked `GetVisualDescendants()`, found no
            // Compute button, and `.Single(b => b.Content == "Compute")` threw
            // "Sequence contains no matching element" — inside an `async void`
            // body, so the throw was discarded with the detached continuation
            // (#972, #766) and the test reported Passed. The bindings this file
            // exists to pin (#160) have never once been checked.
            //
            // MEASURED, not assumed — three attempts, two of them wrong:
            //
            // Attempt 1, `GetVisualDescendants()` with no realization: empty
            //   walk. `.Single(...)` threw, the `async void` swallowed it, green.
            // Attempt 2, `ApplyTemplate()`: still "Button contents found: (none)".
            //   A `ContentControl` applies its own template, but the inner
            //   `ContentPresenter` builds its child during measure.
            // Attempt 3, apply + `Measure` + `Arrange`: STILL none. Measure and
            //   arrange drive layout, and layout alone does not move a
            //   `ContentControl`'s content into the VISUAL tree here.
            //
            // What is actually true: the axaml content of a `UserControl` is a
            // LOGICAL child from the moment `InitializeComponent` runs. The
            // `ContentPresenter` that would re-parent it into the visual tree is
            // part of the templated subtree, which needs a real
            // `TopLevel`/`Window` — and this file deliberately does not open one.
            //
            // So the walk is `GetLogicalDescendants()`, which is both the correct
            // tree for this shape and the reason the no-Window remark can stand.
            // The claims under test are about BINDINGS — does `{Binding
            // LeftText}` on a declared control resolve, does the button's Command
            // equal the view-model's — and a binding is declared on the control
            // regardless of which tree currently parents it. What this test is
            // NOT is a layout or render test; nothing here should depend on a
            // presenter having run.

            // LeftText/RightText → the two input boxes (empty on silent no-resolve).
            texts = view.GetLogicalDescendants()
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
            List<Button> buttons = view.GetLogicalDescendants().OfType<Button>().ToList();
            buttonContents = buttons.Select(b => b.Content?.ToString() ?? "(null)").ToList();
            int computeIndex = buttonContents.FindIndex(c => c == "Compute");
            if (computeIndex >= 0)
            {
                boundComputeCommand = buttons[computeIndex].Command;
            }

            expectedComputeCommand = vm.ComputeCommand;

            // Rows → row list (null source / zero items on silent no-resolve).
            List<ItemsControl> lists = view.GetLogicalDescendants().OfType<ItemsControl>().ToList();
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
