using System.Resources;

namespace Harbor.Application.Resources;

internal static class CoreResources
{
    private static readonly ResourceManager Log = new("Harbor.Application.LogMessages", typeof(CoreResources).Assembly);
    private static readonly ResourceManager Error = new("Harbor.Application.ErrorMessages", typeof(CoreResources).Assembly);

    public static string GetLog(string name) => Log.GetString(name) ?? name;
    public static string GetError(string name) => Error.GetString(name) ?? name;
}
