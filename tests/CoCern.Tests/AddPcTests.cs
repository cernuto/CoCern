using CoCern.Contracts;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using CoCern.Contracts.Dashboard;
using CoCern.Host;
using CoCern.Host.Dashboard;
using Xunit;
using Xunit.Abstractions;

namespace CoCern.Tests;

/// <summary>The dashboard's "Add another PC": the filled-in setup command, shown only on the hub's PC, and the adapter zip it downloads.</summary>
public sealed class AddPcTests : IDisposable
{
    private const string Fingerprint = "AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34";
    private const string JoinCode = "JOIN-CODE-1234";
    private readonly string root = Path.Combine(Path.GetTempPath(), "cocern-add-pc-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper log;
    private string Adapter => Path.Combine(root, "artifacts", "adapter", "CoCern.Adapter.dll");
    private string ZipCache => Path.Combine(root, "zip-cache");

    public AddPcTests(ITestOutputHelper log)
    {
        this.log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(Adapter)!);
        File.WriteAllText(Adapter, "");
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private DashboardAddPcStatus Status(bool discovery, (int, string)? https, string? adapter = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Relay:Discovery:Enabled"] = discovery.ToString(), ["Relay:Discovery:JoinCode"] = JoinCode, ["Relay:Connect:AdapterPath"] = adapter ?? Adapter
        }).Build();
        var environment = new HostingEnvironment { ContentRootPath = root };
        var services = new ServiceCollection().AddSingleton(HubJoinCode.Load(configuration, environment)).BuildServiceProvider();
        return new DashboardAddPc(configuration, services, new HubListener { Https = https }, new DashboardConnect(configuration, environment, null!)).Status();
    }

    [Fact]
    public void The_command_is_filled_in_with_this_hubs_address_join_code_and_fingerprint()
    {
        var status = Status(discovery: true, (5273, Fingerprint));
        Assert.Null(status.Unavailable);
        Assert.Equal(Path.GetDirectoryName(Adapter), status.AdapterFolder);
        var byName = status.Addresses[0];
        Assert.Equal($"https://{Environment.MachineName}:5273/", byName.Url);
        Assert.All(status.Addresses, address =>
        {
            Assert.Contains($"{address.Url}join/adapter.zip", address.Command);
            Assert.Contains($"--setup --hub {address.Url} --join-code {JoinCode} --fingerprint {Fingerprint}", address.Command);
            Assert.Contains("CoCern.Adapter.exe", address.Command);
            Assert.Contains("CoCern.Adapter.dll", address.Command);
            Assert.DoesNotContain(".\\CoCern.Adapter.dll --setup", address.Command);
        });
        Assert.Contains("-LocalPort 5273", status.FirewallCommand);
    }

    [SkippableFact]
    public async Task Reinstall_stops_only_its_adapters_and_stops_the_supervisor_first()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var result = await RunStopFixtureAsync("""
            $script:items=@(
                (Item 20 'dotnet.exe' 'C:\dotnet\dotnet.exe' ('"C:\dotnet\dotnet.exe" "'+$install+'\CoCern.Adapter.dll"')),
                (Item 10 'CoCern.Adapter.exe' ($install+'\CoCern.Adapter.exe') ('"'+$install+'\CoCern.Adapter.exe" --node --data-dir "'+$node+'"')),
                (Item 30 'CoCern.Adapter.exe' ($node+'\updates\versions\image\CoCern.Adapter.exe') ('"'+$node+'\updates\versions\image\CoCern.Adapter.exe" --node-worker --data-dir "'+$node+'"')),
                (Item 40 'CoCern.Adapter.exe' ($install+'-other\CoCern.Adapter.exe') ('"'+$install+'-other\CoCern.Adapter.exe"')),
                (Item 50 'dotnet.exe' 'C:\dotnet\dotnet.exe' 'dotnet "C:\other\CoCern.Adapter.dll"'),
                (Item 60 'dotnet.exe' 'C:\dotnet\dotnet.exe' ('dotnet "'+$install+'\Other.dll"')))
            CoCernStopAdapters $install $node
            if($script:stopped[0] -ne 10 -or (($script:stopped | Sort-Object) -join ',') -ne '10,20,30'){throw 'Wrong adapter selection or stop order.'}
            if(($script:items.ProcessId -join ',') -ne '40,50,60'){throw 'An unrelated process was changed.'}
            'fixture-passed'
            """);
        Assert.Contains("fixture-passed", result);
        var command = DashboardAddPc.Command("https://fixture/", JoinCode, Fingerprint);
        Assert.True(command.IndexOf("Expand-Archive", StringComparison.Ordinal) < command.IndexOf("CoCernStopAdapters $d", StringComparison.Ordinal));
        Assert.True(command.IndexOf("CoCernStopAdapters $d", StringComparison.Ordinal) < command.IndexOf("Rename-Item -LiteralPath $d", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Reinstall_refuses_a_reused_process_id_before_terminating_anything()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var result = await RunStopFixtureAsync("""
            $script:items=@((Item 10 'CoCern.Adapter.exe' ($install+'\CoCern.Adapter.exe') ('"'+$install+'\CoCern.Adapter.exe" --node')))
            $script:replace=$true
            $refused=$false
            try{CoCernStopAdapters $install $node}catch{$refused=$true}
            if(-not $refused -or $script:stopped.Count){throw 'A reused process ID was terminated.'}
            'fixture-passed'
            """);
        Assert.Contains("fixture-passed", result);
    }

    private async Task<string> RunStopFixtureAsync(string scenario)
    {
        var fixture = Path.Combine(root, "stop-fixture.ps1");
        var directory = root.Replace("'", "''");
        var generated = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(DashboardAddPc.Command("https://fixture/", JoinCode, Fingerprint)));
        var script = "$ErrorActionPreference='Stop'\nAdd-Type -IgnoreWarnings -WarningAction SilentlyContinue '" + DashboardAddPc.PinSource + "'\n" +
            DashboardAddPc.StopAdaptersSource + "\n$install='" + directory + "\\owned adapter'\n$node='" + directory + "\\node'\n" + """
            $script:stopped=@()
            $script:replace=$false
            function Item($id,$name,$image,$command){[pscustomobject]@{ProcessId=$id;Name=$name;ExecutablePath=$image;CommandLine=$command;CreationDate='original'}}
            function Get-CimInstance($ClassName,$Filter,$ErrorAction){if($Filter -match '^ProcessId = ([0-9]+)$'){$id=[int]$matches[1];foreach($item in $script:items){if($item.ProcessId -eq $id){if($script:replace){$changed=Item $id $item.Name $item.ExecutablePath 'replacement-process';$changed.CreationDate='replacement';$changed}else{$item}}}}else{$script:items}}
            function Invoke-CimMethod($InputObject,$MethodName,$ErrorAction){if($MethodName -eq 'GetOwnerSid'){return [pscustomobject]@{ReturnValue=0;Sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value}};if($MethodName -ne 'Terminate'){throw 'Unexpected process operation.'};$script:stopped+= [int]$InputObject.ProcessId;$script:items=@($script:items | Where-Object ProcessId -ne $InputObject.ProcessId);[pscustomobject]@{ReturnValue=0}}
            function Start-Sleep{throw 'The fixture must never wait on a real process.'}
            """ + "\n$cocernTokens=$null;$cocernParseErrors=$null;$null=[System.Management.Automation.Language.Parser]::ParseInput([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + generated + "')),[ref]$cocernTokens,[ref]$cocernParseErrors);if($cocernParseErrors.Count){throw 'The generated connect command has a syntax error.'}\n" + scenario;
        await File.WriteAllTextAsync(fixture, script);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", $"& ([scriptblock]::Create([IO.File]::ReadAllText('{fixture.Replace("'", "''")}')))" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(); throw; }
        var text = await output;
        Assert.True(process.ExitCode == 0, await error + text);
        return text;
    }

    private RelayHostFixture ZipHost(bool discovery, string adapterPath, IPAddress? remote = null) => new(remoteAddress: remote, settings: new Dictionary<string, string?>
    {
        ["Relay:Discovery:Enabled"] = discovery ? "true" : "false", ["Relay:Connect:AdapterPath"] = adapterPath
    }, configureServices: services => services.AddSingleton(new AdapterZipCache(ZipCache)));

    private static HttpClient Client(RelayHostFixture host, string scheme = "https") => host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(scheme + "://localhost"), AllowAutoRedirect = false });

    [Fact]
    public async Task The_zip_cache_serves_only_a_zip_this_hub_built_and_leaves_no_part_files()
    {
        var adapter = Path.GetDirectoryName(Adapter)!;
        Directory.CreateDirectory(ZipCache);
        File.WriteAllText(Path.Combine(ZipCache, "0123456789abcdef.zip.4567.part"), "left by a hub that stopped mid-build");
        var cache = new AdapterZipCache(ZipCache);
        Assert.Contains("CoCern.Adapter.dll", await EntriesAsync(cache, adapter));
        var zip = Assert.Single(Directory.GetFiles(ZipCache));
        Assert.EndsWith(".zip", zip);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(ZipCache));

        // Another zip under the expected name, whether written while this hub runs or found by a hub that just started, is rebuilt, never served.
        Plant(zip);
        Assert.Equal(["CoCern.Adapter.dll"], await EntriesAsync(cache, adapter));
        Plant(zip);
        Assert.Equal(["CoCern.Adapter.dll"], await EntriesAsync(new AdapterZipCache(ZipCache), adapter));

        // A rebuilt adapter gets a zip of its own, while the immediately previous archive remains available.
        File.WriteAllText(Path.Combine(adapter, "CoCern.Adapter.runtimeconfig.json"), "{}");
        Assert.Contains("CoCern.Adapter.runtimeconfig.json", await EntriesAsync(cache, adapter));
        var retained = Directory.GetFiles(ZipCache);
        Assert.Equal(2, retained.Length);
        Assert.Contains(zip, retained);
        Assert.Contains(retained, path => path != zip);
    }

    private static async Task<string[]> EntriesAsync(AdapterZipCache cache, string adapter)
    {
        await using var stream = await cache.OpenAsync(adapter, CancellationToken.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
    }

    private static void Plant(string path)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        archive.CreateEntry("planted.txt");
    }

    [Fact]
    public async Task The_zip_endpoint_refuses_plain_http_from_another_pc()
    {
        await using var host = ZipHost(discovery: true, Adapter, remote: IPAddress.Parse("192.168.1.20"));
        using var client = Client(host, scheme: "http");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/join/adapter.zip")).StatusCode);
    }

    /// <summary>An adapter folder: a self-contained Windows publish (its runtime, the exe, includedFrameworks) or a framework-dependent build.</summary>
    private static string FakeAdapter(string folder, bool selfContained)
    {
        Directory.CreateDirectory(folder);
        var dll = Path.Combine(folder, "CoCern.Adapter.dll");
        File.WriteAllText(dll, "");
        File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.runtimeconfig.json"), selfContained
            ? """{ "runtimeOptions": { "tfm": "net10.0", "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.14" } ] } }"""
            : """{ "runtimeOptions": { "tfm": "net10.0", "frameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.0" } ] } }""");
        if (selfContained)
            foreach (var file in new[] { "CoCern.Adapter.exe", "coreclr.dll" }) File.WriteAllText(Path.Combine(folder, file), "");
        return dll;
    }

    [Fact]
    public void Adapters_are_found_only_in_the_hubs_own_checkout_never_in_folders_above_it()
    {
        // "above" stands for a folder any local user can write to, such as C:\.
        var above = Path.Combine(root, "above");
        var checkout = Path.Combine(above, "CoCern");
        var hub = Path.Combine(checkout, "src", "CoCern.Host", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(hub);
        FakeAdapter(Path.Combine(above, "artifacts", "adapter"), selfContained: true);
        FakeAdapter(Path.Combine(above, "src", "CoCern.Adapter", "bin", "Debug", "net10.0"), selfContained: true);
        Assert.Null(DashboardConnect.LocalAdapter(null, hub));
        Assert.Null(DashboardConnect.ServedAdapter(null, hub));

        File.WriteAllText(Path.Combine(checkout, "CoCern.slnx"), "<Solution />");
        Assert.Null(DashboardConnect.LocalAdapter(null, hub));
        var debug = FakeAdapter(Path.Combine(checkout, "src", "CoCern.Adapter", "bin", "Debug", "net10.0"), selfContained: false);
        Assert.Equal(debug, DashboardConnect.LocalAdapter(null, hub));
        Assert.Equal(new AdapterBuild(debug, SelfContained: false), DashboardConnect.ServedAdapter(null, hub));

        // A CoCern.slnx planted above the checkout changes nothing: the nearest one is the checkout.
        File.WriteAllText(Path.Combine(above, "CoCern.slnx"), "");
        Assert.Equal(debug, DashboardConnect.LocalAdapter(null, hub));
    }

    [Fact]
    public async Task Only_a_viewer_on_the_hubs_pc_sees_the_join_code()
    {
        var settings = new Dictionary<string, string?> { ["Relay:Clients:0:IsAdministrator"] = "true", ["Relay:Dashboard:ViewerPrincipalIds:0"] = "test-user", ["Relay:Discovery:Enabled"] = "true" };
        await using var host = new RelayHostFixture(remoteAddress: IPAddress.Parse("192.168.1.20"), settings: settings);
        using var browser = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        browser.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/dashboard/api/add-pc")).StatusCode);
        (await browser.PostAsync("/dashboard/api/login", new StringContent(JsonSerializer.Serialize(new { token = RelayHostFixture.Token }), System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        var body = await browser.GetStringAsync("/dashboard/api/add-pc");
        Assert.DoesNotContain(RelayHostFixture.JoinCode, body);
        Assert.Contains("on the hub's PC", JsonSerializer.Deserialize<DashboardAddPcStatus>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Unavailable);
    }
}
