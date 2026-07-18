using System.Collections.ObjectModel;
using Harbor.Tui.Abstractions.State;
using Microsoft.Extensions.Logging;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;
using Application = Microsoft.Maui.Controls.Application;
using Window = Microsoft.Maui.Controls.Window;

namespace Harbor.Tui.Maui;

/// <summary>
///     MAUI <c>MauiProgram</c> entry. Builds the host, registers the
/// <see cref="MauiBridge" /> singleton, and returns the constructed
/// <see cref="MauiApp" />. The renderer owns the lifetime.
/// </summary>
public static class MauiProgram
{
    /// <summary>Build a MAUI app bound to the supplied store + effect host.</summary>
    public static MauiApp CreateMauiApp(
        UiStore store,
        TuiEffectHost effects,
        ObservableCollection<ChatLineViewModel> lines,
        ILogger logger)
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(effects);
        builder.Services.AddSingleton(lines);
        builder.Services.AddSingleton(logger);
        builder.Services.AddSingleton<MauiBridge>();
        return builder.Build();
    }
}
