using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Harbor.App.Wpf.Services;
using Harbor.App.Wpf.ViewModels;
namespace Harbor.App.Wpf.Views;
/// <summary>
///     Streaming chat view — markdown transcript + input box.
/// </summary>
public partial class ChatView : UserControl
{
    private readonly MarkdownFlowDocumentRenderer _renderer = new();

    /// <summary>Construct a <see cref="ChatView" />.</summary>
    public ChatView()
    {
        InitializeComponent();
        this.DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ChatViewModel old)
        {
            old.Messages.CollectionChanged -= OnMessagesChanged;
            old.PropertyChanged -= OnStreamingChanged;
        }
        if (e.NewValue is ChatViewModel vm)
        {
            vm.Messages.CollectionChanged += OnMessagesChanged;
            vm.PropertyChanged += OnStreamingChanged;
        }
    }

    private void OnStreamingChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Visibility is bound via StreamingVisibility — see partial class below.
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Auto-scroll to bottom.
        if (Transcript.Items.Count > 0)
        {
            Transcript.ScrollIntoView(Transcript.Items[^1]);
        }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (this.DataContext is not ChatViewModel vm) return;
        e.Handled = true;
        if (vm.SendCommand.CanExecute(null))
        {
            _ = vm.SendCommand.ExecuteAsync(null);
        }
    }

    private void MessageBody_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox rtb) return;
        if (rtb.DataContext is not ChatMessageViewModel msg) return;
        var doc = _renderer.Render(msg.Content);
        rtb.Document = doc;
    }
}
