using CoCern.Contracts.Dashboard;

namespace CoCern.Host.Dashboard;

/// <summary>
/// Everything another PC needs to join, filled in: one PowerShell line per hub address that downloads the adapter from this hub and runs its
/// setup with this hub's address, join code and certificate fingerprint, plus the published adapter folder for doing it by hand. It carries the
/// join code, so only a viewer on the hub's PC sees it.
/// </summary>
internal sealed class DashboardAddPc(IConfiguration configuration, IServiceProvider services, HubListener listener, DashboardConnect connect)
{
    /// <summary>The type <see cref="PinSource"/> declares. A changed source needs a new name: a PowerShell window that already added the old one refuses another type by the same name.</summary>
    internal const string PinType = "CoCernDownloadPin";

    /// <summary>
    /// The certificate pin for the download, as a small C# type because a PowerShell script block cannot serve as the callback: .NET calls it
    /// on a thread with no runspace. <c>Add-Type</c> compiles it in Windows PowerShell 5.1 (C# 5 on .NET Framework, so no newer syntax) and
    /// PowerShell 7 alike, and is a no-op when the same source was added before; its warnings are silenced because PowerShell 7 prints .NET's
    /// obsoletion of <c>ServicePointManager</c> even with <c>-IgnoreWarnings</c>, and 5.1 would otherwise count any warning as an error. <c>WebClient</c> honours <c>ServicePointManager</c>'s callback on both
    /// runtimes, where PowerShell 7's <c>Invoke-WebRequest</c> ignores it. The callback and the protocols are process-wide, so <c>Restore</c> puts back
    /// what was there and the rest of that PowerShell window downloads as before. TLS 1.2 is added only for the download, because Windows PowerShell 5.1
    /// may offer nothing newer than TLS 1.0, which current Windows refuses. The hash is SHA-256 of the raw certificate, computed by hand because .NET
    /// Framework's <c>GetCertHashString()</c> only gives SHA-1. It has no line breaks (the command is one line) and no single quote (it sits in a
    /// single-quoted PowerShell string).
    /// </summary>
    internal static readonly string PinSource = OneLine(" ", $$"""
        using System;
        using System.Net;
        using System.Net.Security;
        using System.Security.Cryptography;
        using System.Security.Cryptography.X509Certificates;
        public static class {{PinType}}
        {
            static RemoteCertificateValidationCallback previousCallback;
            static SecurityProtocolType previousProtocols;
            public static void Install(string fingerprint)
            {
                previousCallback = ServicePointManager.ServerCertificateValidationCallback;
                previousProtocols = ServicePointManager.SecurityProtocol;
                if (previousProtocols != 0) ServicePointManager.SecurityProtocol = previousProtocols | SecurityProtocolType.Tls12;
                ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, errors) => Matches(certificate, fingerprint);
            }
            public static void Restore()
            {
                ServicePointManager.ServerCertificateValidationCallback = previousCallback;
                if (previousProtocols != 0) ServicePointManager.SecurityProtocol = previousProtocols;
            }
            public static bool Matches(X509Certificate certificate, string fingerprint)
            {
                if (certificate == null || fingerprint == null) return false;
                using (SHA256 sha = SHA256.Create())
                {
                    return string.Equals(BitConverter.ToString(sha.ComputeHash(certificate.GetRawCertData())).Replace("-", ""), fingerprint, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        """);

