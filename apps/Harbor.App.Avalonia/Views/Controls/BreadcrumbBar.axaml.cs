using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Harbor.App.Avalonia.ViewModels;
using System.Windows.Input;

namespace Harbor.App.Avalonia.Views.Controls;

/// <summary>
///     Clickable path breadcrumb trail (K24).
/// </summary>
/// <remarks>
///     A thin renderer over <see cref="BreadcrumbBuilder"/>: it turns each
///     <see cref="BreadcrumbCrumb"/> into a button carrying the crumb's path as
///     its command parameter, with separators between them. The crumbs and
///     their order come from the pure builder; this control decides only how
///     they are painted. No reflection and no dispatcher work, so it is safe
///     under NativeAOT.
/// </remarks>
public sealed partial class BreadcrumbBar : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<BreadcrumbCrumb>?> CrumbsProperty =
        AvaloniaProperty.Register<BreadcrumbBar, IReadOnlyList<BreadcrumbCrumb>?>(nameof(Crumbs));

    public static readonly StyledProperty<ICommand?> CrumbClickCommandProperty =
        AvaloniaProperty.Register<BreadcrumbBar, ICommand?>(nameof(CrumbClickCommand));

    /// <summary>Crumbs to render, root first and current file last.</summary>
    public IReadOnlyList<BreadcrumbCrumb>? Crumbs
    {
        get => GetValue(CrumbsProperty);
        set => SetValue(CrumbsProperty, value);
    }

    /// <summary>Invoked with a crumb's <see cref="BreadcrumbCrumb.Path"/> when it is clicked.</summary>
    public ICommand? CrumbClickCommand
    {
        get => GetValue(CrumbClickCommandProperty);
        set => SetValue(CrumbClickCommandProperty, value);
    }

    static BreadcrumbBar()
    {
        CrumbsProperty.Changed.AddClassHandler<BreadcrumbBar>((bar, _) => bar.Rebuild());
        CrumbClickCommandProperty.Changed.AddClassHandler<BreadcrumbBar>((bar, _) => bar.Rebuild());
    }

    public BreadcrumbBar()
    {
        InitializeComponent();
        Rebuild();
    }

    private void Rebuild()
    {
        if (this.FindControl<StackPanel>("PART_Host") is not { } host) return;

        host.Children.Clear();
        IReadOnlyList<BreadcrumbCrumb> crumbs = Crumbs ?? Array.Empty<BreadcrumbCrumb>();

        for (int i = 0; i < crumbs.Count; i++)
        {
            BreadcrumbCrumb crumb = crumbs[i];
            if (i > 0)
            {
                var separator = new TextBlock();
                separator.Classes.Add("crumb-sep");
                host.Children.Add(separator);
            }

            var button = new Button
            {
                Content = crumb.Label,
                IsEnabled = crumb.IsEnabled,
                Command = CrumbClickCommand,
                CommandParameter = crumb.Path,
            };
            button.Classes.Add("crumb");
            if (crumb.IsCurrent) button.Classes.Add("crumb-current");
            AutomationProperties.SetName(button, crumb.Label);
            host.Children.Add(button);
        }
    }
}
