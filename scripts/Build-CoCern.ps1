[CmdletBinding()]
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $root
try {
    & dotnet restore CoCern.slnx --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
    # Analytics reads snapshots with DuckDB's sqlite extension. The Windows x64 build is bundled in extensions/duckdb and unpacked into
    # the build output (checksums checked), so on Windows neither the build nor the tests download anything. Elsewhere the tests use
    # COCERN_DUCKDB_EXTENSIONS, or download it once into a temp cache. See docs/reference.md#analytics.
    # Tests produce their own binaries and verify behavior before packaging.
    & dotnet test tests/CoCern.Tests/CoCern.Tests.csproj --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($Publish) {
        # The hub runs on Windows x64: publishing for that runtime ships only its DuckDB library (about 35 MB, not every platform's).
        & dotnet publish src/CoCern.Host/CoCern.Host.csproj -c Release -r win-x64 --self-contained false -o artifacts/host --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Host publish failed.' }
        # Publishing for win-x64 unpacks the bundled windows_amd64 extension into artifacts/host/duckdb_extensions.
        & dotnet publish src/CoCern.Adapter/CoCern.Adapter.csproj -c Release -o artifacts/adapter --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Adapter publish failed.' }
    }
} finally { Pop-Location }
