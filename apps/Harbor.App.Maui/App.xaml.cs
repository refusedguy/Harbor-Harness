using Application = Microsoft.Maui.Controls.Application;

namespace Harbor.App.Maui;

/// <summary>
///     MAUI application root. <see cref="CreateWindow"/> launches a single
///     <see cref="Page"/> containing the placeholder chat shell. The real
///     chat UI (Markdown rendering, message list, input box) is a v0.5
///     follow-up — this class exists so the app boots and shows a window.
/// </summary>
public partial class App : Application
{
    /// <summary>Construct the application and initialize the generated component.</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell());
    }
}

/// <summary>
///     Minimal root page — single-line label so the launched window shows
///     something other than a blank screen. v0.5 will replace this with a
///     real chat shell (CollectionView + Entry + send button).
/// </summary>
internal sealed class AppShell : ContentPage
{
    /// <summary>Construct the placeholder shell.</summary>
    public AppShell()
    {
        Title = "Harbor";
        BackgroundColor = Color.FromArgb("#1E1E2E"); // Catppuccin Mocha base

        Content = new VerticalStackLayout
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Spacing = 16,
            Children =
            {
                new Label
                {
                    Text = "Harbor",
                    FontSize = 32,
                    FontAttributes = FontAttributes.Bold,
                    HorizontalOptions = LayoutOptions.Center,
                },
                new Label
                {
                    Text = "MAUI shell — chat UI coming in v0.5",
                    FontSize = 14,
                    Opacity = 0.7,
                    HorizontalOptions = LayoutOptions.Center,
                }
            }
        };
    }
}
