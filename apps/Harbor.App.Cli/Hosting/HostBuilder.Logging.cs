using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Harbor.Cli.Logging;
using Harbor.Terminal.Abstractions;
using Harbor.Ui.Framework.Diagnostics;

namespace Harbor.Cli.Hosting;

internal static partial class HostBuilder
{
    private static void ConfigureLogging(HostApplicationBuilder builder, string[] args)
    {
        builder.Logging.ClearProviders();
        var logLevel = Program.ResolveLogLevel(args);

        bool interactiveTui = TuiMode.WillEnterInteractiveTui(args);

        var fileProvider = HarborLogManager.Current;
        if (fileProvider is not null)
            builder.Logging.AddProvider(fileProvider);

        if (interactiveTui)
        {
            var panel = DiagnosticsSink.Initialize();
            builder.Logging.AddProvider(new DiagnosticsPanelLoggerProvider(panel));
            builder.Services.AddSingleton<IDiagnosticsPanel>(panel);
        }
        else if (logLevel <= LogLevel.Information)
        {
            builder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        }
        builder.Logging.SetMinimumLevel(logLevel);
    }
}