    /// <summary>
    /// One line that works pasted into Windows PowerShell 5.1 or PowerShell 7. It runs in its own script block, so its variables and its
    /// <c>$ErrorActionPreference='Stop'</c> do not stay in that window, and any failure (a download refused by the pin, a full disk) stops it before
    /// anything is unpacked or run. It pins this hub's certificate for the download only, downloads <c>/join/adapter.zip</c> to a temp file of its own
    /// (always deleted), and unpacks it into a fresh folder beside <c>%LOCALAPPDATA%\CoCern\adapter</c>. When that adapter folder already holds the
    /// same files (pasting again on a PC that joined) it is left alone; otherwise it is swapped for the fresh folder whole, never unpacked over. Windows
    /// refuses to rename a folder that a running node or chat adapter has files open in, so then nothing changes and one warning says what to close
    /// first; if the fresh folder then fails to move in, the old one is moved back. Copies an interrupted run left beside the adapter folder are removed
    /// at the start. Last it runs setup from the adapter folder (the .exe when the zip has one, else the dll through dotnet), with errors back to
    /// <c>Continue</c> so that setup's own messages on standard error cannot stop it halfway in a host that turns them into PowerShell errors.
    /// </summary>
    internal static string Command(string url, string joinCode, string fingerprint)
    {
        var setup = $"--setup --hub {url} --join-code {joinCode} --fingerprint {fingerprint}";
        var script = $$"""
            $ErrorActionPreference='Stop'
            Add-Type -IgnoreWarnings -WarningAction SilentlyContinue '{{PinSource}}'
            $r="$env:LOCALAPPDATA\CoCern"
            $d="$r\adapter"
            $i=[guid]::NewGuid().ToString('N')
            $z="$env:TEMP\CoCern.Adapter-$i.zip"
            $n="$r\adapter-$i"
            $o="$r\adapter-old-$i"
            Get-ChildItem -LiteralPath $r -Directory -Filter 'adapter-*' -ErrorAction Ignore | Remove-Item -Recurse -Force -ErrorAction Ignore
            try{[{{PinType}}]::Install('{{fingerprint}}');try{(New-Object Net.WebClient).DownloadFile('{{url}}join/adapter.zip',$z)}finally{[{{PinType}}]::Restore()};Expand-Archive -LiteralPath $z -DestinationPath $n}finally{Remove-Item -LiteralPath $z -Force -ErrorAction Ignore}
            function CoCernFiles($p){$l=(Get-Item -LiteralPath $p).FullName.Length;Get-ChildItem -LiteralPath $p -Recurse -File | ForEach-Object {$_.FullName.Substring($l)+'|'+(Get-FileHash -LiteralPath $_.FullName).Hash} | Sort-Object}
            if(-not(Test-Path -LiteralPath $d)){Rename-Item -LiteralPath $n -NewName adapter}elseif(((CoCernFiles $n) -join '/') -eq ((CoCernFiles $d) -join '/')){Remove-Item -LiteralPath $n -Recurse -Force -ErrorAction Ignore}else{try{Rename-Item -LiteralPath $d -NewName "adapter-old-$i"}catch{Remove-Item -LiteralPath $n -Recurse -Force -ErrorAction Ignore;Write-Warning "{{InUseWarning}}";return};try{Rename-Item -LiteralPath $n -NewName adapter}catch{Rename-Item -LiteralPath $o -NewName adapter;Remove-Item -LiteralPath $n -Recurse -Force -ErrorAction Ignore;throw};Remove-Item -LiteralPath $o -Recurse -Force -ErrorAction Ignore}
            $ErrorActionPreference='Continue'
            if(Test-Path "$d\CoCern.Adapter.exe"){& "$d\CoCern.Adapter.exe" {{setup}}}else{dotnet "$d\CoCern.Adapter.dll" {{setup}}}
            """;
        return "& {" + OneLine(";", script) + "}";
    }

    /// <summary>What the command prints, and changes nothing, when the adapter folder cannot be swapped because CoCern is running from it.</summary>
    internal const string InUseWarning = "CoCern was not updated: $d is in use. Close the chats that use CoCern and stop the node (Stop-Process -Name CoCern.Adapter), then paste this command again.";

    /// <summary>Shown above the command when the adapter this hub serves is framework-dependent (a Visual Studio or <c>dotnet build</c> output).</summary>
    internal const string NeedsDotnetWarning ="This hub serves an adapter build that needs the .NET 10 runtime on the other PC. Install it there first, or run ./scripts/Build-CoCern.ps1 -Publish on this PC for one that doesn't, then reload.";

    /// <summary>The non-blank lines of <paramref name="text"/>, trimmed and joined by <paramref name="separator"/>.</summary>
    private static string OneLine(string separator, string text) =>
        string.Join(separator, text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    public DashboardAddPcStatus Status()
    {
        if (!DiscoverySettings.Enabled(configuration) || listener.Https is not { } https)
            return new() { Unavailable = "Other PCs can't join yet. Set Relay:Discovery:Enabled and Relay:Network:Enabled to true, then restart the hub. The Visual Studio launch profile already does." };
        if (connect.ServedAdapter() is not { } adapter)
            return new() { Unavailable = "The CoCern adapter was not found. Build the solution (or run ./scripts/Build-CoCern.ps1 -Publish), then reload." };
        var joinCode = services.GetRequiredService<HubJoinCode>().Value;
        var hosts = new[] { Environment.MachineName }.Concat(HubCertificate.NonLoopbackIPv4Addresses().Select(address => address.ToString()));
        return new()
        {
            AdapterFolder = adapter.Folder,
            NeedsDotnet = !adapter.SelfContained,
            Warning = adapter.SelfContained ? null : NeedsDotnetWarning,
            Addresses = hosts.Distinct(StringComparer.OrdinalIgnoreCase).Select(host => $"https://{host}:{https.Port}/").Select(url => new DashboardHubAddress
            {
                Url = url,
                Command = Command(url, joinCode, https.Fingerprint)
            }).ToArray(),
            FirewallCommand = $"New-NetFirewallRule -DisplayName \"CoCern hub (HTTPS {https.Port})\" -Direction Inbound -Action Allow -Protocol TCP -LocalPort {https.Port} -Profile Private"
        };
    }
}
