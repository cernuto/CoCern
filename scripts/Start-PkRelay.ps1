[CmdletBinding()]
param(
    [string]$RepositoryId = 'pkrelay',
    [string]$Url = 'http://127.0.0.1:5271',
    [string]$DataDirectory = (Join-Path $PSScriptRoot '..\data'),
    [string]$HostDll = (Join-Path $PSScriptRoot '..\src\PkRelay.Host\bin\Debug\net10.0\PkRelay.Host.dll')
)
$ErrorActionPreference = 'Stop'
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$HostDll = [IO.Path]::GetFullPath($HostDll)
if (-not (Test-Path -LiteralPath $HostDll -PathType Leaf)) { throw 'Build PkRelay before starting it.' }
$uri = [Uri]$Url
if (-not $uri.IsLoopback) { throw 'This bootstrap script starts a loopback development hub. Configure HTTPS and enrolled clients for remote access.' }
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
$tokenPath = Join-Path $DataDirectory 'local-client-token'
if (-not (Test-Path -LiteralPath $tokenPath)) {
    $token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    [IO.File]::WriteAllText($tokenPath, $token)
} else {
    $token = [IO.File]::ReadAllText($tokenPath).Trim()
    if ($token.Length -lt 32) { throw 'The existing token file is invalid; keep it private and replace it deliberately.' }
}
if ($IsWindows) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $existingAcl = Get-Acl -LiteralPath $tokenPath
    $existingRules = @($existingAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $alreadyPrivate = $existingAcl.AreAccessRulesProtected -and $existingRules.Count -eq 1 -and
        $existingRules[0].IdentityReference -eq $identity -and
        $existingRules[0].AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
        $existingRules[0].FileSystemRights -eq [Security.AccessControl.FileSystemRights]::FullControl -and
        -not $existingRules[0].IsInherited
    if (-not $alreadyPrivate) {
        $acl = [Security.AccessControl.FileSecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'Allow'))
        Set-Acl -LiteralPath $tokenPath -AclObject $acl
    }
} else {
    [IO.File]::SetUnixFileMode($tokenPath, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
}
$settings = @{
    ASPNETCORE_URLS = $Url
    Relay__DatabasePath = (Join-Path $DataDirectory 'relay.db')
    Relay__Clients__0__Token = $token
    Relay__Clients__0__PrincipalId = [Environment]::UserName
    Relay__Clients__0__IsAdministrator = 'true'
    Relay__Clients__0__Repositories__0 = $RepositoryId
}
$previous = @{}
try {
    foreach ($name in $settings.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    Write-Host "Starting PkRelay at $Url. API reference: $Url/scalar"
    Write-Host "Local adapter token file: $tokenPath"
    & dotnet $HostDll
    if ($LASTEXITCODE -ne 0) { throw "PkRelay exited with code $LASTEXITCODE." }
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    $token = $null
    $settings.Clear()
}
