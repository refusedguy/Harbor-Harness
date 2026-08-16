using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Harbor.App.Avalonia.Views.Components;

public sealed partial class EmptyState : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Subtitle), string.Empty);

    public static readonly StyledProperty<object?> IconProperty =
        AvaloniaProperty.Register<EmptyState, object?>(nameof(Icon));

    public static readonly StyledProperty<object?> CtaProperty =
        AvaloniaProperty.Register<EmptyState, object?>(nameof(Cta));

    public static readonly StyledProperty<ObservableCollection<Suggestion>?> SuggestionsProperty =
        AvaloniaProperty.Register<EmptyState, ObservableCollection<Suggestion>?>(nameof(Suggestions));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public object? Cta
    {
        get => GetValue(CtaProperty);
        set => SetValue(CtaProperty, value);
    }

    public ObservableCollection<Suggestion>? Suggestions
    {
        get => GetValue(SuggestionsProperty);
        set => SetValue(SuggestionsProperty, value);
    }

    public EmptyState()
    {
        if (Application.Current is not null)
            InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

public sealed record Suggestion(string Label, string Prompt, ICommand? Command = null)
{
    public static Suggestion Create(string label, string prompt, ICommand? command = null) => new(label, prompt, command);
}
