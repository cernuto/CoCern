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

    [Fact]
    public void Identical_content_reuses_its_immutable_copy_but_each_debug_start_requests_a_new_connection()
    {
        Seed();
        var first = AdapterDeployment.Publish(Source, Deployment);
        var firstPath = AdapterDeployment.AdapterPath(Deployment, first);
        Assert.Equal("adapter one", File.ReadAllText(firstPath));
        var second = AdapterDeployment.Publish(Source, Deployment);
        Assert.Equal(first.Version, second.Version);
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Single(Directory.GetDirectories(Path.Combine(Deployment, "versions")));
        File.WriteAllText(Path.Combine(Source, "CoCern.Adapter.dll"), "adapter two");
        var changed = AdapterDeployment.Publish(Source, Deployment);
        Assert.NotEqual(first.Version, changed.Version);
        Assert.Equal("adapter one", File.ReadAllText(firstPath));
        Assert.Equal("adapter two", File.ReadAllText(AdapterDeployment.AdapterPath(Deployment, changed)));
        Assert.Equal(changed, AdapterDeployment.Read(Deployment));
    }

    [Fact]
    public void Incomplete_build_output_does_not_replace_the_working_manifest()
    {
        Seed();
        var before = AdapterDeployment.Publish(Source, Deployment);
        File.Delete(Path.Combine(Source, "CoCern.Adapter.runtimeconfig.json"));
        Assert.ThrowsAny<IOException>(() => AdapterDeployment.Publish(Source, Deployment));
        Assert.Equal(before, AdapterDeployment.Read(Deployment));
    }

    [Theory]
    [InlineData("../source")]
    [InlineData("C:/outside")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Development_setup_changes_only_the_owned_codex_launch_and_preserves_credentials(bool tableEnvironment)
    {
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "config.toml");
        var token = Path.Combine(root, "token");
        var env = tableEnvironment ? $"[mcp_servers.cocern.env]\nCOCERN_TOKEN_FILE = {ClientSettings.TomlString(token)}\nCOCERN_ROLE = 'admin'\nCOCERN_FINGERPRINT = 'existing-pin'\n"
            : $"env = {{ COCERN_TOKEN_FILE = {ClientSettings.TomlString(token)}, COCERN_ROLE = 'admin', COCERN_FINGERPRINT = 'existing-pin' }}\n";
        var original = "# keep this\nmodel = 'existing-model'\n[mcp_servers.cocern]\ncommand = 'old-host' # keep command comment\nargs = ['old-adapter']\n" + env + "\n[mcp_servers.other]\ncommand = 'other-server'\n";
        File.WriteAllText(config, original, new UTF8Encoding(false));
        var launcher = Path.Combine(Deployment, "versions", new string('a', 64), "CoCern.Adapter.dll");
        var plan = ClientSettings.PlanDevelopmentCodex(config, "dotnet-host", launcher, Deployment, token);
        Assert.False(plan.IsUnchanged);
        plan.Apply(TimeProvider.System);
        var expected = original.Replace("'old-host'", "'dotnet-host'").Replace("['old-adapter']", $"[{ClientSettings.TomlString(launcher)}, '--dev-launch', {ClientSettings.TomlString(Deployment)}]");
        Assert.Equal(expected, File.ReadAllText(config));
        Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(root, "config.toml.cocern-backup-*"))));
        Assert.True(ClientSettings.PlanDevelopmentCodex(config, "dotnet-host", launcher, Deployment, token).IsUnchanged);
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        File.WriteAllText(launcher, "existing immutable launcher");
        var nextLauncher = Path.Combine(Deployment, "versions", new string('b', 64), "CoCern.Adapter.dll");
        Assert.True(ClientSettings.PlanDevelopmentCodex(config, "dotnet-host", nextLauncher, Deployment, token).IsUnchanged);
        Assert.Equal(expected, File.ReadAllText(config));
        Assert.False(ClientSettings.PlanDevelopmentCodex(config, "dotnet-host", nextLauncher, Deployment, token, replaceLauncher: true).IsUnchanged);
    }

    [Fact]
    public void Development_setup_refuses_an_entry_for_a_different_hub()
    {
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "config.toml");
        var original = "[mcp_servers.cocern]\ncommand = 'other-host'\nargs = ['other-adapter']\nenv = { COCERN_TOKEN_FILE = 'another-token' }\n";
        File.WriteAllText(config, original);
        Assert.Throws<ClientSettingsException>(() => ClientSettings.PlanDevelopmentCodex(config, "dotnet", "new-adapter", Deployment, Path.Combine(root, "token")));
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
