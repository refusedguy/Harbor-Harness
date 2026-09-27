using System.Net;
using Harbor.Ipc.Protocol;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Issue #175: <see cref="DaemonBindPolicy"/> resolves listen ids
///     through <see cref="IBindAddressStrategy"/> strategies — pins the
///     behavioral contract (same ids, same addresses, same failure text).
/// </summary>
public class DaemonBindPolicyTests
{
    [Test]
    public async Task Resolve_Loopback_ReturnsLoopback()
    {
        var result = DaemonBindPolicy.ResolveBindAddress("loopback");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(IPAddress.Loopback);
    }

    [Test]
    public async Task Resolve_All_ReturnsAny()
    {
        var result = DaemonBindPolicy.ResolveBindAddress("all");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(IPAddress.Any);
    }

    [Test]
    public async Task Resolve_Ids_AreCaseInsensitive_AndTrimmed()
    {
        var loopback = DaemonBindPolicy.ResolveBindAddress("  LOOPBACK  ");
        await Assert.That(loopback.IsSuccess).IsTrue();
        await Assert.That(loopback.Value).IsEqualTo(IPAddress.Loopback);

        var all = DaemonBindPolicy.ResolveBindAddress("All");
        await Assert.That(all.IsSuccess).IsTrue();
        await Assert.That(all.Value).IsEqualTo(IPAddress.Any);
    }

    [Test]
    public async Task Resolve_UnknownId_ReturnsFailure_NamingId()
    {
        var result = DaemonBindPolicy.ResolveBindAddress("bogus-listen-175");
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error.Contains("bogus-listen-175")).IsTrue();
    }

    [Test]
    public async Task Resolve_Null_ReturnsFailure()
    {
        var result = DaemonBindPolicy.ResolveBindAddress(null);
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task Resolve_Tailscale_ReturnsCgnatAddress_OrActionableFailure()
    {
        // No Tailscale interface on CI agents: either branch is acceptable,
        // but both carry a pinned contract (address in 100.64/10, or the
        // 'tailscale up' diagnostic).
        var result = DaemonBindPolicy.ResolveBindAddress("tailscale0");
        if (result.IsSuccess)
        {
            await Assert.That(DaemonBindPolicy.IsTailscaleAddress(result.Value)).IsTrue();
        }
        else
        {
            await Assert.That(result.Error.Contains("tailscale")).IsTrue();
        }
    }
}
