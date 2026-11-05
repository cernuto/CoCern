[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Selection,
    [switch]$Publish,
    [ValidateRange(1,20)][int]$TimeoutMinutes=10
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $taskRoot
try {
    $taskHead=(& git rev-parse HEAD).Trim()
    if (& git status --porcelain --untracked-files=no) { throw 'Commit the verification source first.' }
    $taskSelection=Get-Content -Raw -LiteralPath $Selection | ConvertFrom-Json
    if ($taskSelection.commit -ne $taskHead) { throw 'Roslyn selection must name the current commit.' }
    $taskSymbols=@($taskSelection.changedSymbols | Where-Object { $_ -is [string] -and ![string]::IsNullOrWhiteSpace($_) })
    if (!$taskSymbols.Count) { throw 'Name the changed symbols before verification; no automatic test selection.' }
    $taskTests=@($taskSelection.tests)
    foreach($taskTest in $taskTests) {
        if ($taskTest.method -notmatch '^[A-Za-z_][A-Za-z0-9_.+]*$') { throw 'Select exact test methods, never a suite or class filter.' }
        if ($taskTest.project -notin @('tests/CoCern.Tests/CoCern.Tests.csproj','tests/CoCern.StandingIdentity.Tests/CoCern.StandingIdentity.Tests.csproj')) { throw 'Unknown test project.' }
        if (!$taskTest.evidence -or !@($taskTest.covers).Count -or @($taskTest.covers | Where-Object { $_ -notin $taskSymbols }).Count) { throw 'Each test needs its Roslyn evidence and changed symbols it covers.' }
    }
    $taskSelectionHash=(Get-FileHash -LiteralPath $Selection -Algorithm SHA256).Hash
    $taskOutput=Join-Path $taskRoot ('artifacts/smoke/'+$taskHead.Substring(0,12)+'-'+$taskSelectionHash.Substring(0,12))
    if(Test-Path -LiteralPath $taskOutput){throw 'This exact source and selection already have a verification receipt; do not rerun unchanged work.'}
    New-Item -ItemType Directory -Path $taskOutput | Out-Null
    $taskBuild=Join-Path $taskOutput 'build'
    $taskDeadline=[DateTimeOffset]::UtcNow.AddMinutes($TimeoutMinutes)
    $taskReceipt=[ordered]@{Commit=$taskHead;ChangedSymbols=$taskSymbols;Tests=$taskTests;StartedUtc=[DateTimeOffset]::UtcNow.ToString('O');Outcome='incomplete'}
    $taskReceipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskOutput 'receipt.json')
    function Invoke-BoundedDotnet([string]$Name,[string[]]$Arguments) {
        $taskStart=[Diagnostics.ProcessStartInfo]::new((Get-Command dotnet).Source)
        $taskStart.WorkingDirectory=$taskRoot
        $taskStart.UseShellExecute=$false
        $taskStart.CreateNoWindow=$true
        $taskStart.RedirectStandardOutput=$true
        $taskStart.RedirectStandardError=$true
        foreach($taskArgument in $Arguments){$taskStart.ArgumentList.Add($taskArgument)}
        $taskProcess=[Diagnostics.Process]::new()
        $taskProcess.StartInfo=$taskStart
        try {
            [void]$taskProcess.Start()
            $taskStdout=$taskProcess.StandardOutput.ReadToEndAsync()
            $taskStderr=$taskProcess.StandardError.ReadToEndAsync()
            $taskRemaining=[Math]::Max(1,[Math]::Min([int]::MaxValue,($taskDeadline-[DateTimeOffset]::UtcNow).TotalMilliseconds))
            $taskTimedOut=!$taskProcess.WaitForExit([int]$taskRemaining)
            if($taskTimedOut){$taskProcess.Kill($true);$taskProcess.WaitForExit()}
            [IO.File]::WriteAllText((Join-Path $taskOutput ($Name+'.stdout.log')),$taskStdout.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $taskOutput ($Name+'.stderr.log')),$taskStderr.GetAwaiter().GetResult())
            if($taskTimedOut -or $taskProcess.ExitCode -ne 0){throw ($Name+' failed or timed out; inspect the retained logs.')}
        } finally { $taskProcess.Dispose() }
    }
    try {
        Invoke-BoundedDotnet 'build' @('build','CoCern.slnx','--artifacts-path',$taskBuild,'-m:1','-p:BuildInParallel=false','--disable-build-servers','--nologo')
        $taskIndex=0
        foreach($taskGroup in ($taskTests | Group-Object project)){
            $taskFilter=($taskGroup.Group.method | Sort-Object -Unique | ForEach-Object {'FullyQualifiedName='+$_}) -join '|'
            Invoke-BoundedDotnet ('smoke-'+(++$taskIndex)) @('test',$taskGroup.Name,'--no-build','--no-restore','--artifacts-path',$taskBuild,'--filter',$taskFilter,'--logger','trx','--results-directory',(Join-Path $taskOutput 'results'),'-m:1','--disable-build-servers','--nologo')
        }
        $taskReceipt.Outcome='passed'
    } finally {
        $taskReceipt['FinishedUtc']=[DateTimeOffset]::UtcNow.ToString('O')
        $taskReceipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskOutput 'receipt.json')
    }
    Write-Output ('Build and '+$taskTests.Count+' selected smoke methods passed. Receipt: '+(Join-Path $taskOutput 'receipt.json'))
    if ($Publish) {
        Invoke-BoundedDotnet 'publish-host' @('publish','src/CoCern.Host/CoCern.Host.csproj','-c','Release','-r','win-x64','--self-contained','false','-o','artifacts/host','--nologo')
        Invoke-BoundedDotnet 'publish-adapter' @('publish','src/CoCern.Adapter/CoCern.Adapter.csproj','-c','Release','-r','win-x64','--self-contained','true','-o','artifacts/adapter','--nologo')
    }
} finally { Pop-Location }
