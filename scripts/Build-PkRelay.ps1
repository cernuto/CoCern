[CmdletBinding()]
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $root
try {
    & dotnet restore PkRelay.slnx --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
    # Tests produce their own binaries and verify behavior before packaging.
    & dotnet test tests/PkRelay.Tests/PkRelay.Tests.csproj --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($Publish) {
        & dotnet publish src/PkRelay.Host/PkRelay.Host.csproj -c Release -o artifacts/host --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Host publish failed.' }
        & dotnet publish src/PkRelay.Adapter/PkRelay.Adapter.csproj -c Release -o artifacts/adapter --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Adapter publish failed.' }
    }
} finally { Pop-Location }
