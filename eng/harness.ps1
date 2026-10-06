[CmdletBinding()]
param(
    [ValidateSet('Doctor', 'Fast', 'Full', 'Live', 'Ui')]
    [string]$Profile = 'Fast',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$ArtifactsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ArtifactsDirectory = Join-Path $repoRoot "artifacts\harness\$stamp-$($Profile.ToLowerInvariant())"
}

$ArtifactsDirectory = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
New-Item -ItemType Directory -Path $ArtifactsDirectory -Force | Out-Null

$results = [System.Collections.Generic.List[object]]::new()
$failed = $false
$effectiveConfiguration = $Configuration

function Invoke-HarnessCommand {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Command,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    $safeName = $Name -replace '[^a-zA-Z0-9_.-]', '-'
    $logPath = Join-Path $ArtifactsDirectory "$safeName.log"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()

    Write-Host "`n==> $Name" -ForegroundColor Cyan
    Write-Host "    $Command $($Arguments -join ' ')"

    & $Command @Arguments 2>&1 | Tee-Object -FilePath $logPath
    $exitCode = $LASTEXITCODE
    $watch.Stop()

    $status = if ($exitCode -eq 0) { 'passed' } else { 'failed' }
    $results.Add([pscustomobject]@{
        name = $Name
        status = $status
        exitCode = $exitCode
        durationMs = $watch.ElapsedMilliseconds
        log = $logPath
    })

    if ($exitCode -ne 0) {
        $script:failed = $true
        Write-Host "FAILED: $Name (exit $exitCode)" -ForegroundColor Red
        return $false
    }

    Write-Host "PASSED: $Name" -ForegroundColor Green
    return $true
}

function Write-DoctorInfo {
    $doctorPath = Join-Path $ArtifactsDirectory 'doctor.log'
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("Repository: $repoRoot")
    $lines.Add("Profile: $Profile")
    $lines.Add("OS: $([System.Environment]::OSVersion)")
    $lines.Add("PowerShell: $($PSVersionTable.PSVersion)")
    $lines.Add("Timestamp: $([DateTimeOffset]::Now.ToString('O'))")
    $lines.Add('')
    $lines.Add('dotnet --version:')
    $lines.Add((& dotnet --version 2>&1 | Out-String).TrimEnd())
    $lines.Add('')
    $lines.Add('git status --short:')
    $gitStatus = (& git -C $repoRoot status --short 2>&1 | Out-String).TrimEnd()
    $lines.Add($(if ($gitStatus) { $gitStatus } else { '(clean)' }))
    $lines | Set-Content -LiteralPath $doctorPath -Encoding utf8
    $lines | ForEach-Object { Write-Host $_ }
    $results.Add([pscustomobject]@{
        name = 'doctor'
        status = 'passed'
        exitCode = 0
        durationMs = 0
        log = $doctorPath
    })
}

function Write-UiChecklist {
    $path = Join-Path $ArtifactsDirectory 'ui-checklist.md'
    @"
# TaskbarLyrics UI / Windows manual evidence

- [ ] Overlay is click-through, does not activate, and does not appear in Alt+Tab/taskbar.
- [ ] Play, pause, resume, forward seek, backward seek, and continuous scrub stay synchronized.
- [ ] QRC word highlight, LRC line progress, long-line auto-pan, and interlude hold look correct.
- [ ] Settings live preview and save/reload agree for Dark, Light, and FollowSystem.
- [ ] Keyboard focus, accessible control names, reduced motion, and real Windows high-contrast mode work.
- [ ] Overview actions, visual color editing, additive offsets, stale-track protection, and rematch retry work.
- [ ] Font dropdown scrollbar keeps both ends rounded at the top, middle, and bottom positions; neither end has extra space or crosses the popup edge.
- [ ] 100%, 125%, 150%, and 200% DPI keep the overlay inside the intended taskbar free region.
- [ ] Explorer/taskbar restart and display/DPI changes trigger correct repositioning.
- [ ] Full-screen app suppresses the overlay and leaving full-screen restores it.
- [ ] Tray show/hide, settings, rematch, reload, cache folder, and exit work.
- [ ] A second instance activates settings instead of starting another process.
- [ ] Logs contain no unhandled exception; config/cache/manual binding remain intact.

Automated evidence is in presentation-offline.log and screenshots/. Render scales simulate pixel density; they do NOT replace physical per-monitor DPI movement. shell-check.txt records the opt-in real overlay style/show-hide check.

Unchecked entries remain manual follow-up, not implicitly passed by this profile.

Record machine, Windows build, displays/DPI, QQ Music version, screenshots, and log path below.
"@ | Set-Content -LiteralPath $path -Encoding utf8
    Write-Host "UI checklist: $path" -ForegroundColor Yellow
}

