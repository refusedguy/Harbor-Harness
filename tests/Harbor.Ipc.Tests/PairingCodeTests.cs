using Harbor.Ipc.Protocol;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Pairing code + advertise-address tests (sprint 6 zone T): the
///     daemon's pairing code round-trips, PSKs are 22-char base64url, and
///     tailscale/lan/loopback classification follows 100.64/10 and RFC1918.
/// </summary>
public class PairingCodeTests
{
    [Test]
    public async Task Build_Parse_RoundTrips()
    {
        string code = PairingCode.Build("dell.tail1234.ts.net", 48710, "AbC123xyz");

        var parsed = PairingCode.Parse(code);
        await Assert.That(parsed.IsSuccess).IsTrue();
        await Assert.That(parsed.Value.Host).IsEqualTo("dell.tail1234.ts.net");
        await Assert.That(parsed.Value.Port).IsEqualTo(48710);
        await Assert.That(parsed.Value.Psk).IsEqualTo("AbC123xyz");
    }

    [Test]
    public async Task GeneratePsk_IsBase64UrlOf16Bytes()
    {
        string psk = PairingCode.GeneratePsk();
        // 16 bytes → 22 base64url chars, no padding.
        await Assert.That(psk.Length).IsEqualTo(22);
        await Assert.That(psk.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')).IsTrue();
    }

    [Test]
    public async Task GeneratePsk_ProducesDistinctValues()
    {
        string a = PairingCode.GeneratePsk();
        string b = PairingCode.GeneratePsk();
        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task Parse_RejectsMalformedCodes()
    {
        await Assert.That(PairingCode.Parse("").IsFailure).IsTrue();
        await Assert.That(PairingCode.Parse("http://host:1#k").IsFailure).IsTrue();
        await Assert.That(PairingCode.Parse("harbor://host:48710").IsFailure).IsTrue();
        await Assert.That(PairingCode.Parse("harbor://host#k").IsFailure).IsTrue();
        await Assert.That(PairingCode.Parse("harbor://host:notaport#k").IsFailure).IsTrue();
    }

    [Test]
    public async Task DaemonPairingInfo_Code_MatchesCanonicalForm()
    {
        var info = new DaemonPairingInfo("100.84.12.7", 48710, "psk-value");
        await Assert.That(info.Code).IsEqualTo("harbor://100.84.12.7:48710#psk-value");
    }

    [Test]
    public async Task TailscaleRange_Classification()
    {
        await Assert.That(DaemonBindPolicy.IsTailscaleAddress(System.Net.IPAddress.Parse("100.64.0.1"))).IsTrue();
        await Assert.That(DaemonBindPolicy.IsTailscaleAddress(System.Net.IPAddress.Parse("100.127.255.254"))).IsTrue();
        await Assert.That(DaemonBindPolicy.IsTailscaleAddress(System.Net.IPAddress.Parse("100.128.0.1"))).IsFalse();
        await Assert.That(DaemonBindPolicy.IsTailscaleAddress(System.Net.IPAddress.Parse("100.63.255.255"))).IsFalse();
        await Assert.That(DaemonBindPolicy.IsTailscaleAddress(System.Net.IPAddress.Parse("192.168.88.42"))).IsFalse();

        await Assert.That(DaemonBindPolicy.IsPrivateLanAddress(System.Net.IPAddress.Parse("192.168.88.42"))).IsTrue();
        await Assert.That(DaemonBindPolicy.IsPrivateLanAddress(System.Net.IPAddress.Parse("10.1.2.3"))).IsTrue();
        await Assert.That(DaemonBindPolicy.IsPrivateLanAddress(System.Net.IPAddress.Parse("172.20.0.5"))).IsTrue();
        await Assert.That(DaemonBindPolicy.IsPrivateLanAddress(System.Net.IPAddress.Parse("8.8.8.8"))).IsFalse();
        await Assert.That(DaemonBindPolicy.IsPrivateLanAddress(System.Net.IPAddress.Parse("100.64.0.1"))).IsFalse();
    }

    [Test]
    public async Task BindPolicy_ResolvesLoopbackAndAll_AndRejectsGarbage()
    {
        var loopback = DaemonBindPolicy.ResolveBindAddress("loopback");
        await Assert.That(loopback.IsSuccess).IsTrue();
        await Assert.That(loopback.Value).IsEqualTo(System.Net.IPAddress.Loopback);

        var all = DaemonBindPolicy.ResolveBindAddress("ALL");
        await Assert.That(all.IsSuccess).IsTrue();

        var bad = DaemonBindPolicy.ResolveBindAddress("eth0");
        await Assert.That(bad.IsFailure).IsTrue();

        var ts = DaemonBindPolicy.ResolveBindAddress("tailscale0");
        // Environment-dependent: passes only when the address (if found)
        // really is inside the CGNAT range.
        if (ts.IsSuccess)
        {
            await Assert.That(DaemonBindPolicy.IsTailscaleAddress(ts.Value)).IsTrue();
        }
    }
}
