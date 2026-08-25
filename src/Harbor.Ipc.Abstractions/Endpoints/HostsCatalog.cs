using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     Loads and resolves <c>~/.harbor/hosts.json</c> — the static host
///     catalog used by <c>harbor connect &lt;name&gt;</c> and
///     <c>harbor status --all</c>. No mDNS/DNS-SD discovery: the list in
///     the config is the source of truth.
/// </summary>
/// <remarks>
///     <para>
///         File shape (all members case-insensitive):
///     </para>
///     <code>
///     {
///       "dell":   { "kind": "tailscale", "host": "dell.tail1234.ts.net", "port": 48710 },
///       "nuc":    { "kind": "tcp", "host": "192.168.88.42", "port": 48710 },
///       "local":  { "kind": "uds", "path": "/tmp/harbor-ipc.sock" }
///     }
///     </code>
///     <para>
///         <c>host</c> on a tailscale entry is optional — when omitted the
///         entry name itself is dialed (MagicDNS resolves it inside the
///         tailnet). <see cref="HostsCatalog.DefaultPort"/> applies when
///         <c>port</c> is absent.
///     </para>
/// </remarks>
public static class HostsCatalog
{
    /// <summary>Default TCP port for Harbor daemons reachable over a network.</summary>
    public const int DefaultPort = 48710;

    /// <summary>Default catalog location: <c>~/.harbor/hosts.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor", "hosts.json");

    /// <summary>
    ///     Load the catalog from <paramref name="path"/>. Missing file is an
    ///     empty catalog, not an error — hosts are opt-in.
    /// </summary>
    public static Result<IReadOnlyDictionary<string, EndpointDescriptor>> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Result.Success<IReadOnlyDictionary<string, EndpointDescriptor>>(
                    new Dictionary<string, EndpointDescriptor>());
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Result.Failure<IReadOnlyDictionary<string, EndpointDescriptor>>(
                    $"hosts.json root must be an object: {path}");
            }

            var hosts = new Dictionary<string, EndpointDescriptor>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var parsed = ParseEntry(entry.Name, entry.Value);
                if (parsed.IsFailure)
                {
                    return Result.Failure<IReadOnlyDictionary<string, EndpointDescriptor>>(parsed.Error);
                }

                hosts[entry.Name] = parsed.Value;
            }

            return Result.Success<IReadOnlyDictionary<string, EndpointDescriptor>>(hosts);
        }
        catch (JsonException ex)
        {
            return Result.Failure<IReadOnlyDictionary<string, EndpointDescriptor>>(
                $"Malformed hosts.json ({path}): {ex.Message}");
        }
        catch (IOException ex)
        {
            return Result.Failure<IReadOnlyDictionary<string, EndpointDescriptor>>(
                $"Cannot read hosts.json ({path}): {ex.Message}");
        }
    }

    /// <summary>Resolve a host name from the catalog to a concrete endpoint.</summary>
    public static Result<EndpointDescriptor> Resolve(
        IReadOnlyDictionary<string, EndpointDescriptor> hosts, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Failure<EndpointDescriptor>("Host name must not be empty.");
        }

        // A bare IPv4/IPv6 or anything containing ':' / '.' that is not in
        // the catalog is treated as a direct address: harbor connect 100.1.2.3.
        if (!hosts.TryGetValue(name, out var descriptor) && IsDirectAddress(name))
        {
            descriptor = new EndpointDescriptor.Tcp(name, DefaultPort);
        }

        return descriptor is null
            ? Result.Failure<EndpointDescriptor>(
                $"Unknown host '{name}'. Add it to {DefaultPath} or pass a direct address.")
            : Result.Success(descriptor);
    }

    /// <summary>True for strings that look like a dialable address rather than a catalog name.</summary>
    private static bool IsDirectAddress(string name)
    {
        return name.Contains(':')                    // IPv6 literal or host:port pair
               || (name.Contains('.') && !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static Result<EndpointDescriptor> ParseEntry(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return Result.Failure<EndpointDescriptor>($"Host '{name}' must be an object.");
        }

        string kind = "uds";
        string? host = null;
        string? path = null;
        int? port = null;

        foreach (var prop in value.EnumerateObject())
        {
            switch (prop.Name.ToLowerInvariant())
            {
                case "kind":
                    kind = prop.Value.GetString()?.ToLowerInvariant() ?? "uds";
                    break;
                case "host" when prop.Value.ValueKind == JsonValueKind.String:
                    host = prop.Value.GetString();
                    break;
                case "path" when prop.Value.ValueKind == JsonValueKind.String:
                    path = prop.Value.GetString();
                    break;
                case "port" when prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int p):
                    port = p;
                    break;
            }
        }

        return kind switch
        {
            "uds" => path is not null
                ? Result.Success<EndpointDescriptor>(new EndpointDescriptor.Uds(path))
                : Result.Failure<EndpointDescriptor>($"Host '{name}': uds entries require \"path\"."),
            "tcp" => host is not null
                ? Result.Success<EndpointDescriptor>(new EndpointDescriptor.Tcp(host, port ?? DefaultPort))
                : Result.Failure<EndpointDescriptor>($"Host '{name}': tcp entries require \"host\"."),
            "tailscale" => Result.Success<EndpointDescriptor>(
                new EndpointDescriptor.Tailscale(name, host, port ?? DefaultPort)),
            _ => Result.Failure<EndpointDescriptor>(
                $"Host '{name}': unknown kind \"{kind}\" (expected uds | tcp | tailscale).")
        };
    }
}
