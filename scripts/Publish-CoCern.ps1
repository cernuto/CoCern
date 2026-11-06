[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64',
    [string]$OutputDirectory=(Join-Path $PSScriptRoot '../artifacts/packages')
)
$ErrorActionPreference='Stop'
$taskRepository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskOutput=[IO.Path]::GetFullPath($OutputDirectory)
$taskStamp=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$taskStage=Join-Path $taskOutput ('staging-'+$taskStamp)
$taskBundle=Join-Path $taskStage 'CoCern'
$taskBuild=Join-Path $taskStage 'build'
New-Item -ItemType Directory -Path $taskBundle -Force | Out-Null
$taskAdapterFiles=@()
foreach($taskComponent in @('CoCern.Setup','CoCern.Host','CoCern.Adapter')) {
    $taskProject=Join-Path $taskRepository ('src/'+$taskComponent+'/'+$taskComponent+'.csproj')
    $taskDestination=Join-Path $taskStage $taskComponent
    & dotnet publish $taskProject -c Release -r $Runtime --self-contained true --artifacts-path $taskBuild -o $taskDestination -m:1 --disable-build-servers --nologo -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false -p:DebugSymbols=false -p:DebugType=None
    if($LASTEXITCODE -ne 0){throw ('Publishing '+$taskComponent+' failed. Staging files were retained.')}
    foreach($taskFile in Get-ChildItem -LiteralPath $taskDestination -Recurse -File | Where-Object { $_.Extension -notin @('.pdb','.xml') }) {
        $taskRelative=$taskFile.FullName.Substring($taskDestination.Length+1)
        if($taskComponent -eq 'CoCern.Adapter'){$taskAdapterFiles+=$taskRelative.Replace('\','/')}
        $taskTarget=[IO.Path]::GetFullPath((Join-Path $taskBundle $taskRelative))
        if(!$taskTarget.StartsWith($taskBundle+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Publish output escaped the bundle.'}
        if(Test-Path -LiteralPath $taskTarget) {
            if((Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $taskTarget -Algorithm SHA256).Hash){throw ('Conflicting shared publish file: '+$taskRelative+'. No conflicting runtime was silently selected.')}
        } else {
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskTarget)) -Force | Out-Null
            Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTarget
        }
    }
}
$taskFiles=@(Get-ChildItem -LiteralPath $taskBundle -Recurse -File | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($taskBundle.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
} | Sort-Object { $_.path })
[ordered]@{schema=1;rid=$Runtime;selfContained=$true;files=$taskFiles;adapterFiles=$taskAdapterFiles} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskBundle 'cocern-package.json') -Encoding utf8
$taskZip=Join-Path $taskOutput ('CoCern-'+$Runtime+'-'+$taskStamp+'.zip')
Compress-Archive -LiteralPath $taskBundle -DestinationPath $taskZip -CompressionLevel Optimal
Write-Output ('Self-contained Windows package: '+$taskZip)
Get-Item -LiteralPath (Join-Path $taskBundle 'CoCern.Setup.exe'),(Join-Path $taskBundle 'CoCern.Host.exe'),(Join-Path $taskBundle 'CoCern.Adapter.exe'),$taskZip | Select-Object Name,Length
Write-Output 'Extract the CoCern folder and run CoCern.Setup.exe. End users do not need the SDK, a .NET installation or a developer verification script.'
