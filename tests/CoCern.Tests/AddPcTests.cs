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

    [Fact]
    public void The_command_is_one_line_that_stops_at_the_first_failure_and_swaps_the_adapter_folder_whole()
    {
        var command = Status(discovery: true, (5273, Fingerprint)).Addresses[0].Command;
        Assert.DoesNotContain("\n", command);
        Assert.DoesNotContain("\r", command);
        // Its own script block, so the preference and the variables do not stay in the window it was pasted into.
        Assert.StartsWith("& {$ErrorActionPreference='Stop';", command);
        Assert.EndsWith("}", command);
        Assert.Contains($"Add-Type -IgnoreWarnings -WarningAction SilentlyContinue '{DashboardAddPc.PinSource}'", command);
        Assert.DoesNotContain("'", DashboardAddPc.PinSource);
        Assert.DoesNotContain("\n", DashboardAddPc.PinSource);
        // Pinned for the download only, to a temp file of its own that is always deleted.
        Assert.Matches(@"try\{\[CoCernDownloadPin\]::Install\('[0-9A-F]{64}'\);try\{\(New-Object Net\.WebClient\)\.DownloadFile\('https://[^']+/join/adapter\.zip',\$z\)\}finally\{\[CoCernDownloadPin\]::Restore\(\)\}", command);
        Assert.Contains("$z=\"$env:TEMP\\CoCern.Adapter-$i.zip\"", command);
        Assert.Contains("finally{Remove-Item -LiteralPath $z -Force -ErrorAction Ignore}", command);
        // Unpacked into a fresh folder, never over the one CoCern runs from.
        Assert.Contains("$d=\"$r\\adapter\"", command);
        Assert.Contains("Expand-Archive -LiteralPath $z -DestinationPath $n", command);
        Assert.DoesNotContain("-DestinationPath $d", command);
        Assert.DoesNotContain("Expand-Archive $z $d", command);
        Assert.Contains(DashboardAddPc.InUseWarning, command);
        Assert.DoesNotContain("SkipCertificateCheck", command);
        Assert.True(Balanced(command), "braces, brackets, parentheses and quotes should pair up outside strings");
    }

    /// <summary>A rough check that a PowerShell line pairs up its brackets outside quoted strings (single quotes double up to escape, a backtick escapes in double quotes).</summary>
    private static bool Balanced(string line)
    {
        var open = new Stack<char>();
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'')
            {
                for (i++; i < line.Length && !(line[i] == '\'' && (i + 1 == line.Length || line[i + 1] != '\'')); i += line[i] == '\'' ? 2 : 1) { }
                if (i >= line.Length) return false;
            }
            else if (c == '"')
            {
                for (i++; i < line.Length && line[i] != '"'; i += line[i] == '`' ? 2 : 1) { }
                if (i >= line.Length) return false;
            }
            else if (c is '{' or '(' or '[') open.Push(c);
            else if (c is '}' or ')' or ']' && (open.Count == 0 || open.Pop() != (c == '}' ? '{' : c == ')' ? '(' : '['))) return false;
        }
        return open.Count == 0;
    }

