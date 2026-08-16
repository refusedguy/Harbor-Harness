using System.Diagnostics;

namespace Harbor.Core.Telemetry;

public static class HarborTelemetry
{
    public static readonly ActivitySource Source = new("Harbor");
}
