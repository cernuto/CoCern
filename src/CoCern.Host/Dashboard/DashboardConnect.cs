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

    /// <summary>The published adapter if there is one (it outlives rebuilds), else the build output beside the hub's.</summary>
    internal string? AdapterDll()
    {
        if (configuration["Relay:Connect:AdapterPath"] is { Length: > 0 } configured) return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        foreach (var start in new[] { environment.ContentRootPath, AppContext.BaseDirectory })
            for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
            {
                var published = Path.Combine(folder.FullName, "artifacts", "adapter", "CoCern.Adapter.dll");
                if (File.Exists(published)) return published;
                foreach (var build in new[] { "Debug", "Release" })
                {
                    var built = Path.Combine(folder.FullName, "src", "CoCern.Adapter", "bin", build, "net10.0", "CoCern.Adapter.dll");
                    if (File.Exists(built)) return built;
                }
            }
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
