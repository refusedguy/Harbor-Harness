using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Extensions;
/// <summary>
///     Fluent extension methods for <see cref="DotNetPublishSettings" />.
///     Centralizes the per-flag <c>SetProperty</c> chains so target code
///     stays readable.
/// </summary>
public static class DotNetPublishExtensions
{
    /// <summary>
    ///     Adds a single <c>/p:Name=value</c> property to the publish invocation
    ///     only if <paramref name="value" /> is not <c>null</c> or whitespace.
    /// </summary>
    public static DotNetPublishSettings SetPropertyIfValue(
        this DotNetPublishSettings settings,
        string name,
        string? value)
        => string.IsNullOrWhiteSpace(value) ? settings : settings.SetProperty(name, value!);

    /// <summary>
    ///     Sets the publish output directory AND ensures it exists (NUKE 9.x
    ///     doesn't create the directory automatically — <c>dotnet publish</c>
    ///     will fail with "could not find a part of the path" otherwise).
    /// </summary>
    public static DotNetPublishSettings SetOutputAndEnsureExists(
        this DotNetPublishSettings settings,
        AbsolutePath outputDir)
    {
        Directory.CreateDirectory(outputDir);
        return settings.SetOutput(outputDir);
    }
}
