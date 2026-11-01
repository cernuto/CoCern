[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [string]$AdapterDll = (Join-Path $PSScriptRoot '..\artifacts\adapter-wake\CoCern.Adapter.dll')
)
$ErrorActionPreference = 'Stop'
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
$AdapterDll = [IO.Path]::GetFullPath($AdapterDll)
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw 'A private wake configuration file is required.' }
if (-not (Test-Path -LiteralPath $AdapterDll -PathType Leaf)) { throw 'Publish the wake-capable adapter to the selected output folder before starting it.' }
& dotnet $AdapterDll --wake $ConfigPath
if ($LASTEXITCODE -ne 0) { throw "CoCern wake adapter exited with code $LASTEXITCODE." }
