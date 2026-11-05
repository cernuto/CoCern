using CoCern.Adapter;
using CoCern.Adapter.Node;
using System.Text.Json;
using Xunit;

namespace CoCern.Tests;

public sealed class LocalWakeStartupTests : IDisposable
{
    private const string Secret = "synthetic-startup-test-credential";
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-wake-startup-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("replaced", "credential_mismatch")]
    public void Credential_refusals_are_distinct_and_do_not_rehome_the_session(string kind, string expected)
    {
        Directory.CreateDirectory(root);
        var tokenFile = Path.Combine(root, Secret);
        if (kind != "missing") File.WriteAllText(tokenFile, kind == "oversized" ? new string('x', 16385) : "replacement-synthetic-token");
        var reasons = new List<string>();
        Assert.Null(LocalWakeConnection.Create(Options(), tokenFile, Path.Combine(root, "cache"), reasons.Add));
        Assert.Equal(expected, Assert.Single(reasons));
        Assert.DoesNotContain(Secret, string.Join(" ", reasons));
        Assert.DoesNotContain(root, string.Join(" ", reasons));
        Assert.False(Directory.Exists(Path.Combine(root, "cache")));
    }

    [Fact]
    public void A_ready_connection_reports_success_only_after_private_metadata_exists()
    {
        Directory.CreateDirectory(root);
        var tokenFile = Path.Combine(root, "token");
        File.WriteAllText(tokenFile, Secret);
        var reasons = new List<string>();
        var connection = LocalWakeConnection.Create(Options(), tokenFile, Path.Combine(root, "cache"), reasons.Add);
        Assert.NotNull(connection);
        Assert.Equal("connection_ready", Assert.Single(reasons));
        Assert.True(File.Exists(Path.Combine(connection.Folder, "connection.json")));
        Assert.DoesNotContain(Secret, File.ReadAllText(Path.Combine(connection.Folder, "connection.json")));
    }

    [Fact]
    public async Task Setup_io_failure_remains_nonfatal_and_reports_no_path_or_exception_text()
    {
        Directory.CreateDirectory(root);
        var tokenFile = Path.Combine(root, "token");
        File.WriteAllText(tokenFile, Secret);
        var blockedCache = Path.Combine(root, "cache-is-a-file");
        File.WriteAllText(blockedCache, "fixture");
        var reasons = new List<string>();
        await using var wake = LocalChatWake.TryCreate(Options(), tokenFile, blockedCache, reasons.Add);
        Assert.Null(wake);
        Assert.Equal("setup_io_error", Assert.Single(reasons));
        Assert.DoesNotContain(root, string.Join(" ", reasons));
        Assert.DoesNotContain(Secret, string.Join(" ", reasons));
    }

    private static AdapterOptions Options() => new("http://127.0.0.1:5271/", Secret);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
