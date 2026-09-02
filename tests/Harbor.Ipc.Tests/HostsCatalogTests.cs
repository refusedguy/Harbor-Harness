using Harbor.Ipc.Protocol;

namespace Harbor.Ipc.Tests;

/// <summary>
///     hosts.json catalog tests (sprint 6 zone T): parsing all endpoint
///     kinds, defaults, error reporting, and name resolution.
/// </summary>
public class HostsCatalogTests
{
    private static string WriteTempHostsFile(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-hosts-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Test]
    public async Task Load_MissingFile_ReturnsEmptyCatalog()
    {
        var result = HostsCatalog.Load(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-absent.json"));
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEmpty();
    }

    [Test]
    public async Task Load_ParsesAllKinds_WithDefaults()
    {
        string path = WriteTempHostsFile("""
            {
              "dell":  { "kind": "tailscale", "port": 48710 },
              "nuc":   { "kind": "tcp", "host": "192.168.88.42" },
              "local": { "kind": "uds", "path": "/tmp/harbor.sock" }
            }
            """);
        try
        {
            var result = HostsCatalog.Load(path);
            await Assert.That(result.IsSuccess).IsTrue();

            var dell = result.Value["dell"] as EndpointDescriptor.Tailscale;
            await Assert.That(dell).IsNotNull();
            await Assert.That(dell!.Name).IsEqualTo("dell");
            await Assert.That(dell.ConnectHost).IsEqualTo("dell");
            await Assert.That(dell.Port).IsEqualTo(48710);

            var nuc = result.Value["nuc"] as EndpointDescriptor.Tcp;
            await Assert.That(nuc).IsNotNull();
            await Assert.That(nuc!.Host).IsEqualTo("192.168.88.42");
            await Assert.That(nuc.Port).IsEqualTo(HostsCatalog.DefaultPort);

            var local = result.Value["local"] as EndpointDescriptor.Uds;
            await Assert.That(local).IsNotNull();
            await Assert.That(local!.Path).IsEqualTo("/tmp/harbor.sock");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Load_TailscaleExplicitHost_IsUsedVerbatim()
    {
        string path = WriteTempHostsFile(
            """{ "dell": { "kind": "tailscale", "host": "dell.tail1234.ts.net", "port": 48710 } }""");
        try
        {
            var result = HostsCatalog.Load(path);
            await Assert.That(result.IsSuccess).IsTrue();
            var dell = (EndpointDescriptor.Tailscale)result.Value["dell"];
            await Assert.That(dell.ConnectHost).IsEqualTo("dell.tail1234.ts.net");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Load_MalformedJson_Fails()
    {
        string path = WriteTempHostsFile("{ not json ");
        try
        {
            var result = HostsCatalog.Load(path);
            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("Malformed");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Load_TcpWithoutHost_Fails()
    {
        string path = WriteTempHostsFile("""{ "nuc": { "kind": "tcp", "port": 1 } }""");
        try
        {
            var result = HostsCatalog.Load(path);
            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("require \"host\"");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Resolve_KnownName_ReturnsDescriptor()
    {
        var hosts = new Dictionary<string, EndpointDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["Dell"] = new EndpointDescriptor.Tailscale("dell", null, 48710)
        };
        var result = HostsCatalog.Resolve(hosts, "dell");
        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Resolve_DirectAddress_BypassesCatalog()
    {
        string address = $"100.84.12.{3}"; // CGNAT-shaped test value
        var result = HostsCatalog.Resolve(new Dictionary<string, EndpointDescriptor>(), address);
        await Assert.That(result.IsSuccess).IsTrue();
        var tcp = result.Value as EndpointDescriptor.Tcp;
        await Assert.That(tcp).IsNotNull();
        await Assert.That(tcp!.Host).IsEqualTo(address);
        await Assert.That(tcp.Port).IsEqualTo(HostsCatalog.DefaultPort);
    }

    [Test]
    public async Task Resolve_UnknownName_Fails()
    {
        var result = HostsCatalog.Resolve(new Dictionary<string, EndpointDescriptor>(), "pluto");
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Unknown host 'pluto'");
    }
}
