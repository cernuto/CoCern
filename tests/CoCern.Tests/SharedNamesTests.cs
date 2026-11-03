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
