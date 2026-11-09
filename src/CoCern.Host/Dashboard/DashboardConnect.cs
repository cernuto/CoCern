using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using CoCern.Contracts.Dashboard;
using CoCern.Contracts;
using CoCern.Host.Development;

namespace CoCern.Host.Dashboard;

/// <summary>
/// Adds CoCern to the apps on the hub's own PC (Claude Code, the Claude desktop app, Codex) from the dashboard. It runs the adapter's
/// <c>--connect</c>, which edits only this user's app settings, never the hub's. Only a viewer on this PC may use it.
/// </summary>
internal sealed class DashboardConnect(IConfiguration configuration, IHostEnvironment environment, IServer server, HubListener? listener = null)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static bool OnThisPc(HttpContext context) => HubLocalRequest.IsLocal(context);

    /// <summary>
    /// Each app's state. With <paramref name="admin"/> given (the page's administrator tools box was changed), an entry for this hub that gives
    /// other tools shows as one to connect again; without it, the tools an entry gives are only reported.
    /// </summary>
    public Task<DashboardConnectStatus> StatusAsync(bool? admin, CancellationToken cancellationToken) => RunAsync(["--check", .. Role(admin)], cancellationToken);

    /// <summary>Connects one app; with <paramref name="admin"/> given, its entry gives the administrator tools or not to match.</summary>
    public Task<DashboardConnectStatus> ConnectAsync(string client, bool? admin, CancellationToken cancellationToken) => RunAsync(["--only", client, .. Role(admin)], cancellationToken);

    /// <summary>Preview or apply repairs to this user's owned installation; read-only previews never change settings.</summary>
    public Task<DashboardRepairStatus> RepairAsync(bool check, CancellationToken cancellationToken) =>
        RunAdapterAsync("--repair", check ? ["--check"] : [], message => new DashboardRepairStatus { Unavailable = message }, cancellationToken);

    private static string[] Role(bool? admin) => admin is { } wanted ? ["--role", wanted ? "admin" : "worker"] : [];

    /// <summary>The page's <c>admin</c> query value: <c>true</c> or <c>false</c>, else absent (leave the tools as they are).</summary>
    public static bool? AdminChoice(HttpContext context) => context.Request.Query["admin"].ToString() switch { "true" => true, "false" => false, _ => null };

    private async Task<DashboardConnectStatus> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var result = await RunAdapterAsync("--connect", arguments, message => new DashboardConnectStatus { Unavailable = message }, cancellationToken);
        return result with { Clients = result.Clients.Select(PageState).ToArray() };
    }

    private async Task<T> RunAdapterAsync<T>(string command, string[] arguments, Func<string, T> unavailable, CancellationToken cancellationToken)
    {
        if (AdapterDll() is not { } adapter) return unavailable("The CoCern adapter was not found. Run the CoCern installer and choose Repair, then reload.");
        if (TokenFile() is not { } tokenFile) return unavailable("CoCern isn't ready on this PC. Start the hub, then try again.");
        var hubArguments = HubArguments(server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [], listener?.Local);
        if (hubArguments is null) return unavailable("This hub has no connection available on this PC.");
        var start = AdapterProcess(adapter, new[] { command, "--json", "--token-file", tokenFile }.Concat(hubArguments).Concat(RepositoryArguments()).Concat(arguments));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        Process? process = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The adapter could not start.");
            // Both streams are drained together: an adapter writing a long error would otherwise block on a full pipe until the timeout.
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            _ = await errors;
            if (output.Trim().Length == 0 && process.ExitCode != 0)
                return unavailable("The adapter could not complete the check. Run the CoCern installer and choose Repair.");
            using var reply = JsonDocument.Parse(output.Trim() is { Length: > 0 } text ? text : "{}");
            if (reply.RootElement.TryGetProperty("error", out _)) return unavailable("The adapter reported a setup problem. Run the CoCern installer and choose Repair.");
            return reply.RootElement.Deserialize<T>(Web) ?? unavailable("The adapter returned no installation status. Try checking again.");
        }
        catch (Exception exception) when (exception is OperationCanceledException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            try { process?.Kill(); } catch (Exception failure) when (failure is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return unavailable("The adapter did not complete the check. Check again before retrying a repair.");
        }
        finally { process?.Dispose(); }
    }

    /// <summary>A published apphost uses its bundled runtime; framework-dependent DLLs retain the existing dotnet fallback.</summary>
    internal static ProcessStartInfo AdapterProcess(string adapter, IEnumerable<string> arguments)
    {
        var dll = Path.GetExtension(adapter).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var executable = dll ? Path.Combine(Path.GetDirectoryName(adapter)!, Path.GetFileNameWithoutExtension(adapter) + (OperatingSystem.IsWindows() ? ".exe" : "")) : adapter;
        var useExecutable = File.Exists(executable);
        var start = new ProcessStartInfo(useExecutable ? executable : DotnetHost())
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (!useExecutable) start.ArgumentList.Add(adapter);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>
    /// The page shows five states. An entry the adapter reports <c>outdated</c> (this hub's, but pinned to an old address or certificate) is one to
    /// connect, so the page offers Connect; one it <c>updated</c> was just changed, so the page says to restart the app, as for one just added.
    /// One it reports <c>other-hub</c> passes through: Connect would not change it, so the page offers nothing and says why.
    /// </summary>
    internal static DashboardConnectClient PageState(DashboardConnectClient client) => client.State switch
    {
        ConnectStates.Outdated => client with { State = ConnectStates.Available },
        ConnectStates.Updated => client with { State = ConnectStates.Added },
        _ => client
    };


    /// <summary>The adapter Connect runs on this PC: <c>Relay:Connect:AdapterPath</c> when set, else the first build of <see cref="Candidates"/> that exists.</summary>
    internal string? AdapterDll() => LocalAdapter(configuration["Relay:Connect:AdapterPath"], AppContext.BaseDirectory);

    /// <summary>The adapter this hub distributes, optionally separate from the installed helper Connect verifies on this PC.</summary>
    internal AdapterBuild? ServedAdapter() => ServedAdapter(
        configuration["Relay:Connect:DistributionAdapterPath"] is { Length: > 0 } distribution ? distribution : configuration["Relay:Connect:AdapterPath"], AppContext.BaseDirectory);

    /// <summary>
    /// The adapter offered to enrolled nodes as their update (<c>nodeRelease</c>): the newest self-contained build the hub can find, never a framework-dependent
    /// one, because a node runs self-contained with no known .NET host and refuses those. See <see cref="NodeAdapter(string?, string?, string)"/>.
    /// The pasted join command keeps <see cref="ServedAdapter()"/>.
    /// </summary>
    internal AdapterBuild? NodeAdapter() => NodeAdapter(configuration["Relay:Connect:DistributionAdapterPath"], configuration["Relay:Connect:AdapterPath"], AppContext.BaseDirectory);

    /// <summary>
    /// The newest self-contained build, by its dll's write time as <see cref="ServedAdapter(string?, string)"/> orders them, among the configured distribution
    /// and adapter paths, the hub's own node adapter publish (<see cref="NodeAdapterBuilds"/>) and <see cref="Candidates"/>; null when none is self-contained,
    /// so nodes are offered nothing rather than a release every one of them refuses. Configured paths are administrator settings; everything else is inside the
    /// hub's checkout or beside its published host.
    /// </summary>
    internal static AdapterBuild? NodeAdapter(string? distribution, string? configured, string hubFolder)
    {
        var dlls = new List<string>();
        foreach (var path in new[] { distribution, configured })
            if (path is { Length: > 0 } && File.Exists(path)) dlls.Add(Path.GetFullPath(path));
        if (CheckoutRoot(hubFolder) is { } checkout && NodeAdapterBuilds.CurrentDll(checkout) is { } published) dlls.Add(published);
        dlls.AddRange(Candidates(hubFolder).Where(File.Exists));
        return Newest(dlls.Distinct(StringComparer.OrdinalIgnoreCase)).Select(AdapterBuild.At).FirstOrDefault(build => build.SelfContained);
    }

    /// <summary>
    /// The newest adapter build: the published one or the build output in the hub's checkout, whichever was written last, so a published copy that
    /// outlived later rebuilds is not used. <paramref name="hubFolder"/> is where
    /// the running hub's own files are, so the search starts from code this hub already runs, never from the working directory.
    /// </summary>
    internal static string? LocalAdapter(string? configured, string hubFolder) =>
        configured is { Length: > 0 } ? (File.Exists(configured) ? Path.GetFullPath(configured) : null) : Newest(Candidates(hubFolder).Where(File.Exists)).FirstOrDefault();

    /// <summary>
    /// What other PCs download: the newest build found, so a published copy that rebuilds have overtaken is never served (it outlives rebuilds, and a
    /// hub started without the development adapter, or in the moment before that service sets its path, would otherwise hand other PCs and their nodes
    /// a stale adapter). Among builds as new as the newest, a self-contained one comes first, because the pasted command may run on a PC with no .NET;
    /// <see cref="AdapterBuild.SelfContained"/> tells the Add another PC page when the served build needs it. A configured path is served as it is.
    /// </summary>
    internal static AdapterBuild? ServedAdapter(string? configured, string hubFolder)
    {
        if (configured is { Length: > 0 }) return File.Exists(configured) ? AdapterBuild.At(Path.GetFullPath(configured)) : null;
        var builds = Newest(Candidates(hubFolder).Where(File.Exists)).Select(AdapterBuild.At).ToArray();
        if (builds.Length == 0) return null;
        var newest = File.GetLastWriteTimeUtc(builds[0].Dll);
        return builds.FirstOrDefault(build => build.SelfContained && File.GetLastWriteTimeUtc(build.Dll) >= newest) ?? builds[0];
    }

    /// <summary>The adapter builds newest first by their dll's write time, keeping <see cref="Candidates"/> order between equals.</summary>
    private static IEnumerable<string> Newest(IEnumerable<string> dlls) =>
        dlls.Select((dll, order) => (Dll: dll, Order: order, Written: File.GetLastWriteTimeUtc(dll)))
            .OrderByDescending(build => build.Written).ThenBy(build => build.Order).Select(build => build.Dll);

    /// <summary>
    /// Where an adapter build may be: beside the published host (<c>Build-CoCern.ps1 -Publish</c> writes the host to <c>artifacts/host</c> and the adapter to
    /// <c>artifacts/adapter</c>), or inside this hub's CoCern checkout, the nearest folder above the hub that holds <c>CoCern.slnx</c>. Nowhere else above the
    /// hub is searched, and a drive or file system root never counts as either: on Windows any local user can create folders at <c>C:\</c>, and an adapter
    /// planted there would be run by Connect with the administrator token and served to every PC that joins.
    /// </summary>
    internal static IEnumerable<string> Candidates(string hubFolder)
    {
        var hub = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(hubFolder)));
        if (hub.Parent is { Parent: not null } published) yield return Path.Combine(published.FullName, "adapter", "CoCern.Adapter.dll");
        if (CheckoutRoot(hub) is not { } root) yield break;
        yield return Path.Combine(root, "artifacts", "adapter", "CoCern.Adapter.dll");
        foreach (var build in new[] { "Debug", "Release" }) yield return Path.Combine(root, "src", "CoCern.Adapter", "bin", build, "net10.0", "CoCern.Adapter.dll");
    }

    /// <summary>The CoCern checkout the hub at <paramref name="hubFolder"/> runs from, when it runs from one.</summary>
    internal static string? CheckoutRoot(string hubFolder) => CheckoutRoot(new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(hubFolder))));

    private static string? CheckoutRoot(DirectoryInfo hub)
    {
        for (var folder = hub; folder is { Parent: not null }; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "CoCern.slnx"))) return folder.FullName;
        return null;
    }

    private string? TokenFile() => LocalClient() is { } client ? TokenPath(client) : null;

    /// <summary>The client whose token Connect gives the apps on this PC.</summary>
    private RelayClient? LocalClient() => configuration.GetSection("Relay:Clients").Get<RelayClient[]>()?
        .Where(client => client.IsAdministrator && string.IsNullOrWhiteSpace(client.Token) && !string.IsNullOrWhiteSpace(client.TokenFile))
        .FirstOrDefault(client => File.Exists(TokenPath(client)));

    private string TokenPath(RelayClient client) => Path.GetFullPath(Path.Combine(environment.ContentRootPath, client.TokenFile!));

    /// <summary>That client's repository, exactly as configured, when it has just one; with several, a chat must still name the one it means.</summary>
    private string[] RepositoryArguments() => LocalClient()?.Repositories is [{ } repository] && !string.IsNullOrWhiteSpace(repository) ? ["--repository", repository] : [];

    /// <summary>Use the running server's loopback HTTP address, then its known local HTTPS identity.</summary>
    internal static string[]? HubArguments(IEnumerable<string> addresses, (int Port, string Fingerprint)? localHttps)
    {
        if (HubAddresses.SelectLoopbackHttp(addresses) is { } http) return ["--hub", http];
        return localHttps is { } local ? ["--hub", $"https://127.0.0.1:{local.Port}/", "--fingerprint", local.Fingerprint] : null;
    }

    internal static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)) return host;
        if (Environment.ProcessPath is { } process && Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return process;
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") is var rooted && File.Exists(rooted)) return rooted;
        return "dotnet";
    }
}

