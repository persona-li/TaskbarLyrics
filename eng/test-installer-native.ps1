[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceDir, [Parameter(Mandatory)][string]$IsccPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repo ('artifacts/installer-test-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $root 'installed'
$data = Join-Path $root 'data'
New-Item -ItemType Directory $root,$data -Force | Out-Null
# A distinct AppId, shortcut name, Run value, and data root ensure no real user
# configuration or production uninstall registration is touched by this test.
& $IsccPath '/DPackagingTest' "/DTestDataDir=$data" "/DSourceDir=$SourceDir" "/DOutputDir=$root" (Join-Path $PSScriptRoot 'installer-native.iss') *> (Join-Path $root 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Test installer compilation failed: $root" }
$setup = Join-Path $root 'TaskbarLyrics-Native-v1.0.0-win-x64-setup.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = 'TaskbarLyrics.PackagingTest'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskbarLyrics.PackagingTest_is1'
if (Test-Path $uninstallKey) { throw 'A previous isolated test installation must be removed first.' }
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function RunInstaller($exe, $arguments) {
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    Assert ($p.ExitCode -eq 0) "Installer returned $($p.ExitCode)"
}
$installArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LANG=chinesesimplified /DIR=`"$app`" /TASKS=`"desktopicon`""
$sentinel = Join-Path $data 'config.json'
Set-Content $sentinel 'preserve me'
New-ItemProperty -Path $runKey -Name $valueName -Value '"C:\old-test\TaskbarLyrics.Native.exe"' -PropertyType String -Force | Out-Null
RunInstaller $setup $installArgs
Assert (-not (Test-Path (Join-Path $app 'coreclr.dll'))) '.NET runtime unexpectedly bundled'
Assert (Test-Path (Join-Path $app 'ui/index.html')) 'React UI missing'
Assert (-not (Test-Path (Join-Path $app 'portable.flag'))) 'Installed copy accidentally portable'
Assert (Test-Path $uninstallKey) 'Windows uninstall entry missing'
Assert ((Get-ItemProperty $runKey).$valueName -eq "`"$app\TaskbarLyrics.Native.exe`" --background") 'Startup entry not updated'
$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'TaskbarLyrics Packaging Test.lnk'
Assert (Test-Path $shortcut) 'Optional desktop shortcut missing'
RunInstaller $setup $installArgs
Assert ((Get-Content $sentinel -Raw).Trim() -eq 'preserve me') 'Upgrade changed user data'
RunInstaller (Join-Path $app 'unins000.exe') '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
Assert (Test-Path $sentinel) 'Default uninstall removed data'
Assert (-not (Test-Path $uninstallKey)) 'Uninstall entry remains'
Assert (-not (Test-Path $shortcut)) 'Shortcut remains'
Assert (-not (Get-ItemProperty $runKey -Name $valueName -ErrorAction SilentlyContinue)) 'Startup entry remains'
RunInstaller $setup $installArgs
RunInstaller (Join-Path $app 'unins000.exe') '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PURGEDATA=1'
Assert (-not (Test-Path $data)) 'Explicit purge did not remove isolated test data'
Assert (-not (Test-Path (Join-Path $app 'TaskbarLyrics.Native.exe'))) 'Executable remains after uninstall'
Write-Host "PASS: install, upgrade, startup relocation, shortcuts, preserve-data uninstall, purge-data uninstall. Evidence: $root"
