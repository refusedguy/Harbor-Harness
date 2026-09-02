using System.Security.Cryptography;
using Harbor.Transport.Remote;
using TUnit.Assertions;

namespace Harbor.Transport.Remote.Tests;

/// <summary>
///     A8 (sprint 5): negative coverage for the PSK handshake primitive.
///     <see cref="PsAuthHandler.Validate" /> is the single gate every
///     RemoteGateway/RemoteClient connection must pass.
/// </summary>
/// <remarks>
///     «Replay» semantics for a STATIC-PSK handshake: there is no challenge,
///     so a captured PSK is by design re-usable — transport-level replay
///     protection belongs to the gateway session layer, not this primitive.
///     The suite pins that documented behaviour explicitly.
/// </remarks>
public class PsAuthHandlerTests
{
    [Test]
    public async Task Validate_NullProvided_ReturnsFalse()
    {
        await Assert.That(PsAuthHandler.Validate(null, "expected")).IsFalse();
    }

    [Test]
    public async Task Validate_EmptyProvided_ReturnsFalse()
    {
        await Assert.That(PsAuthHandler.Validate(string.Empty, "expected")).IsFalse();
    }

    [Test]
    public async Task Validate_WrongKey_ReturnsFalse()
    {
        string real = PsAuthHandler.GeneratePsk();
        string wrong = PsAuthHandler.GeneratePsk();

        // Two independent 32-byte keys colliding is cryptographically impossible.
        await Assert.That(PsAuthHandler.Validate(wrong, real)).IsFalse();
    }

    [Test]
    public async Task Validate_CorrectKey_ReturnsTrue()
    {
        string psk = PsAuthHandler.GeneratePsk();

        await Assert.That(PsAuthHandler.Validate(psk, psk)).IsTrue();
    }

    [Test]
    public async Task Validate_IsCaseSensitive()
    {
        await Assert.That(PsAuthHandler.Validate("EXPECTED", "expected")).IsFalse();
    }

    [Test]
    public async Task Validate_LengthMismatch_ReturnsFalse()
    {
        await Assert.That(PsAuthHandler.Validate("short", "a-much-longer-expected-value")).IsFalse();
    }

    [Test]
    public async Task Validate_ExpectedEmpty_AlwaysFalse()
    {
        // A misconfigured gateway with an empty expected PSK must not let
        // an empty-provided client through (both sides empty would compare
        // equal under naive string comparison — IsNullOrEmpty guard on the
        // PROVIDED side plus fixed-time equals keeps the contract explicit).
        await Assert.That(PsAuthHandler.Validate("anything", string.Empty)).IsFalse();
    }

    [Test]
    public async Task GeneratePsk_Base64DecodesTo32Bytes()
    {
        string psk = PsAuthHandler.GeneratePsk();

        byte[] raw = Convert.FromBase64String(psk);
        await Assert.That(raw.Length).IsEqualTo(32);
    }

    [Test]
    public async Task GeneratePsk_UniqueAcrossCalls()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 100; i++)
        {
            await Assert.That(seen.Add(PsAuthHandler.GeneratePsk())).IsTrue();
        }
    }

    [Test]
    public async Task GeneratePsk_RoundTripsThroughValidate()
    {
        for (int i = 0; i < 25; i++)
        {
            string psk = PsAuthHandler.GeneratePsk();
            await Assert.That(PsAuthHandler.Validate(psk, psk)).IsTrue();
        }
    }

    [Test]
    public async Task Replay_SameValidKey_AcceptedRepeatedly_ByDesign()
    {
        // Static-PSK model: no nonce/challenge exists at THIS layer, so the
        // same valid key validates on every connection. Pinned deliberately —
        // if replay protection ever moves into this primitive, this test
        // SHOULD start failing and force a design review.
        string psk = PsAuthHandler.GeneratePsk();

        for (int connection = 0; connection < 5; connection++)
        {
            await Assert.That(PsAuthHandler.Validate(psk, psk)).IsTrue();
        }
    }
}
