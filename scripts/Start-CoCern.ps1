[CmdletBinding()]
param(
    [string]$RepositoryId = 'cocern',
    [string]$Url = 'http://127.0.0.1:5271',
    [string]$DataDirectory = (Join-Path $PSScriptRoot '..\data'),
    [string]$HostDll = (Join-Path $PSScriptRoot '..\src\CoCern.Host\bin\Debug\net10.0\CoCern.Host.dll'),
    # Leave the doc cache without the checkout this script runs from.
    [switch]$NoDocs
)
$ErrorActionPreference = 'Stop'
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$HostDll = [IO.Path]::GetFullPath($HostDll)
if (-not (Test-Path -LiteralPath $HostDll -PathType Leaf)) { throw 'Build CoCern before starting it.' }
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
# The doc cache syncs committed docs from git. Out of the box it follows the checkout this script runs from, on its current branch,
# with the default patterns. It reads commits only (git show), never the working tree. A clone without a remote is read locally.
function Get-DocRepositorySettings([string]$Root) {
    if ($NoDocs -or [Environment]::GetEnvironmentVariable('Relay__DocCache__Repositories__0__Id', 'Process')) { return @{} }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return @{} }
    $top = & git -C $Root rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $top -or [IO.Path]::GetFullPath($top).TrimEnd('\', '/') -ne $Root.TrimEnd('\', '/')) { return @{} }
    $branch = & git -C $Root symbolic-ref --quiet --short HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $branch) { Write-Host 'Doc cache: the checkout is not on a branch, so its docs are not synced.'; return @{} }
    $remote = & git -C $Root config --get "branch.$branch.remote" 2>$null
    $merge = & git -C $Root config --get "branch.$branch.merge" 2>$null
    # Follow the branch's upstream when it has one; otherwise the branch itself, from origin (or read locally in a clone without remotes).
    if ($remote -and $remote -ne '.' -and $merge -like 'refs/heads/*') { $branch = $merge.Substring('refs/heads/'.Length) } else { $remote = $null }
    $safe = '^(?![-./])(?!.*\.\.)(?!.*//)(?!.*@\{)[A-Za-z0-9._/-]{1,200}(?<![/.])(?<!\.lock)$'
    if ($branch -notmatch $safe -or ($remote -and $remote -notmatch '^(?![-.])[A-Za-z0-9._-]{1,200}(?<!\.)(?<!\.lock)$')) {
        Write-Host "Doc cache: the branch name '$branch' is not supported, so the checkout's docs are not synced."
        return @{}
    }
    $docs = @{
        Relay__DocCache__Repositories__0__Id = $RepositoryId
        Relay__DocCache__Repositories__0__ClonePath = $Root
        Relay__DocCache__Repositories__0__Branch = $branch
    }
    if ($remote) { $docs.Relay__DocCache__Repositories__0__Remote = $remote }
    Write-Host "Doc cache: syncing $Root ($branch) as repository $RepositoryId."
    return $docs
}
$docSettings = Get-DocRepositorySettings ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')))
foreach ($name in $docSettings.Keys) { $settings[$name] = $docSettings[$name] }
$previous = @{}
try {
    foreach ($name in $settings.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    Write-Host "Starting CoCern at $Url. API reference: $Url/scalar"
    Write-Host "Local adapter token file: $tokenPath"
    & dotnet $HostDll
    if ($LASTEXITCODE -ne 0) { throw "CoCern exited with code $LASTEXITCODE." }
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    $token = $null
    $settings.Clear()
}
