using CoCern.Contracts.Dashboard;

namespace CoCern.Host.Dashboard;

/// <summary>
/// Everything another PC needs to join, filled in: one PowerShell line per hub address that downloads the adapter from this hub and runs its
/// setup with this hub's address, join code and certificate fingerprint, plus the published adapter folder for doing it by hand. It carries the
/// join code, so only a viewer on the hub's PC sees it.
/// </summary>
internal sealed class DashboardAddPc(IConfiguration configuration, IServiceProvider services, HubListener listener, DashboardConnect connect)
{
    /// <summary>
    /// The certificate pin for the download, as a small C# type because a PowerShell script block cannot serve as the callback: .NET calls it
    /// on a thread with no runspace. <c>Add-Type</c> compiles it in Windows PowerShell 5.1 and PowerShell 7 alike (and is a no-op when the same
    /// source was added before), and <c>WebClient</c> honours <c>ServicePointManager</c>'s callback on both runtimes, where PowerShell 7's
    /// <c>Invoke-WebRequest</c> ignores it. The hash is SHA-256 of the raw certificate, computed by hand because .NET Framework's
    /// <c>GetCertHashString()</c> only gives SHA-1.
    /// </summary>
    private const string Pin = "Add-Type 'using System;using System.Net;using System.Security.Cryptography;public static class CoCernPin{public static void Install(string f){ServicePointManager.ServerCertificateValidationCallback=(s,c,ch,e)=>string.Equals(BitConverter.ToString(SHA256.Create().ComputeHash(c.GetRawCertData())).Replace(\"-\",\"\"),f,StringComparison.OrdinalIgnoreCase);}}'";

    /// <summary>
    /// One line that works pasted into Windows PowerShell 5.1 or PowerShell 7: pin this hub's certificate, download <c>/join/adapter.zip</c> to the
    /// temp folder, unpack it over <c>%LOCALAPPDATA%\CoCern\adapter</c>, then run setup from there (the .exe when the zip has one, else the dll
    /// through dotnet).
    /// </summary>
    internal static string Command(string url, string joinCode, string fingerprint)
    {
        var setup = $"--setup --hub {url} --join-code {joinCode} --fingerprint {fingerprint}";
        return $"{Pin};[CoCernPin]::Install('{fingerprint}');$z=\"$env:TEMP\\CoCern.Adapter.zip\";(New-Object Net.WebClient).DownloadFile('{url}join/adapter.zip',$z);"
            + "$d=\"$env:LOCALAPPDATA\\CoCern\\adapter\";Expand-Archive $z $d -Force;"
            + $"if(Test-Path \"$d\\CoCern.Adapter.exe\"){{& \"$d\\CoCern.Adapter.exe\" {setup}}}else{{dotnet \"$d\\CoCern.Adapter.dll\" {setup}}}";
    }

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
                Command = Command(url, joinCode, https.Fingerprint)
            }).ToArray(),
            FirewallCommand = $"New-NetFirewallRule -DisplayName \"CoCern hub (HTTPS {https.Port})\" -Direction Inbound -Action Allow -Protocol TCP -LocalPort {https.Port} -Profile Private"
        };
    }
}
