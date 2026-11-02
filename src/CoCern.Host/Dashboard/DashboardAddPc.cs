using CoCern.Contracts.Dashboard;

namespace CoCern.Host.Dashboard;

/// <summary>
/// Everything another PC needs to join, filled in: the published adapter folder, and the setup command with this hub's address, join
/// code and certificate fingerprint. It carries the join code, so only a viewer on the hub's PC sees it.
/// </summary>
internal sealed class DashboardAddPc(IConfiguration configuration, IServiceProvider services, HubListener listener, DashboardConnect connect)
{
    public DashboardAddPcStatus Status()
    {
        if (!DiscoverySettings.Enabled(configuration) || listener.Https is not { } https)
            return new() { Unavailable = "Other PCs can't join yet. Set Relay:Discovery:Enabled and Relay:Network:Enabled to true, then restart the hub. The Visual Studio launch profile already does." };
        if (connect.AdapterDll() is not { } adapter)
            return new() { Unavailable = "The CoCern adapter was not found. Build the solution (or run ./scripts/Build-CoCern.ps1 -Publish), then reload." };
        var folder = Path.GetDirectoryName(adapter)!;
        var joinCode = services.GetRequiredService<HubJoinCode>().Value;
        var hosts = new[] { Environment.MachineName }.Concat(HubCertificate.NonLoopbackIPv4Addresses().Select(address => address.ToString()));
        return new()
        {
            AdapterFolder = folder,
            Addresses = hosts.Distinct(StringComparer.OrdinalIgnoreCase).Select(host => $"https://{host}:{https.Port}/").Select(url => new DashboardHubAddress
            {
                Url = url,
                Command = $"dotnet .\\CoCern.Adapter.dll --setup --hub {url} --join-code {joinCode} --fingerprint {https.Fingerprint}"
            }).ToArray(),
            FirewallCommand = $"New-NetFirewallRule -DisplayName \"CoCern hub (HTTPS {https.Port})\" -Direction Inbound -Action Allow -Protocol TCP -LocalPort {https.Port} -Profile Private"
        };
    }
}