#pragma warning disable SYSLIB0014 // ServicePointManager is what the pin sets, because WebClient in PowerShell reads it.
    [Fact]
    public void The_pin_in_the_command_accepts_only_the_hub_certificate_and_puts_back_what_was_there()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var hub = new CertificateRequest("CN=hub", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var other = new CertificateRequest("CN=hub", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
        var original = (ServicePointManager.ServerCertificateValidationCallback, ServicePointManager.SecurityProtocol);
        var context = new AssemblyLoadContext("add-pc-pin", isCollectible: true);
        try
        {
            foreach (var (fingerprint, protocols) in new[] { (CertificateFingerprint.Of(hub), SecurityProtocolType.Tls13), (CertificateFingerprint.Of(hub).ToLowerInvariant(), SecurityProtocolType.SystemDefault) })
            {
                var command = DashboardAddPc.Command("https://hub:5273/", JoinCode, fingerprint);
                var installed = Regex.Match(command, @"\[CoCernDownloadPin\]::Install\('([^']*)'\)").Groups[1].Value;
                Assert.Equal(Regex.Match(command, "--fingerprint ([0-9A-Fa-f]+)").Groups[1].Value, installed);
                Assert.Equal(fingerprint, installed);
                var pin = CompilePin(command, context);
                RemoteCertificateValidationCallback before = (_, _, _, _) => false;
                ServicePointManager.ServerCertificateValidationCallback = before;
                ServicePointManager.SecurityProtocol = protocols;

                pin.GetMethod("Install")!.Invoke(null, [installed]);
                var callback = ServicePointManager.ServerCertificateValidationCallback;
                Assert.NotNull(callback);
                Assert.NotSame(before, callback);
                // The hub's certificate is self-signed, so the chain errors are expected; only the pinned hash decides.
                Assert.True(callback(this, hub, null, SslPolicyErrors.RemoteCertificateChainErrors));
                Assert.False(callback(this, other, null, SslPolicyErrors.None));
                Assert.False(callback(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
                // Windows PowerShell 5.1 may offer only TLS 1.0, so TLS 1.2 is added; the system default is left to the system.
                Assert.Equal(protocols == SecurityProtocolType.SystemDefault ? protocols : protocols | SecurityProtocolType.Tls12, ServicePointManager.SecurityProtocol);

                pin.GetMethod("Restore")!.Invoke(null, null);
                Assert.Same(before, ServicePointManager.ServerCertificateValidationCallback);
                Assert.Equal(protocols, ServicePointManager.SecurityProtocol);
            }
        }
        finally
        {
            (ServicePointManager.ServerCertificateValidationCallback, ServicePointManager.SecurityProtocol) = original;
            context.Unload();
        }
    }
#pragma warning restore SYSLIB0014

    /// <summary>
    /// Compiles the source the command hands to <c>Add-Type</c>, exactly as it appears there, as C# 5: what Windows PowerShell 5.1 compiles. The one warning
    /// allowed is .NET's obsoletion of <c>ServicePointManager</c>, which .NET Framework does not have.
    /// </summary>
    private static Type CompilePin(string command, AssemblyLoadContext context)
    {
        var match = Regex.Match(command, "Add-Type -IgnoreWarnings -WarningAction SilentlyContinue '((?:[^']|'')*)'");
        Assert.True(match.Success, "the command should add the pin type from a single-quoted string");
        var source = match.Groups[1].Value.Replace("''", "'");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("CoCernPinTest" + Guid.NewGuid().ToString("N"), [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp5))], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success, string.Join('\n', result.Diagnostics));
        Assert.Empty(result.Diagnostics.Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning && diagnostic.Id != "SYSLIB0014"));
        image.Position = 0;
        return context.LoadFromStream(image).GetType(DashboardAddPc.PinType)!;
    }

    /// <summary>
    /// The command run for real, by Windows PowerShell 5.1 on Windows and by PowerShell 7 wherever <c>pwsh</c> is on the PATH, against an HTTPS server with
    /// a hub certificate. Off Windows its backslashes become slashes, because .NET there reads a backslash as part of a file name. Where neither shell
    /// is present it is reported as skipped, never as passed.
    /// </summary>
    [SkippableFact]
    public async Task Pasting_the_command_installs_keeps_or_swaps_the_adapter_and_leaves_the_window_as_it_was()
    {
        var shells = new[] { OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : null, OnPath("pwsh") }
            .OfType<string>().Where(File.Exists).Distinct().ToArray();
        Skip.If(shells.Length == 0, "Neither Windows PowerShell nor pwsh is installed here, so the pasted command can't be run.");
        using var certificate = HubCertificate.LoadOrCreate(Path.Combine(root, "hub-certificate.pfx"), "localhost");
        using var stranger = HubCertificate.LoadOrCreate(Path.Combine(root, "other-certificate.pfx"), "localhost");
        byte[] first = Zip(("CoCern.Adapter.dll", "first"), ("CoCern.Adapter.runtimeconfig.json", "{}"), ("old-only.txt", "gone after the swap"));
        byte[] second = Zip(("CoCern.Adapter.dll", "second"), ("CoCern.Adapter.runtimeconfig.json", "{}"));
        var served = first;
        await using var server = await HttpsServer(certificate, () => served);
        var url = $"https://127.0.0.1:{server.Port}/";
        var fingerprint = CertificateFingerprint.Of(certificate);
        foreach (var shell in shells)
        {
            var run = Path.Combine(root, Path.GetFileNameWithoutExtension(shell));
            var local = Path.Combine(run, "local");
            var temp = Path.Combine(run, "temp");
            Directory.CreateDirectory(local);
            Directory.CreateDirectory(temp);
            var installed = Path.Combine(local, "CoCern", "adapter");
            var setupFile = Path.Combine(run, "setup.txt");
            string Paste(string pinned, string fail = "")
            {
                File.Delete(setupFile);
                var output = RunPowerShell(shell, run, DashboardAddPc.Command(url, JoinCode, pinned), local, temp, setupFile, fail);
                log.WriteLine($"{Path.GetFileName(shell)}:\n{output}");
                Assert.Contains("callback restored: True", output);
                Assert.Contains("preference kept: True", output);
                Assert.Empty(Directory.GetFiles(temp, "CoCern.Adapter*"));
                // No fresh or old copy is left beside the adapter folder.
                var cocern = Path.Combine(local, "CoCern");
                Assert.All(Directory.Exists(cocern) ? Directory.GetDirectories(cocern) : [], folder => Assert.Equal(installed, folder));
                return output;
            }

            // A hub presenting another certificate: the download fails and nothing is unpacked or run.
            Assert.Contains("failed:", Paste(CertificateFingerprint.Of(stranger)));
            Assert.False(Directory.Exists(installed));
            Assert.False(File.Exists(setupFile));

            string Quiet(string output)
            {
                Assert.DoesNotContain("failed:", output);
                Assert.DoesNotContain("WARNING", output);
                return output;
            }

            Quiet(Paste(fingerprint));
            Assert.Equal("first", File.ReadAllText(Path.Combine(installed, "CoCern.Adapter.dll")));
            Assert.Equal($"{Path.Combine(installed, "CoCern.Adapter.dll")} --setup --hub {url} --join-code {JoinCode} --fingerprint {fingerprint}", File.ReadAllText(setupFile).Trim());

            // Pasted again on a PC that joined: the same files are left exactly as they are, and setup runs (and changes nothing there).
            var marker = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(installed, "old-only.txt"), marker);
            Quiet(Paste(fingerprint));
            Assert.Equal(marker, File.GetLastWriteTimeUtc(Path.Combine(installed, "old-only.txt")));
            Assert.True(File.Exists(setupFile));

            // A changed adapter while CoCern runs from the folder (the rename refused): nothing changes and one warning says what to close.
            served = second;
            var locked = Paste(fingerprint, fail: "old");
            Assert.Contains("WARNING: CoCern was not updated: ", locked);
            Assert.Contains("Stop-Process -Name CoCern.Adapter", locked);
            Assert.DoesNotContain("failed:", locked);
            Assert.Equal("first", File.ReadAllText(Path.Combine(installed, "CoCern.Adapter.dll")));
            Assert.False(File.Exists(setupFile));

            // The fresh folder failing to move in after the old one moved out: the old one is put back and the error shown.
            Assert.Contains("failed:", Paste(fingerprint, fail: "new"));
            Assert.Equal("first", File.ReadAllText(Path.Combine(installed, "CoCern.Adapter.dll")));
            Assert.False(File.Exists(setupFile));

            // Once nothing runs from it, the changed adapter replaces the folder whole: no file of the old one survives.
            Quiet(Paste(fingerprint));
            Assert.Equal("second", File.ReadAllText(Path.Combine(installed, "CoCern.Adapter.dll")));
            Assert.False(File.Exists(Path.Combine(installed, "old-only.txt")));
            Assert.True(File.Exists(setupFile));

            if (OperatingSystem.IsWindows())
            {
                // CoCern running from the folder: Windows will not rename it, so nothing changes and one warning says what to close.
                served = first;
                using (File.Open(Path.Combine(installed, "CoCern.Adapter.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.Contains("CoCern was not updated", Paste(fingerprint));
                Assert.Equal("second", File.ReadAllText(Path.Combine(installed, "CoCern.Adapter.dll")));
                Assert.False(File.Exists(setupFile));
            }
            served = first;
        }
    }

    private static string? OnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(folder => new[] { Path.Combine(folder, name), Path.Combine(folder, name + ".exe") }).FirstOrDefault(File.Exists);

    /// <summary>
    /// Pastes <paramref name="command"/> into a fresh PowerShell with its own local app data and temp folders, and a <c>dotnet</c> that only records how setup
    /// was started. <paramref name="fail"/> makes one rename fail as Windows does while CoCern runs from the folder: <c>old</c> moving the adapter folder
    /// out of the way, <c>new</c> moving the fresh one in.
    /// </summary>
    private static string RunPowerShell(string shell, string folder, string command, string local, string temp, string setupFile, string fail)
    {
        var commandFile = Path.Combine(folder, "command.txt");
        File.WriteAllText(commandFile, OperatingSystem.IsWindows() ? command : command.Replace('\\', '/'));
        var script = Path.Combine(folder, "paste.ps1");
        File.WriteAllText(script, """
            param([string]$CommandFile, [string]$Local, [string]$Temp, [string]$SetupFile, [string]$Fail)
            $env:LOCALAPPDATA = $Local
            $env:TEMP = $Temp
            function dotnet { Set-Content -LiteralPath $SetupFile -Value ($args -join ' ') }
            function Rename-Item([string]$LiteralPath, [string]$NewName) {
                if (($Fail -eq 'old' -and $NewName -like 'adapter-old-*') -or ($Fail -eq 'new' -and $NewName -eq 'adapter' -and (Split-Path -Leaf $LiteralPath) -notlike 'adapter-old-*')) { throw 'The process cannot access the file because it is being used by another process.' }
                Microsoft.PowerShell.Management\Rename-Item -LiteralPath $LiteralPath -NewName $NewName
            }
            $preference = $ErrorActionPreference
            try { Invoke-Expression (Get-Content -Raw -LiteralPath $CommandFile) } catch { "failed: $($_.Exception.Message)" }
            "callback restored: $($null -eq [Net.ServicePointManager]::ServerCertificateValidationCallback)"
            "preference kept: $($ErrorActionPreference -eq $preference)"
            """);
        var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-CommandFile", commandFile, "-Local", local, "-Temp", temp, "-SetupFile", setupFile, "-Fail", fail })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2))) { process.Kill(entireProcessTree: true); Assert.Fail("PowerShell did not finish."); }
        return output.Result + errors.Result;
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(text);
        return stream.ToArray();
    }

    private sealed class ZipServer(WebApplication app, int port) : IAsyncDisposable
    {
        public int Port { get; } = port;
        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<ZipServer> HttpsServer(X509Certificate2 certificate, Func<byte[]> zip)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.MapGet(JoinEndpoints.AdapterZipPath, () => Results.File(zip(), "application/zip", JoinEndpoints.AdapterZipName));
        await app.StartAsync();
        return new(app, new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port);
    }

    private RelayHostFixture ZipHost(bool discovery, string adapterPath, IPAddress? remote = null) => new(remoteAddress: remote, settings: new Dictionary<string, string?>
    {
        ["Relay:Discovery:Enabled"] = discovery ? "true" : "false", ["Relay:Connect:AdapterPath"] = adapterPath
    }, configureServices: services => services.AddSingleton(new AdapterZipCache(ZipCache)));

    private static HttpClient Client(RelayHostFixture host, string scheme = "https") => host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(scheme + "://localhost"), AllowAutoRedirect = false });

    [Fact]
    public async Task The_zip_endpoint_streams_the_adapter_folder_without_a_dashboard_sign_in()
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Adapter)!, "CoCern.Adapter.runtimeconfig.json"), "{}");
        await using var host = ZipHost(discovery: true, Adapter, remote: IPAddress.Parse("192.168.1.20"));
        using var client = Client(host);
        using var response = await client.GetAsync("/join/adapter.zip");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("CoCern.Adapter.zip", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.Contains("CoCern.Adapter.dll", zip.Entries.Select(entry => entry.FullName));
        Assert.Contains("CoCern.Adapter.runtimeconfig.json", zip.Entries.Select(entry => entry.FullName));
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("CoCern.Host"));
        Assert.EndsWith(".zip", Assert.Single(Directory.GetFiles(ZipCache)));
    }

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

        // A rebuilt adapter gets a zip of its own, and the old one goes.
        File.WriteAllText(Path.Combine(adapter, "CoCern.Adapter.runtimeconfig.json"), "{}");
        Assert.Contains("CoCern.Adapter.runtimeconfig.json", await EntriesAsync(cache, adapter));
        Assert.NotEqual(zip, Assert.Single(Directory.GetFiles(ZipCache)));
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
    public void The_hubs_zip_cache_is_beside_its_database_not_in_the_shared_temp_folder()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Relay:DatabasePath"] = Path.Combine("state", "relay.db") }).Build();
        Assert.Equal(Path.Combine(root, "state", "adapter-zip"), AdapterZipCache.DefaultFolder(configuration, new HostingEnvironment { ContentRootPath = root }));
        Assert.Equal(Path.Combine(root, "data", "adapter-zip"), AdapterZipCache.DefaultFolder(new ConfigurationBuilder().Build(), new HostingEnvironment { ContentRootPath = root }));
    }

    [Fact]
    public async Task The_zip_endpoint_is_404_without_the_adapter_folder()
    {
        await using var host = ZipHost(discovery: true, Path.Combine(root, "missing", "CoCern.Adapter.dll"));
        using var client = Client(host);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/join/adapter.zip")).StatusCode);
    }

    [Fact]
    public async Task The_zip_endpoint_does_not_exist_while_discovery_is_off()
    {
        await using var host = ZipHost(discovery: false, Adapter);
        using var client = Client(host);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/join/adapter.zip")).StatusCode);
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
    public void A_hub_run_from_this_checkout_finds_the_adapter_built_in_it()
    {
        // Visual Studio and Start-CoCern.ps1 run the hub from src/CoCern.Host/bin; these tests run from tests/CoCern.Tests/bin. Both are in the checkout.
        var checkout = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(checkout.FullName, "CoCern.slnx"))) checkout = checkout.Parent!;
        var hostBin = Directory.GetDirectories(Path.Combine(checkout.FullName, "src", "CoCern.Host", "bin")).Select(build => Path.Combine(build, "net10.0")).Where(Directory.Exists);
        foreach (var hub in hostBin.Prepend(AppContext.BaseDirectory))
        {
            var found = DashboardConnect.LocalAdapter(null, hub);
            Assert.NotNull(found);
            Assert.StartsWith(checkout.FullName + Path.DirectorySeparatorChar, found);
            Assert.True(File.Exists(found));
        }
    }

    [Fact]
    public void Other_pcs_get_a_self_contained_build_while_connect_keeps_the_one_it_runs_here()
    {
        var checkout = Path.Combine(root, "checkout");
        var hub = Path.Combine(checkout, "src", "CoCern.Host", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(hub);
        File.WriteAllText(Path.Combine(checkout, "CoCern.slnx"), "<Solution />");
        var published = FakeAdapter(Path.Combine(checkout, "artifacts", "adapter"), selfContained: false);
        var release = FakeAdapter(Path.Combine(checkout, "src", "CoCern.Adapter", "bin", "Release", "net10.0"), selfContained: true);
        Assert.Equal(published, DashboardConnect.LocalAdapter(null, hub));
        Assert.Equal(new AdapterBuild(release, SelfContained: true), DashboardConnect.ServedAdapter(null, hub));

        // Build-CoCern.ps1 -Publish: the host in artifacts/host finds the self-contained adapter in artifacts/adapter, with or without the checkout around it.
        var host = Path.Combine(root, "copied", "host");
        Directory.CreateDirectory(host);
        var beside = FakeAdapter(Path.Combine(root, "copied", "adapter"), selfContained: true);
        Assert.Equal(beside, DashboardConnect.LocalAdapter(null, host));
        // AppContext.BaseDirectory, where the real hub starts, ends with a separator.
        Assert.Equal(new AdapterBuild(beside, SelfContained: true), DashboardConnect.ServedAdapter(null, host + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_build_counts_as_self_contained_only_with_its_runtime_its_exe_and_includedFrameworks()
    {
        var folder = Path.Combine(root, "probe");
        var dll = FakeAdapter(folder, selfContained: true);
        Assert.True(AdapterBuild.At(dll).SelfContained);
        File.Delete(Path.Combine(folder, "coreclr.dll"));
        Assert.False(AdapterBuild.At(dll).SelfContained);
        FakeAdapter(folder, selfContained: true);
        File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.runtimeconfig.json"), "{ not json");
        Assert.False(AdapterBuild.At(dll).SelfContained);
        File.WriteAllText(Path.Combine(folder, "CoCern.Adapter.runtimeconfig.json"), """{ "runtimeOptions": { "includedFrameworks": [], "framework": { "name": "Microsoft.NETCore.App" } } }""");
        Assert.False(AdapterBuild.At(dll).SelfContained);
    }

    [Fact]
    public void The_page_warns_when_the_other_pc_would_need_dotnet()
    {
        var dependent = Status(discovery: true, (5273, Fingerprint));
        Assert.True(dependent.NeedsDotnet);
        Assert.Equal(DashboardAddPc.NeedsDotnetWarning, dependent.Warning);
        Assert.Contains("./scripts/Build-CoCern.ps1 -Publish", dependent.Warning);

        var standalone = Status(discovery: true, (5273, Fingerprint), adapter: FakeAdapter(Path.Combine(root, "self-contained"), selfContained: true));
        Assert.False(standalone.NeedsDotnet);
        Assert.Null(standalone.Warning);
        Assert.Equal(Path.Combine(root, "self-contained"), standalone.AdapterFolder);
    }

    [Fact]
    public void It_says_what_to_turn_on_or_build_first()
    {
        Assert.Contains("Relay:Discovery:Enabled", Status(discovery: false, (5273, Fingerprint)).Unavailable);
        Assert.Contains("Relay:Network:Enabled", Status(discovery: true, https: null).Unavailable);
        Assert.Contains("adapter was not found", Status(discovery: true, (5273, Fingerprint), adapter: Path.Combine(root, "missing.dll")).Unavailable);
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
