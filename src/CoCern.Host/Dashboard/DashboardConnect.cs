using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using CoCern.Contracts.Dashboard;

namespace CoCern.Host.Dashboard;

/// <summary>
/// Adds CoCern to the apps on the hub's own PC (Claude Code, the Claude desktop app, Codex) from the dashboard. It runs the adapter's
/// <c>--connect</c>, which edits only this user's app settings, never the hub's. Only a viewer on this PC may use it.
/// </summary>
internal sealed class DashboardConnect(IConfiguration configuration, IHostEnvironment environment, IServer server, HubListener? listener = null)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    internal static readonly string[] ClientIds = ["claude-code", "claude-desktop", "codex"];

    public static bool OnThisPc(HttpContext context) => context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote);

    public Task<DashboardConnectStatus> StatusAsync(CancellationToken cancellationToken) => RunAsync(["--check"], cancellationToken);

    public Task<DashboardConnectStatus> ConnectAsync(string client, CancellationToken cancellationToken) => RunAsync(["--only", client], cancellationToken);

    private async Task<DashboardConnectStatus> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        if (AdapterDll() is not { } adapter) return new() { Unavailable = "The CoCern adapter was not found. Build the solution (or run Build-CoCern.ps1 -Publish), then reload." };
        if (TokenFile() is not { } tokenFile) return new() { Unavailable = "No administrator client reads its token from a file (Relay:Clients:N:TokenFile), so there is no token to give the apps." };
        var start = new ProcessStartInfo(DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { adapter, "--connect", "--json", "--token-file", tokenFile }.Concat(HubArguments()).Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The adapter could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            // Both streams are drained together: an adapter writing a long error would otherwise block on a full pipe until the timeout.
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var failure = await errors;
            if (output.Trim().Length == 0 && process.ExitCode != 0)
                return new() { Unavailable = "The adapter failed: " + (failure.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() is { Length: > 0 } line ? line[..Math.Min(line.Length, 300)] : $"exit code {process.ExitCode}") };
            using var reply = JsonDocument.Parse(output.Trim() is { Length: > 0 } text ? text : "{}");
            if (reply.RootElement.TryGetProperty("error", out var error))
                return new() { Unavailable = error.ValueKind == JsonValueKind.String ? error.GetString() : "The adapter failed." };
            return new() { Clients = reply.RootElement.TryGetProperty("clients", out var clients) ? clients.Deserialize<DashboardConnectClient[]>(Web) ?? [] : [] };
        }
        catch (Exception exception) when (exception is OperationCanceledException or JsonException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return new() { Unavailable = "The adapter did not answer. Try again." };
        }
    }

    /// <summary>The adapter Connect runs on this PC: <c>Relay:Connect:AdapterPath</c> when set, else the first build of <see cref="Candidates"/> that exists.</summary>
    internal string? AdapterDll() => LocalAdapter(configuration["Relay:Connect:AdapterPath"], AppContext.BaseDirectory);

    /// <summary>The adapter this hub serves to other PCs as <c>/join/adapter.zip</c>; see <see cref="ServedAdapter(string?, string)"/>.</summary>
    internal AdapterBuild? ServedAdapter() => ServedAdapter(configuration["Relay:Connect:AdapterPath"], AppContext.BaseDirectory);

    /// <summary>
    /// The published adapter if there is one (it outlives rebuilds), else the build output in the hub's checkout. <paramref name="hubFolder"/> is where
    /// the running hub's own files are, so the search starts from code this hub already runs, never from the working directory.
    /// </summary>
    internal static string? LocalAdapter(string? configured, string hubFolder) =>
        configured is { Length: > 0 } ? (File.Exists(configured) ? Path.GetFullPath(configured) : null) : Candidates(hubFolder).FirstOrDefault(File.Exists);

    /// <summary>
    /// What other PCs download. A self-contained build comes first, because the pasted command may run on a PC with no .NET; without one it is the same
    /// build Connect uses here, and <see cref="AdapterBuild.SelfContained"/> tells the Add another PC page to say so. A configured path is served as it is.
    /// </summary>
    internal static AdapterBuild? ServedAdapter(string? configured, string hubFolder)
    {
        if (configured is { Length: > 0 }) return File.Exists(configured) ? AdapterBuild.At(Path.GetFullPath(configured)) : null;
        var builds = Candidates(hubFolder).Where(File.Exists).Select(AdapterBuild.At).ToArray();
        return builds.FirstOrDefault(build => build.SelfContained) ?? builds.FirstOrDefault();
    }

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

    private static string? CheckoutRoot(DirectoryInfo hub)
    {
        for (var folder = hub; folder is { Parent: not null }; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "CoCern.slnx"))) return folder.FullName;
        return null;
    }

    private string? TokenFile() => configuration.GetSection("Relay:Clients").Get<RelayClient[]>()?
        .Where(client => client.IsAdministrator && string.IsNullOrWhiteSpace(client.Token) && !string.IsNullOrWhiteSpace(client.TokenFile))
        .Select(client => Path.GetFullPath(Path.Combine(environment.ContentRootPath, client.TokenFile!))).FirstOrDefault(File.Exists);

    /// <summary>
    /// Where the apps reach the hub: its loopback HTTPS with the certificate pinned, so their token never goes to another process on that
    /// port; else (that listener off) its loopback HTTP address when it is not the adapter's default.
    /// </summary>
    private IEnumerable<string> HubArguments()
    {
        if (listener?.Local is { } local) return ["--hub", $"https://127.0.0.1:{local.Port}/", "--fingerprint", local.Fingerprint];
        var url = DashboardSignInLink.LoopbackUrl(server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [])?.Replace("/dashboard/", "/");
        return url is null || url == "http://127.0.0.1:5271/" ? [] : ["--hub", url];
    }

    private static string DotnetHost()
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
