using System.Buffers.Text;
using System.Security.Cryptography;
using CSharpFunctionalExtensions;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     The pairing code a daemon hands to new clients (printed as text and
///     rendered as a QR next to it):
///     <c>harbor://&lt;host&gt;:&lt;port&gt;#&lt;psk&gt;</c>.
///     The PSK is 128 bits of randomness, base64url — short enough that the
///     whole code fits a compact QR, strong enough to be the second factor
///     behind the tailnet/network boundary.
/// </summary>
public static class PairingCode
{
    /// <summary>URI scheme prefix of every pairing code.</summary>
    public const string Prefix = "harbor://";

    /// <summary>Generate a fresh 128-bit PSK (base64url, no padding — 22 chars).</summary>
    public static string GeneratePsk()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>Build the canonical pairing code string.</summary>
    public static string Build(string host, int port, string psk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(psk);
        return $"{Prefix}{host}:{port}#{psk}";
    }

    /// <summary>Parse a pairing code back into its parts.</summary>
    public static Result<(string Host, int Port, string Psk)> Parse(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || !code.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Result.Failure<(string, int, string)>(
                $"Pairing code must start with '{Prefix}'.");
        }

        string rest = code[Prefix.Length..];
        int hash = rest.IndexOf('#');
        if (hash < 0)
        {
            return Result.Failure<(string, int, string)>("Pairing code is missing the '#<psk>' fragment.");
        }

        string hostPort = rest[..hash];
        string psk = rest[(hash + 1)..];
        int colon = hostPort.LastIndexOf(':');
        if (colon <= 0 || colon == hostPort.Length - 1)
        {
            return Result.Failure<(string, int, string)>("Pairing code is missing ':<port>'.");
        }

        if (!int.TryParse(hostPort[(colon + 1)..], out int port) || port <= 0)
        {
            return Result.Failure<(string, int, string)>($"Invalid port in pairing code: {hostPort}");
        }

        return Result.Success((hostPort[..colon], port, psk));
    }
}

/// <summary>
///     What a running daemon advertises for pairing — composed by the host
///     when a networked listener is configured and consumed by the CLI to
///     print the pairing block (text + QR).
/// </summary>
public sealed record DaemonPairingInfo(string Host, int Port, string Psk)
{
    /// <summary>The canonical pairing code string.</summary>
    public string Code => PairingCode.Build(Host, Port, Psk);
}
