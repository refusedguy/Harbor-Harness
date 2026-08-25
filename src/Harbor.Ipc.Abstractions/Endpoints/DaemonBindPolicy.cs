using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CSharpFunctionalExtensions;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     Daemon listen policy: which interface(s) the IPC listener binds.
///     Resolved from <c>HARBOR_LISTEN</c> (or hosts.json-style config):
///     <c>uds</c> (default, local only) | <c>loopback</c> | <c>tailscale0</c>
///     | <c>all</c>.
/// </summary>
public static class DaemonBindPolicy
{
    /// <summary>Default TCP port when a networked listener is configured.</summary>
    public const int DefaultPort = HostsCatalog.DefaultPort;

    /// <summary>
    ///     Resolve the TCP bind address for a networked listen policy.
    /// </summary>
    /// <param name="listenOn">uds | loopback | tailscale0 | all (case-insensitive).</param>
    public static Result<IPAddress> ResolveBindAddress(string? listenOn)
    {
        switch (listenOn?.Trim().ToLowerInvariant())
        {
            case "loopback":
                return Result.Success(IPAddress.Loopback);
            case "all":
                return Result.Success(IPAddress.Any);
            case "tailscale0":
                var ts = FindTailscaleAddress();
                return ts is not null
                    ? Result.Success(ts)
                    : Result.Failure<IPAddress>(
                        "listenOn=tailscale0 but no Tailscale interface with a 100.64/10 address was found. " +
                        "Is 'tailscale up' running on this machine?");
            default:
                return Result.Failure<IPAddress>(
                    $"Unknown HARBOR_LISTEN value '{listenOn}'. Expected: uds | loopback | tailscale0 | all.");
        }
    }

    /// <summary>True when the address is inside the Tailscale CGNAT range 100.64.0.0/10.</summary>
    public static bool IsTailscaleAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        // 100.64.0.0/10 → first 10 bits: 0110010 0xx
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    /// <summary>True for RFC1918 private addresses (LAN).</summary>
    public static bool IsPrivateLanAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>
    ///     The machine's Tailscale unicast address (100.64/10), if the
    ///     interface is up. Scans by CGNAT range — name-agnostic across
    ///     platforms (tailscale0 on Linux, "Tailscale" adapters elsewhere).
    /// </summary>
    public static IPAddress? FindTailscaleAddress()
    {
        foreach (var nic in UpIpv4Interfaces())
        {
            foreach (var addr in nic.Addresses)
            {
                if (IsTailscaleAddress(addr)) return addr;
            }
        }

        return null;
    }

    /// <summary>All IPv4 unicast addresses of operational non-loopback interfaces.</summary>
    public static IReadOnlyList<IPAddress> LanAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var (_, addresses) in UpIpv4Interfaces())
        {
            foreach (var addr in addresses)
            {
                if (!IPAddress.IsLoopback(addr) && IsPrivateLanAddress(addr)) result.Add(addr);
            }
        }

        return result;
    }

    private static IEnumerable<(string Name, IReadOnlyList<IPAddress> Addresses)> UpIpv4Interfaces()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var addresses = new List<IPAddress>();
            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(info.Address))
                {
                    addresses.Add(info.Address);
                }
            }

            if (addresses.Count > 0) yield return (nic.Name, addresses);
        }
    }}

/// <summary>
///     The daemon's pre-shared key store: <c>~/.harbor/daemon.psk</c>.
///     The key is the second authentication factor for networked listeners
///     (defence in depth even inside a tailnet) and travels to clients via
///     QR pairing. Validation semantics mirror
///     <c>Harbor.Transport.Remote.PsAuthHandler.Validate</c> — constant-time.
/// </summary>
public static class PskStore
{
    /// <summary>Default key file location: <c>~/.harbor/daemon.psk</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor", "daemon.psk");

    /// <summary>Generate a fresh 256-bit base64 PSK (same shape as PsAuthHandler.GeneratePsk).</summary>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Load the PSK from <paramref name="path"/>. Missing file → failure (callers decide policy).</summary>
    public static Result<string> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Result.Failure<string>($"No PSK file at {path}.");
            }

            string key = File.ReadAllText(path).Trim();
            return key.Length == 0
                ? Result.Failure<string>($"PSK file at {path} is empty.")
                : Result.Success(key);
        }
        catch (IOException ex)
        {
            return Result.Failure<string>($"Cannot read PSK file ({path}): {ex.Message}");
        }
    }

    /// <summary>Persist a PSK (owner-readable only where the OS allows).</summary>
    public static Result Save(string path, string psk)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, psk);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return Result.Success();
        }
        catch (IOException ex)
        {
            return Result.Failure($"Cannot write PSK file ({path}): {ex.Message}");
        }
    }

    /// <summary>
    ///     Constant-time comparison of a provided key against the expected
    ///     one. Mirrors PsAuthHandler.Validate; duplicated here so the IPC
    ///     stack does not take an ASP.NET FrameworkReference dependency.
    /// </summary>
    public static bool Matches(string? provided, string expected)
        => !string.IsNullOrEmpty(provided) && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
