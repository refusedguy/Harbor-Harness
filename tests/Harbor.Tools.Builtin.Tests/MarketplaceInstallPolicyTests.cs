// #2680 F153. Behaviour tests for the pure marketplace-install policy: which
// package ids and specs are safe to accept, and whether a downloaded payload
// matches its pinned digest. Nothing here downloads, writes a file or touches
// product wiring.

using System.Security.Cryptography;
using System.Text;
using Harbor.Tools.Builtin;

namespace Harbor.Tools.Builtin.Tests;

public class MarketplaceInstallPolicyTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("marketplace-payload");

    private static string DigestOf(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload));

    [Test]
    public async Task IsSafeId_CanonicalForms_Accepted()
    {
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("websearch")).IsTrue();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("git-tools")).IsTrue();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("a1-b2-c3")).IsTrue();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("v2")).IsTrue();
    }

    [Test]
    public async Task IsSafeId_MissingOrBlank_Rejected()
    {
        await Assert.That(MarketplaceInstallPolicy.IsSafeId(null)).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId(string.Empty)).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("   ")).IsFalse();
    }

    [Test]
    public async Task IsSafeId_HyphenPlacement_Rejected()
    {
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("-leading")).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("trailing-")).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("double--hyphen")).IsFalse();
    }

    [Test]
    public async Task IsSafeId_UnsafeCharacters_Rejected()
    {
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("Uppercase")).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("with space")).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("with.dot")).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("../escape")).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("a/b")).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId("caf\u00e9")).IsFalse();
    }

    [Test]
    public async Task IsSafeId_LongerThanCap_Rejected()
    {
        string tooLong = new('a', MarketplaceInstallPolicy.MaxIdLength + 1);
        string atCap = new('a', MarketplaceInstallPolicy.MaxIdLength);

        await Assert.That(MarketplaceInstallPolicy.IsSafeId(tooLong)).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.IsSafeId(atCap)).IsTrue();
    }

    [Test]
    public async Task TryParseSpec_BareId_NoVersion()
    {
        bool parsed = MarketplaceInstallPolicy.TryParseSpec("git-tools", out string id, out string? version);

        await Assert.That(parsed).IsTrue();
        await Assert.That(id).IsEqualTo("git-tools");
        await Assert.That(version).IsNull();
    }

    [Test]
    public async Task TryParseSpec_IdWithVersion_Splits()
    {
        bool parsed = MarketplaceInstallPolicy.TryParseSpec("git-tools@1.2.3", out string id, out string? version);

        await Assert.That(parsed).IsTrue();
        await Assert.That(id).IsEqualTo("git-tools");
        await Assert.That(version).IsEqualTo("1.2.3");
    }

    [Test]
    public async Task TryParseSpec_UnsafeParts_Rejected()
    {
        await Assert.That(MarketplaceInstallPolicy.TryParseSpec(null, out _, out _)).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.TryParseSpec(string.Empty, out _, out _)).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.TryParseSpec("../bad@1.0", out _, out _)).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.TryParseSpec("good@", out _, out _)).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.TryParseSpec("good@1.0@2.0", out _, out _)).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.TryParseSpec("good@bad_version", out _, out _)).IsFalse();
    }

    [Test]
    public async Task VerifySha256_MatchingDigest_Accepted()
    {
        string digest = DigestOf(Payload);

        await Assert.That(MarketplaceInstallPolicy.VerifySha256(Payload, digest)).IsTrue();
        await Assert.That(MarketplaceInstallPolicy.VerifySha256(Payload, digest.ToLowerInvariant())).IsTrue();
        await Assert.That(MarketplaceInstallPolicy.VerifySha256(Payload, "sha256:" + digest)).IsTrue();
        await Assert.That(MarketplaceInstallPolicy.VerifySha256(Payload, "SHA256:" + digest)).IsTrue();
    }

    [Test]
    public async Task VerifySha256_WrongPayloadOrDigest_Rejected()
    {
        string digest = DigestOf(Payload);
        byte[] tampered = Encoding.UTF8.GetBytes("tampered");

        await Assert.That(MarketplaceInstallPolicy.VerifySha256(tampered, digest)).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.VerifySha256(Payload, new string('0', digest.Length))).IsFalse();
    }

    [Test]
    public async Task VerifySha256_MalformedDigest_FailsClosed()
    {
        string digest = DigestOf(Payload);

        await Assert.That(MarketplaceInstallPolicy.VerifySha256(Payload, null)).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.VerifySha256(Payload, string.Empty)).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.VerifySha256(Payload, digest[..^1])).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.VerifySha256(Payload, new string('z', digest.Length))).IsFalse();
        await Assert.That(MarketplaceInstallPolicy.VerifySha256(Payload, "md5:" + digest)).IsFalse();
    }
}
