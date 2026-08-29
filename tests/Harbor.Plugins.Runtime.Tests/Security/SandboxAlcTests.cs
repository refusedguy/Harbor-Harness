using System.Reflection;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Runtime.Tests.TestSupport;

namespace Harbor.Plugins.Runtime.Tests.Security;

/// <summary>
///     Sandbox ALC contract: sensitive framework assemblies resolve only when the
///     matching capability is granted; shared host types resolve from the host ALC;
///     the context is collectible and unloads without leaks.
/// </summary>
public sealed class SandboxAlcTests
{
    [Test]
    public async Task DenyList_UnknownCapability_IsDenied()
    {
        var granted = new HashSet<PluginCapability>();

        await Assert.That(CollectiblePluginLoadContext.IsDenied("System.IO.FileSystem", granted)).IsTrue();
        await Assert.That(CollectiblePluginLoadContext.IsDenied("System.Diagnostics.Process", granted)).IsTrue();
        await Assert.That(CollectiblePluginLoadContext.IsDenied("System.Net.Http", granted)).IsTrue();
        await Assert.That(CollectiblePluginLoadContext.IsDenied("System.Linq", granted)).IsFalse();
    }

    [Test]
    public async Task DenyList_GrantedCapability_IsAllowed()
    {
        var granted = new HashSet<PluginCapability> { PluginCapability.HttpRequests, PluginCapability.RunProcesses };

        await Assert.That(CollectiblePluginLoadContext.IsDenied("System.Net.Http", granted)).IsFalse();
        await Assert.That(CollectiblePluginLoadContext.IsDenied("System.Diagnostics.Process", granted)).IsFalse();
        await Assert.That(CollectiblePluginLoadContext.IsDenied("System.IO.FileSystem", granted)).IsTrue();
    }

    [Test]
    public async Task Load_SensitiveAssemblyWithoutCapability_ThrowsFileNotFoundException()
    {
        using var alc = new CollectiblePluginLoadContext("deny-test", new HashSet<PluginCapability>());

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Task.Run(() => alc.LoadFromAssemblyName(new AssemblyName("System.Net.Http"))));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Task.Run(() => alc.LoadFromAssemblyName(new AssemblyName("System.Diagnostics.Process"))));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Task.Run(() => alc.LoadFromAssemblyName(new AssemblyName("System.IO.FileSystem"))));
    }

    [Test]
    public async Task Load_SensitiveAssemblyWithCapability_Resolves()
    {
        using var alc = new CollectiblePluginLoadContext(
            "allow-net",
            new HashSet<PluginCapability> { PluginCapability.HttpRequests });

        var asm = alc.LoadFromAssemblyName(new AssemblyName("System.Net.Http"));

        await Assert.That(asm).IsNotNull();
    }

    [Test]
    public async Task ForScript_ResolvesSharedHostAssemblies()
    {
        var script = new PluginScript(
            Path.Combine(Path.GetTempPath(), "shared-test.cs"),
            "// harbor:capabilities read_files\n// any plugin body");
        using var alc = CollectiblePluginLoadContext.ForScript(script);

        var hostPluginsAsm = typeof(Harbor.Abstractions.Plugins.IPlugin).Assembly;
        var hostAbstractionsAsm = typeof(PluginScript).Assembly;
        var resolvedPluginsAsm = alc.LoadFromAssemblyName(new AssemblyName(hostPluginsAsm.GetName().Name!));
        var resolvedAbstractionsAsm = alc.LoadFromAssemblyName(new AssemblyName(hostAbstractionsAsm.GetName().Name!));

        await Assert.That(ReferenceEquals(resolvedPluginsAsm, hostPluginsAsm)).IsTrue();
        await Assert.That(ReferenceEquals(resolvedAbstractionsAsm, hostAbstractionsAsm)).IsTrue();
    }

    [Test]
    public async Task Unload_AfterLoadingPluginImage_IsGarbageCollected()
    {
        static WeakReference CreateLoadUnloadAndTrack()
        {
            // Keep every strong reference to the sandbox and its assemblies local to
            // this method: after return, only the WeakReference survives.
            var script = new PluginScript(
                Path.Combine(Path.GetTempPath(), "unload-test.cs"),
                "// harbor:capabilities read_files\n// plugin body");
            var alc = CollectiblePluginLoadContext.ForScript(script);
            // Load a real (non-shared) PE image into the collectible context so the
            // unloading path exercises loaded collectible assemblies, not an empty ALC.
            alc.LoadFromPluginPath(typeof(Harbor.Plugins.Storage.PluginAuditLog).Assembly.Location);
            alc.Unload();
            return new WeakReference(alc);
        }

        WeakReference alcRef = CreateLoadUnloadAndTrack();
        bool unloaded = false;
        for (int i = 0; i < 10 && !unloaded; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            unloaded = !alcRef.IsAlive;
        }

        await Assert.That(unloaded).IsTrue();
    }
}