[CmdletBinding()]
param([string]$Executable)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Executable) { $Executable = Join-Path $repo 'publish/Release/Native/TaskbarLyrics.Native.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
if (Get-Process TaskbarLyrics.Native -ErrorAction SilentlyContinue) {
    throw 'Exit the running native application before the isolated window lifecycle test.'
}
$started = Get-Date
$process = Start-Process -FilePath $Executable -ArgumentList '--smoke' -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(45000)) { throw 'Native window lifecycle test timed out.' }
if ($process.ExitCode -ne 0) { throw "Native window lifecycle test exited with $($process.ExitCode)." }
$reportPath = Join-Path (Split-Path $Executable -Parent) 'smoke-summary.json'
if ((Get-Item -LiteralPath $reportPath).LastWriteTime -lt $started) { throw 'Fresh smoke report was not written.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
foreach ($phase in @('initial', 'reopenCached', 'reopenReleased')) {
    $check = $report.uiChecks.$phase
    if (-not $check.rendered -or -not $check.controllerVisible) {
        throw "UI did not render visibly during $phase. Report: $reportPath"
    }
    if ($check.nativeCaption -ne $false -or $check.resizable -ne $true) {
        throw "Duplicate native caption or missing resize frame during $phase. Report: $reportPath"
    }
}
if (-not $report.webReady) { throw 'React bridge is not ready after recreation.' }
Write-Host "PASS: initial render, cached reopen, released WebView recreation; custom caption and resize frame. Evidence: $reportPath"
