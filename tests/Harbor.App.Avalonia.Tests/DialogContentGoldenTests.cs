// Headless golden coverage for the declarative dialog form introduced by #3560.
// The old DialogService built three control trees by hand; the reference below
// pins the tree the compiled DialogContent.axaml now produces for each of those
// three dialogs: body text, button labels in paint order, the hds-primary class,
// and the optional input row.
using Avalonia.Controls;
using Avalonia.LogicalTree;
using CommunityToolkit.Mvvm.Input;
using Harbor.App.Avalonia.Tests.Support;
using Harbor.App.Avalonia.Views.Components;
using TUnit;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.App.Avalonia.Tests;

[NotInParallel(new[] { "avalonia-headless" })]
public class DialogContentGoldenTests
{
    /// <summary>
    ///     Inflate the declarative form with host data and render a plain-text
    ///     snapshot of the parts the golden pins. Avalonia objects stay inside
    ///     the headless dispatch; only the string comes back out.
    /// </summary>
    private static async Task<string> SnapshotAsync(DialogFormData data) =>
        await AvaloniaSession.RunAsync(() =>
        {
            var form = new DialogContent();
            form.Bind(data);
            form.ApplyTemplate();

            TextBlock message = form.GetLogicalDescendants().OfType<TextBlock>().Single();
            TextBox input = form.GetLogicalDescendants().OfType<TextBox>().Single();
            Button[] buttons = form.GetLogicalDescendants().OfType<Button>().ToArray();

            string labels = string.Join(" | ", buttons.Select(b =>
                b.Content?.ToString() + (b.Classes.Contains("hds-primary") ? "*" : string.Empty)));
            return $"message={message.Text}; buttons=[{labels}]; "
                   + $"input=visible={input.IsVisible}, text={input.Text}, placeholder={input.PlaceholderText}";
        });

    [Test]
    public async Task Confirm_MessageAndButtons_MatchGolden()
    {
        string snapshot = await SnapshotAsync(new DialogFormData(
            Message: "Save changes?",
            OkLabel: "OK",
            CancelLabel: "Cancel",
            OkCommand: new RelayCommand(() => { }),
            CancelCommand: new RelayCommand(() => { })));

        await Assert.That(snapshot).IsEqualTo(
            "message=Save changes?; buttons=[Cancel | OK*]; "
            + "input=visible=False, text=, placeholder=Save changes?");
    }

    [Test]
    public async Task Prompt_MessageInputAndButtons_MatchGolden()
    {
        string snapshot = await SnapshotAsync(new DialogFormData(
            Message: "Enter name:",
            OkLabel: "OK",
            CancelLabel: "Cancel",
            InputText: "Ada",
            OkCommand: new RelayCommand(() => { }),
            CancelCommand: new RelayCommand(() => { })));

        await Assert.That(snapshot).IsEqualTo(
            "message=Enter name:; buttons=[Cancel | OK*]; "
            + "input=visible=True, text=Ada, placeholder=Enter name:");
    }

    [Test]
    public async Task Alert_MessageAndSingleButton_MatchGolden()
    {
        string snapshot = await SnapshotAsync(new DialogFormData(
            Message: "Done.",
            OkLabel: "OK",
            OkCommand: new RelayCommand(() => { })));

        await Assert.That(snapshot).IsEqualTo(
            "message=Done.; buttons=[OK*]; "
            + "input=visible=False, text=, placeholder=Done.");
    }

    [Test]
    public async Task Bind_WiresHostCommands()
    {
        bool ok = false;
        bool cancel = false;
        bool invoked = await AvaloniaSession.RunAsync(() =>
        {
            var form = new DialogContent();
            form.Bind(new DialogFormData(
                Message: "m",
                OkLabel: "OK",
                CancelLabel: "Cancel",
                OkCommand: new RelayCommand(() => ok = true),
                CancelCommand: new RelayCommand(() => cancel = true)));

            form.OkCommand?.Execute(null);
            form.CancelCommand?.Execute(null);
            return ok && cancel;
        });

        await Assert.That(invoked).IsTrue();
    }
}
