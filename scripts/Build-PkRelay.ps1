[CmdletBinding()]
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $root
try {
    & dotnet restore PkRelay.slnx --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
    # Analytics reads snapshots with DuckDB's sqlite extension. Install it once for this machine (this needs internet access the first
    # time), so the tests never download it. CI does the same and sets PKRELAY_DUCKDB_EXTENSIONS. See docs/analytics.md.
    $extensions = Join-Path $root 'artifacts/duckdb_extensions'
    & dotnet run --project src/PkRelay.Host/PkRelay.Host.csproj -c Release --no-restore --no-launch-profile --nologo -- --install-analytics-extension $extensions
    if ($LASTEXITCODE -ne 0) { throw 'Installing the DuckDB sqlite extension failed.' }
    $env:PKRELAY_DUCKDB_EXTENSIONS = $extensions
    # Tests produce their own binaries and verify behavior before packaging.
    & dotnet test tests/PkRelay.Tests/PkRelay.Tests.csproj --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($Publish) {
        # The hub runs on Windows x64: publishing for that runtime ships only its DuckDB library (about 35 MB, not every platform's).
        & dotnet publish src/PkRelay.Host/PkRelay.Host.csproj -c Release -r win-x64 --self-contained false -o artifacts/host --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Host publish failed.' }
        # The hub loads the extension from duckdb_extensions next to it, so analytics works offline. Install the windows_amd64 build
        # whatever machine this runs on.
        $shipped = Join-Path $root 'artifacts/host/duckdb_extensions'
        & dotnet run --project src/PkRelay.Host/PkRelay.Host.csproj -c Release --no-build --no-launch-profile --nologo -- --install-analytics-extension $shipped windows_amd64
        if ($LASTEXITCODE -ne 0) { throw 'Installing the DuckDB sqlite extension for the published hub failed.' }
        & dotnet publish src/PkRelay.Adapter/PkRelay.Adapter.csproj -c Release -o artifacts/adapter --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Adapter publish failed.' }
    }
} finally { Pop-Location }
