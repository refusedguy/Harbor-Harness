using System.Resources;

namespace Harbor.Core.Resources;

internal static class CoreResources
{
    private static readonly ResourceManager Log = new("Harbor.Core.LogMessages", typeof(CoreResources).Assembly);
    private static readonly ResourceManager Error = new("Harbor.Core.ErrorMessages", typeof(CoreResources).Assembly);

    public static string GetLog(string name) => Log.GetString(name) ?? name;
    public static string GetError(string name) => Error.GetString(name) ?? name;
}
