using System.Diagnostics;

namespace Harbor.Application.Telemetry;

public static class HarborTelemetry
{
    public static readonly ActivitySource Source = new("Harbor");
}
