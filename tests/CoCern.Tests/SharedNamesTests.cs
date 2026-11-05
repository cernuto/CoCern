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
