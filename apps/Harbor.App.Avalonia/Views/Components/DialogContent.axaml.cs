using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Harbor.App.Avalonia.Views.Components;

/// <summary>
///     The declarative generic form shared by the modal confirm, prompt and alert
///     dialogs. XAML owns the whole control tree; the host only supplies data
///     (message, labels, optional input value) and commands (OK / Cancel) through
///     <see cref="Bind" />. No reflection or runtime XAML loading is involved —
///     the markup is compiled to IL by AvaloniaUseCompiledXaml.
/// </summary>
public sealed partial class DialogContent : UserControl
{
    /// <summary>Body text, painted once by PART_Message and reused as the input placeholder.</summary>
    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<DialogContent, string?>(nameof(Message), string.Empty);

    /// <summary>Label of the primary action.</summary>
    public static readonly StyledProperty<string?> OkLabelProperty =
        AvaloniaProperty.Register<DialogContent, string?>(nameof(OkLabel), "OK");

    /// <summary>Label of the secondary action; empty when the dialog has no Cancel.</summary>
    public static readonly StyledProperty<string?> CancelLabelProperty =
        AvaloniaProperty.Register<DialogContent, string?>(nameof(CancelLabel), string.Empty);

    /// <summary>Current input value, written back by the TextBox on every edit.</summary>
    public static readonly StyledProperty<string?> InputTextProperty =
        AvaloniaProperty.Register<DialogContent, string?>(nameof(InputText), string.Empty);

    /// <summary>True only for the prompt dialog, which owns an input row.</summary>
    public static readonly StyledProperty<bool> ShowInputProperty =
        AvaloniaProperty.Register<DialogContent, bool>(nameof(ShowInput));

    /// <summary>True for confirm and prompt; false for the single-action alert.</summary>
    public static readonly StyledProperty<bool> ShowCancelProperty =
        AvaloniaProperty.Register<DialogContent, bool>(nameof(ShowCancel));

    /// <summary>Command run by PART_Ok.</summary>
    public static readonly StyledProperty<ICommand?> OkCommandProperty =
        AvaloniaProperty.Register<DialogContent, ICommand?>(nameof(OkCommand));

    /// <summary>Command run by PART_Cancel.</summary>
    public static readonly StyledProperty<ICommand?> CancelCommandProperty =
        AvaloniaProperty.Register<DialogContent, ICommand?>(nameof(CancelCommand));

    /// <summary>
    ///     Construct the form. The control is its own DataContext so the declarative
    ///     bindings resolve to the StyledProperties below without an ancestor walk;
    ///     Avalonia's generated InitializeComponent inflates the XAML.
    /// </summary>
    public DialogContent()
    {
        DataContext = this;
        if (global::Avalonia.Application.Current is not null)
            InitializeComponent();
    }

    /// <summary>Body text.</summary>
    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>Primary button label.</summary>
    public string? OkLabel
    {
        get => GetValue(OkLabelProperty);
        set => SetValue(OkLabelProperty, value);
    }

    /// <summary>Secondary button label.</summary>
    public string? CancelLabel
    {
        get => GetValue(CancelLabelProperty);
        set => SetValue(CancelLabelProperty, value);
    }

    /// <summary>Current value of the prompt input.</summary>
    public string? InputText
    {
        get => GetValue(InputTextProperty);
        set => SetValue(InputTextProperty, value);
    }

    /// <summary>Whether the input row is visible.</summary>
    public bool ShowInput
    {
        get => GetValue(ShowInputProperty);
        set => SetValue(ShowInputProperty, value);
    }

    /// <summary>Whether the Cancel button is visible.</summary>
    public bool ShowCancel
    {
        get => GetValue(ShowCancelProperty);
        set => SetValue(ShowCancelProperty, value);
    }

    /// <summary>Command invoked by the primary button.</summary>
    public ICommand? OkCommand
    {
        get => GetValue(OkCommandProperty);
        set => SetValue(OkCommandProperty, value);
    }

    /// <summary>Command invoked by the secondary button.</summary>
    public ICommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    /// <summary>
    ///     Apply host data and commands. A null <see cref="DialogFormData.CancelLabel" />
    ///     hides Cancel (alert); a null <see cref="DialogFormData.InputText" /> hides the
    ///     input row (confirm / alert). The two state flags are derived here so the
    ///     XAML only ever binds data.
    /// </summary>
    public void Bind(DialogFormData data)
    {
        Message = data.Message;
        OkLabel = data.OkLabel;
        CancelLabel = data.CancelLabel ?? string.Empty;
        InputText = data.InputText ?? string.Empty;
        ShowInput = data.InputText is not null;
        ShowCancel = data.CancelLabel is not null;
        OkCommand = data.OkCommand;
        CancelCommand = data.CancelCommand;
    }
}

/// <summary>
///     The data and commands a host hands to <see cref="DialogContent.Bind" />.
///     No control references leak into the host: closing the window is the host's
///     command, not a concern of this form.
/// </summary>
/// <param name="Message">Body text shown above the actions.</param>
/// <param name="OkLabel">Primary action label.</param>
/// <param name="CancelLabel">Secondary action label; null hides it.</param>
/// <param name="InputText">Initial input value; null hides the input row.</param>
/// <param name="OkCommand">Command run by the primary button.</param>
/// <param name="CancelCommand">Command run by the secondary button.</param>
public sealed record DialogFormData(
    string Message,
    string OkLabel,
    string? CancelLabel = null,
    string? InputText = null,
    ICommand? OkCommand = null,
    ICommand? CancelCommand = null);