/// <summary>An adapter build the hub found, and whether its folder runs on a Windows PC that has no .NET installed.</summary>
internal sealed record AdapterBuild(string Dll, bool SelfContained)
{
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public string Folder => Path.GetDirectoryName(Dll)!;

    public static AdapterBuild At(string dll) => new(dll, IsSelfContained(Path.GetDirectoryName(dll)!));

    /// <summary>
    /// A self-contained Windows publish carries the runtime (<c>coreclr.dll</c>) and the <c>CoCern.Adapter.exe</c> the pasted command starts, and its
    /// runtimeconfig lists what it carries under <c>includedFrameworks</c>; a framework-dependent build names the runtime it needs under <c>framework</c>
    /// or <c>frameworks</c> instead. Anything unreadable counts as needing .NET, so the page warns rather than promises.
    /// </summary>
    private static bool IsSelfContained(string folder)
    {
        if (!File.Exists(Path.Combine(folder, "CoCern.Adapter.exe")) || !File.Exists(Path.Combine(folder, "coreclr.dll"))) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "CoCern.Adapter.runtimeconfig.json")), Lenient);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("runtimeOptions", out var options)
                && options.ValueKind == JsonValueKind.Object && options.TryGetProperty("includedFrameworks", out _)
                && !options.TryGetProperty("framework", out _) && !options.TryGetProperty("frameworks", out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
}
