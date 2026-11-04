using System.Text.Json;
using CoCern.Adapter.Setup;
using CoCern.Contracts;
using Xunit;

namespace CoCern.Tests;

/// <summary>The names the hub, adapter and dashboard share live once in Contracts; these pin the formats both sides read and write.</summary>
public sealed class SharedNamesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "cocern-shared-" + Guid.NewGuid().ToString("N"));

    public SharedNamesTests() => Directory.CreateDirectory(folder);

    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    [Fact]
    public void Recorded_http_origin_is_preferred_and_the_legacy_tls_identity_remains_readable()
    {
        var pin = new string('A', 64);
        var tls = new LocalHubRecord("https://127.0.0.1:5443/", pin);
        var recorded = tls with { HttpUrl = "http://localhost:6123/" };
        File.WriteAllText(Path.Combine(folder, LocalHubRecord.FileName), recorded.ToJson());
        var read = LocalHubRecord.Read(folder);
        Assert.Equal(recorded, read);
        Assert.Equal(("http://127.0.0.1:6123/", (string?)null), ConnectCommand.PreferredLocalHub(read));
        Assert.Equal((tls.Url, pin), ConnectCommand.LocalHub(folder));
        Assert.Equal((tls.Url, pin), ConnectCommand.PreferredLocalHub(tls));
        Assert.Equal((tls.Url, pin), ConnectCommand.PreferredLocalHub(tls with { HttpUrl = "http://hub.example/" }));
    }

    [Theory]
    [InlineData("https://hub.example:5443/", true)]
    [InlineData("https://192.168.1.12:5443/", true)]
    [InlineData("https://127.0.0.1:0/", true)]
    [InlineData("https://127.0.0.1:5443/", false)]
    public void Invalid_local_identity_does_not_supply_a_destination(string url, bool validPin)
    {
        var record = new LocalHubRecord(url, validPin ? new string('A', 64) : "invalid") { HttpUrl = "http://localhost:6123/" };
        File.WriteAllText(Path.Combine(folder, LocalHubRecord.FileName), record.ToJson());
        Assert.Null(ConnectCommand.LocalHub(folder));
        Assert.Null(ConnectCommand.PreferredLocalHub(record));
    }

    [Theory]
    [InlineData("http://127.0.0.1:0/")]
    [InlineData("http://+:6222")]
    [InlineData("http://*:6222")]
    [InlineData("http://0.0.0.0:6222")]
    [InlineData("http://[::]:6222")]
    [InlineData("http://192.168.1.12:6222")]
    [InlineData("http://hub.example:6222")]
    [InlineData("https://127.0.0.1:6222")]
    [InlineData("http://127.0.0.1:6222/private")]
    [InlineData("http://localhost:6222/private/../")]
    [InlineData("http://127.0.0.1:6222/#part")]
    [InlineData("http://127.0.0.1:6222/?key")]
    [InlineData("http://user@localhost:6222/")]
    [InlineData("http://@localhost:6222/")]
    public void Selector_skips_ineligible_addresses_and_preserves_first_eligible_family(string unsafeAddress)
    {
        const string ipv6 = "http://[::1]:6222/", ipv4 = "http://127.0.0.1:8123/";
        Assert.Null(HubAddresses.SelectLoopbackHttp([unsafeAddress]));
        Assert.Equal(ipv6, HubAddresses.SelectLoopbackHttp([unsafeAddress, ipv6, ipv4]));
        Assert.Equal(ipv4, HubAddresses.SelectLoopbackHttp([ipv4, ipv6]));
    }

    [Fact]
    public void Dashboard_selects_the_running_loopback_listener_before_falling_back_to_known_tls()
    {
        var tls = (5443, new string('A', 64));
        foreach (var http in new[] { HubAddresses.LocalUrl + "/", "http://[::1]:6222/" })
            Assert.Equal(["--hub", http], CoCern.Host.Dashboard.DashboardConnect.HubArguments([http], tls)!);
        Assert.Equal(["--hub", "http://127.0.0.1:8123/"], CoCern.Host.Dashboard.DashboardConnect.HubArguments(["http://localhost:8123"], null)!);
        Assert.Equal(["--hub", "https://127.0.0.1:5443/", "--fingerprint", tls.Item2], CoCern.Host.Dashboard.DashboardConnect.HubArguments(["http://0.0.0.0:6222"], tls)!);
        Assert.Null(CoCern.Host.Dashboard.DashboardConnect.HubArguments(["http://0.0.0.0:6222"], null));
    }

    [Fact]
    public void The_local_hub_record_the_hub_writes_is_the_one_connect_reads()
    {
        var fingerprint = new string('a', 64);
        var written = new LocalHubRecord("https://127.0.0.1:5272/", fingerprint);
        File.WriteAllText(Path.Combine(folder, LocalHubRecord.FileName), written.ToJson());

        // The file keeps its documented shape, so an older adapter still reads it.
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, LocalHubRecord.FileName))))
            Assert.Equal(["url", "fingerprint"], json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(written, LocalHubRecord.Read(folder));
        // Connect pins the uppercase form whatever case the file holds.
        Assert.Equal(("https://127.0.0.1:5272/", fingerprint.ToUpperInvariant()), ConnectCommand.LocalHub(folder));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"url\":\"http://127.0.0.1:5271/\",\"fingerprint\":\"" + "AA" + "\"}")]
    [InlineData("{\"fingerprint\":\"" + "ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB" + "\"}")]
    public void A_missing_unreadable_or_unpinnable_record_gives_connect_nothing(string content)
    {
        Assert.Null(ConnectCommand.LocalHub(folder));
        File.WriteAllText(Path.Combine(folder, LocalHubRecord.FileName), content);
        Assert.Null(ConnectCommand.LocalHub(folder));
    }

    [Fact]
    public void A_pasted_fingerprint_is_normalized_once_for_every_reader()
    {
        var pasted = " ab:cd:" + new string('e', 60) + " ";
        var normalized = CertificateFingerprint.Normalize(pasted)!;
        Assert.Equal("ABCD" + new string('E', 60), normalized);
        Assert.True(CertificateFingerprint.IsValid(normalized));
        Assert.False(CertificateFingerprint.IsValid(normalized[1..]));
        Assert.Null(CertificateFingerprint.Normalize(null));
    }
}
