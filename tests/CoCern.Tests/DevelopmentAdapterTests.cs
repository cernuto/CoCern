using System.Text;
using System.Text.Json;
using CoCern.Adapter.Development;
using CoCern.Adapter.Setup;
using Xunit;

namespace CoCern.Tests;

public sealed class DevelopmentAdapterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-dev-adapter-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Deployment => Path.Combine(root, "deployment");

    [Theory]
    [InlineData("../source")]
    [InlineData("C:/outside")]
    public void Invalid_or_missing_version_cannot_launch_a_different_folder(string version)
    {
        Directory.CreateDirectory(Deployment);
        File.WriteAllText(Path.Combine(Deployment, "current.json"), JsonSerializer.Serialize(new AdapterManifest(version, Guid.NewGuid().ToString("N"))));
        Assert.ThrowsAny<IOException>(() => AdapterDeployment.Read(Deployment));
    }

    [Fact]
    public void A_tampered_version_is_not_overwritten_or_selected()
    {
        Seed();
        var first = AdapterDeployment.Publish(Source, Deployment);
        File.WriteAllText(AdapterDeployment.AdapterPath(Deployment, first), "tampered");
        Assert.ThrowsAny<IOException>(() => AdapterDeployment.Publish(Source, Deployment));
        Assert.Equal("tampered", File.ReadAllText(AdapterDeployment.AdapterPath(Deployment, first)));
    }

    [Fact]
    public void Claude_development_setup_points_only_the_cocern_entry_at_the_launcher_and_keeps_credentials()
    {
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, ".claude.json");
        var token = Path.Combine(root, "token");
        var original = JsonSerializer.Serialize(new
        {
            numStartups = 7,
            projects = new Dictionary<string, object> { ["C:/work"] = new { allowedTools = Array.Empty<string>() } },
            mcpServers = new Dictionary<string, object>
            {
                ["cocern"] = new { type = "stdio", command = "old-host", args = new[] { "old-adapter.dll" }, env = new Dictionary<string, string> { ["COCERN_TOKEN_FILE"] = token, ["COCERN_URL"] = "https://127.0.0.1:5272/", ["COCERN_FINGERPRINT"] = "existing-pin" } },
                ["other"] = new { type = "stdio", command = "other-server", args = Array.Empty<string>() }
            }
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        File.WriteAllText(config, original, new UTF8Encoding(false));
        var launcher = Path.Combine(Deployment, "versions", new string('a', 64), "CoCern.Adapter.dll");
        var plan = ClientSettings.PlanDevelopmentClaudeCode(config, "dotnet-host", launcher, Deployment, token);
        Assert.False(plan.IsUnchanged);
        plan.Apply(TimeProvider.System);
        using var written = JsonDocument.Parse(File.ReadAllText(config));
        var cocern = written.RootElement.GetProperty("mcpServers").GetProperty("cocern");
        Assert.Equal("dotnet-host", cocern.GetProperty("command").GetString());
        Assert.Equal(new[] { launcher, "--dev-launch", Deployment }, cocern.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("stdio", cocern.GetProperty("type").GetString());
        Assert.Equal("existing-pin", cocern.GetProperty("env").GetProperty("COCERN_FINGERPRINT").GetString());
        Assert.Equal(token, cocern.GetProperty("env").GetProperty("COCERN_TOKEN_FILE").GetString());
        Assert.Equal("other-server", written.RootElement.GetProperty("mcpServers").GetProperty("other").GetProperty("command").GetString());
        Assert.Equal(7, written.RootElement.GetProperty("numStartups").GetInt32());
        Assert.True(written.RootElement.GetProperty("projects").TryGetProperty("C:/work", out _));
        Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(root, ".claude.json.cocern-backup-*"))));
        Assert.True(ClientSettings.PlanDevelopmentClaudeCode(config, "dotnet-host", launcher, Deployment, token).IsUnchanged);
        // Once a deployed launcher is in place, later deployments reach Claude through current.json and leave the entry alone.
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        File.WriteAllText(launcher, "existing immutable launcher");
        var nextLauncher = Path.Combine(Deployment, "versions", new string('b', 64), "CoCern.Adapter.dll");
        Assert.True(ClientSettings.PlanDevelopmentClaudeCode(config, "dotnet-host", nextLauncher, Deployment, token).IsUnchanged);
        Assert.False(ClientSettings.PlanDevelopmentClaudeCode(config, "dotnet-host", nextLauncher, Deployment, token, replaceLauncher: true).IsUnchanged);
    }

    [Fact]
    public void Claude_development_setup_refuses_an_entry_for_a_different_hub()
    {
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, ".claude.json");
        var original = "{\n  \"mcpServers\": {\n    \"cocern\": { \"command\": \"other-host\", \"args\": [\"other-adapter\"], \"env\": { \"COCERN_TOKEN_FILE\": \"another-token\" } }\n  }\n}\n";
        File.WriteAllText(config, original);
        Assert.Throws<ClientSettingsException>(() => ClientSettings.PlanDevelopmentClaudeCode(config, "dotnet", "new-adapter", Deployment, Path.Combine(root, "token")));
        Assert.Throws<IOException>(() => DevelopmentSetup.ConfigureClaudeCode(config, "dotnet", "new-adapter", Deployment, Path.Combine(root, "token")));
        Assert.Equal(original, File.ReadAllText(config));
        Assert.Empty(Directory.GetFiles(root, "*.cocern-backup-*"));
    }

    private void Seed()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "CoCern.Adapter.dll"), "adapter one");
        File.WriteAllText(Path.Combine(Source, "CoCern.Adapter.deps.json"), "{}");
        File.WriteAllText(Path.Combine(Source, "CoCern.Adapter.runtimeconfig.json"), "{}");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
