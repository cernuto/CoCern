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
    internal const string PinType = "CoCernDownloadPinV2";

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
        using System.Runtime.InteropServices;
        public static class {{PinType}}
        {
            [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
            [DllImport("kernel32.dll")]
            static extern IntPtr LocalFree(IntPtr memory);
            public static string[] Arguments(string commandLine)
            {
                int count;
                IntPtr memory = CommandLineToArgvW(commandLine, out count);
                if (memory == IntPtr.Zero) throw new InvalidOperationException("CoCern process identity could not be read.");
                try { string[] result = new string[count]; for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size)); return result; }
                finally { LocalFree(memory); }
            }
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
    /// (always deleted), and unpacks it into a fresh folder beside <c>%LOCALAPPDATA%\CoCern\adapter</c>. It unpacks with .NET's
    /// <c>ZipFile.ExtractToDirectory</c>, present in Windows PowerShell 5.1 and PowerShell 7 alike and refusing entries that would land outside the
    /// folder, rather than the <c>Expand-Archive</c> cmdlet, whose module can be missing or fail to load (and whose name was once mangled to
    /// <c>Expand-Archi…</c> on its way to a PowerShell window, stopping the join). When that adapter folder already holds the
    /// same files (pasting again on a PC that joined) it and its adapters are left alone; otherwise it is swapped whole, never unpacked over.
    /// Running adapters belonging to this installation are stopped before replacement (the node supervisor first). Unverifiable identity or a failed stop
    /// refuses replacement; if the fresh folder then fails to move in, the old one is moved back. Copies an interrupted run left beside the adapter folder are removed
    /// at the start. Last it runs setup from the adapter folder (the .exe when the zip has one, else the dll through dotnet), with errors back to
    /// <c>Continue</c> so that setup's own messages on standard error cannot stop it halfway in a host that turns them into PowerShell errors.
    /// </summary>
    internal static string Command(string url, string joinCode, string fingerprint)
    {
        var setup = $"--setup --hub {url} --join-code {joinCode} --fingerprint {fingerprint}";
        var script = $$$"""
            $ErrorActionPreference='Stop'
            Add-Type -IgnoreWarnings -WarningAction SilentlyContinue '{{{PinSource}}}'
            {{{StopAdaptersSource}}}
            $r=[IO.Path]::GetFullPath("$env:LOCALAPPDATA\CoCern")
            $d=CoCernContained $r "$r\adapter"
            $i=[guid]::NewGuid().ToString('N')
            $z="$env:TEMP\CoCern.Adapter-$i.zip"
            $n=CoCernContained $r "$r\adapter-$i"
            $o=CoCernContained $r "$r\adapter-old-$i"
            Get-ChildItem -LiteralPath $r -Directory -Filter 'adapter-*' -ErrorAction Ignore | ForEach-Object { $p=CoCernContained $r $_.FullName; Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Ignore }
            try{[{{{PinType}}}]::Install('{{{fingerprint}}}');try{(New-Object Net.WebClient).DownloadFile('{{{url}}}join/adapter.zip',$z)}finally{[{{{PinType}}}]::Restore()};Add-Type -AssemblyName System.IO.Compression.FileSystem;[IO.Compression.ZipFile]::ExtractToDirectory($z,$n)}finally{Remove-Item -LiteralPath $z -Force -ErrorAction Ignore}
            if(-not((Test-Path -LiteralPath "$n\CoCern.Adapter.exe" -PathType Leaf) -or (Test-Path -LiteralPath "$n\CoCern.Adapter.dll" -PathType Leaf))){Remove-Item -LiteralPath $n -Recurse -Force -ErrorAction Ignore;throw 'The downloaded CoCern adapter is incomplete; the installed adapter was left running.'}
            function CoCernFiles($p){$l=(Get-Item -LiteralPath $p).FullName.Length;Get-ChildItem -LiteralPath $p -Recurse -File | ForEach-Object {$_.FullName.Substring($l)+'|'+(Get-FileHash -LiteralPath $_.FullName).Hash} | Sort-Object}
            $exists=Test-Path -LiteralPath $d
            $replace=(-not $exists) -or (((CoCernFiles $n) -join '/') -ne ((CoCernFiles $d) -join '/'))
            if($replace){try{CoCernStopAdapters $d (CoCernContained $r "$r\node")}catch{Remove-Item -LiteralPath $n -Recurse -Force -ErrorAction Ignore;throw 'CoCern was not replaced: its running adapters could not be safely stopped. Run this command as the installation owner and reconnect chats afterwards.'}}
            if(-not $exists){Rename-Item -LiteralPath $n -NewName adapter}elseif(-not $replace){Remove-Item -LiteralPath $n -Recurse -Force -ErrorAction Ignore}else{try{Rename-Item -LiteralPath $d -NewName "adapter-old-$i"}catch{Remove-Item -LiteralPath $n -Recurse -Force -ErrorAction Ignore;Write-Warning "{{{InUseWarning}}}";return};try{Rename-Item -LiteralPath $n -NewName adapter}catch{Rename-Item -LiteralPath $o -NewName adapter;Remove-Item -LiteralPath $n -Recurse -Force -ErrorAction Ignore;throw};Remove-Item -LiteralPath $o -Recurse -Force -ErrorAction Ignore}
            $ErrorActionPreference='Continue'
            if(Test-Path "$d\CoCern.Adapter.exe"){& "$d\CoCern.Adapter.exe" {{{setup}}}}else{dotnet "$d\CoCern.Adapter.dll" {{{setup}}}}
            """;
        return "& {" + OneLine(";", script) + "}";
    }

    /// <summary>What the command prints, and changes nothing, when the adapter folder cannot be swapped because CoCern is running from it.</summary>
    internal const string InUseWarning = "CoCern was not updated: $d is still in use after stopping its adapters. Close the chats that use this installation, then paste this command again.";

    /// <summary>Generated installer helper; process metadata is never printed because it can contain credentials.</summary>
    internal static readonly string StopAdaptersSource = OneLine(";", $$$"""
        function CoCernNoLinks($p){$x=[IO.Path]::GetFullPath($p);while($x){if(Test-Path -LiteralPath $x){if(((Get-Item -LiteralPath $x -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'CoCern installation links are unsupported.'}};$next=[IO.Path]::GetDirectoryName($x);if($next -eq $x){break};$x=$next}}
        function CoCernContained($root,$path){$root=[IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar);$path=[IO.Path]::GetFullPath($path);if(-not $path.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'CoCern installation path is outside its root.'};CoCernNoLinks $path;return $path}
        function CoCernAdapter($p,$install,$node){if(-not $p.ExecutablePath){return};if(-not $p.CommandLine){$known=[IO.Path]::GetFullPath([string]$p.ExecutablePath);if([IO.Path]::GetFileName($known) -ieq 'CoCern.Adapter.exe' -and ($known.StartsWith($install+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $known.StartsWith($node+'\updates\versions\',[StringComparison]::OrdinalIgnoreCase))){throw 'CoCern process identity is incomplete.'};return};$cocernArgs=[{{{PinType}}}]::Arguments([string]$p.CommandLine);if($cocernArgs.Count -eq 0){return};$image=[string]$p.ExecutablePath;if([IO.Path]::GetFileName($image) -ieq 'dotnet.exe'){if($cocernArgs.Count -lt 2 -or [IO.Path]::GetFileName($cocernArgs[1]) -ine 'CoCern.Adapter.dll'){return};$image=$cocernArgs[1]}elseif([IO.Path]::GetFileName($image) -ine 'CoCern.Adapter.exe'){return};if(-not [IO.Path]::IsPathRooted($image)){return};$image=[IO.Path]::GetFullPath($image);$installed=$image.StartsWith($install+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase);$worker=$image.StartsWith($node+'\updates\versions\',[StringComparison]::OrdinalIgnoreCase) -and ($cocernArgs -contains '--node-worker');if(-not $installed -and -not $worker){return};if($worker){$k=[Array]::IndexOf($cocernArgs,'--data-dir');if($k -lt 0 -or $k+1 -ge $cocernArgs.Count -or -not [IO.Path]::IsPathRooted($cocernArgs[$k+1]) -or [IO.Path]::GetFullPath($cocernArgs[$k+1]).TrimEnd('\') -ine $node.TrimEnd('\')){return}};CoCernNoLinks $image;if(-not $p.CreationDate){throw 'CoCern process identity is incomplete.'};[pscustomobject]@{Process=$p;Priority=[int](-not ($cocernArgs -contains '--node'))}}
        function CoCernStopAdapters($install,$node){$install=[IO.Path]::GetFullPath($install).TrimEnd('\');$node=[IO.Path]::GetFullPath($node).TrimEnd('\');CoCernNoLinks $install;CoCernNoLinks $node;$targets=@(Get-CimInstance Win32_Process -Filter "Name = 'CoCern.Adapter.exe' OR Name = 'dotnet.exe'" -ErrorAction Stop | ForEach-Object {CoCernAdapter $_ $install $node});foreach($target in @($targets | Sort-Object Priority)){$p=$target.Process;$live=@(Get-CimInstance Win32_Process -Filter "ProcessId = $($p.ProcessId)" -ErrorAction Stop);if($live.Count -eq 0){continue};if($live.Count -ne 1 -or $live[0].CreationDate -ne $p.CreationDate -or $live[0].ExecutablePath -cne $p.ExecutablePath -or $live[0].CommandLine -cne $p.CommandLine){throw 'CoCern process identity changed.'};$owner=Invoke-CimMethod -InputObject $live[0] -MethodName GetOwnerSid -ErrorAction Stop;if($owner.ReturnValue -ne 0 -or $owner.Sid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'CoCern adapter belongs to another owner.'};Write-Host ('Stopping CoCern adapter '+$p.ProcessId);$stopped=Invoke-CimMethod -InputObject $live[0] -MethodName Terminate -ErrorAction Stop;if($stopped.ReturnValue -ne 0){throw 'CoCern adapter could not be stopped.'};$timer=[Diagnostics.Stopwatch]::StartNew();while(@(Get-CimInstance Win32_Process -Filter "ProcessId = $($p.ProcessId)" -ErrorAction Stop).Count -gt 0){if($timer.ElapsedMilliseconds -ge 10000){throw 'CoCern adapter did not exit.'};Start-Sleep -Milliseconds 100}};$remaining=@(Get-CimInstance Win32_Process -Filter "Name = 'CoCern.Adapter.exe' OR Name = 'dotnet.exe'" -ErrorAction Stop | ForEach-Object {CoCernAdapter $_ $install $node});if($remaining.Count){throw 'CoCern adapter restarted before installation.'}}
        """);

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