Push-Location $repoRoot
try {
    Write-DoctorInfo

    if ($Profile -ne 'Doctor') {
        $effectiveConfiguration = if ($Profile -in @('Full', 'Live', 'Ui')) { 'Release' } else { $Configuration }

        if (Invoke-HarnessCommand 'restore-solution' 'dotnet' @('restore', 'TaskbarLyrics.slnx')) {
            Invoke-HarnessCommand 'build-solution' 'dotnet' @(
                'build', 'TaskbarLyrics.slnx', '--no-restore', '--configuration', $effectiveConfiguration
            ) | Out-Null
        }

        if (-not $failed) {
            Invoke-HarnessCommand 'timeline-clock-offline' 'dotnet' @(
                'run', '--project', 'tools/TimelineClockSmoke/TimelineClockSmoke.csproj',
                '--configuration', $effectiveConfiguration
            ) | Out-Null
        }

        if (-not $failed) {
            Invoke-HarnessCommand 'interlude-hold-offline' 'dotnet' @(
                'run', '--project', 'tools/InterludeHoldSmoke/InterludeHoldSmoke.csproj',
                '--configuration', $effectiveConfiguration
            ) | Out-Null
        }

        if (-not $failed) {
            Invoke-HarnessCommand 'track-match-workflow-offline' 'dotnet' @(
                'run', '--project', 'tools/TrackMatchWorkflowSmoke/TrackMatchWorkflowSmoke.csproj',
                '--configuration', $effectiveConfiguration
            ) | Out-Null
        }

        if (-not $failed) {
            Invoke-HarnessCommand 'scrollbar-theme-offline' 'pwsh' @(
                '-NoProfile', '-File', 'tools/ScrollbarThemeSmoke/ScrollbarThemeSmoke.ps1'
            ) | Out-Null
        }

        if (-not $failed) {
            $presentationArgs = @('run', '--project', 'tools/PresentationSmoke/PresentationSmoke.csproj', '--configuration', $effectiveConfiguration)
            if ($Profile -eq 'Ui') {
                $presentationArgs += @('--', '--screenshots', (Join-Path $ArtifactsDirectory 'screenshots'), '--shell-check')
            }
            Invoke-HarnessCommand 'presentation-offline' 'dotnet' $presentationArgs | Out-Null
        }

        if (-not $failed -and $Profile -eq 'Live') {
            Invoke-HarnessCommand 'qqmusic-live-contract' 'dotnet' @(
                'run', '--project', 'tools/MatchSmoke/MatchSmoke.csproj',
                '--configuration', $effectiveConfiguration
            ) | Out-Null
        }

        if ($Profile -eq 'Ui') {
            Write-UiChecklist
        }
    }
}
finally {
    Pop-Location
    $summary = [pscustomobject]@{
        schemaVersion = 1
        profile = $Profile
        configuration = $effectiveConfiguration
        startedForRepository = $repoRoot
        completedAt = [DateTimeOffset]::Now.ToString('O')
        success = -not $failed
        results = $results
    }
    $summaryPath = Join-Path $ArtifactsDirectory 'summary.json'
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host "`nHarness summary: $summaryPath"
}

if ($failed) { exit 1 }
exit 0
