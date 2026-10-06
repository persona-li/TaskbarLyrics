[CmdletBinding()]
param([string]$IsccPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $IsccPath) {
    $candidates = @((Join-Path $repo 'artifacts/packaging-tools/InnoSetup/ISCC.exe'),
        "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe")
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $IsccPath) { throw 'Install Inno Setup 6 or pass -IsccPath.' }
$project = Join-Path $repo 'src/TaskbarLyrics.App/TaskbarLyrics.App.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stage = Join-Path $repo ('artifacts/distribution-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$binary = Join-Path $stage 'app'
$portable = Join-Path $stage 'portable'
$output = Join-Path $repo 'publish'
dotnet publish $project -c Release -p:PublishProfile=Distribution -p:DebugType=none -p:DebugSymbols=false -o $binary
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
New-Item -ItemType Directory $portable -Force | Out-Null
Get-ChildItem -LiteralPath $binary | Copy-Item -Destination $portable -Recurse
Set-Content (Join-Path $portable 'portable.flag') 'TaskbarLyrics portable mode' -Encoding utf8
Set-Content (Join-Path $portable '使用说明.txt') '运行 TaskbarLyrics.App.exe。配置、缓存和日志保存在旁边的 Data 文件夹。请放在有写入权限的位置。更新时保留 Data；删除前退出程序，如已开启开机启动请先关闭。' -Encoding utf8
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath (Join-Path $output "TaskbarLyrics-v$version-win-x64-portable.zip") -Force
& $IsccPath "/DSourceDir=$binary" "/DOutputDir=$output" "/DAppVersion=$version" (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
Set-Content (Join-Path $repo 'artifacts/latest-distribution.txt') $binary -Encoding utf8
Write-Host "Packages built in $output. Binary staging path: $binary"
# Deployment to publish/Release is separate: back up and stop the running copy first.
