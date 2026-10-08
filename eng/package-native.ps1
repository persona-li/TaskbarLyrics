param([string]$IsccPath)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$cmake=Get-Content -LiteralPath (Join-Path $repo 'native/CMakeLists.txt') -Raw
if($cmake -notmatch 'project\(TaskbarLyricsNative VERSION (\d+\.\d+\.\d+) '){throw 'Unable to read the application version from CMakeLists.txt'}
$version=$Matches[1]
$source=Join-Path $repo 'publish/Release/Native'
if(-not(Test-Path (Join-Path $source 'TaskbarLyrics.Native.exe'))){throw 'Run eng/build-native.ps1 first'}
$stage=Join-Path $repo ('artifacts/native-distribution-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$app=Join-Path $stage 'app'
New-Item -ItemType Directory -Force $app | Out-Null
# Only known application payloads may enter a public package. Never copy the
# runtime directory wholesale: it can contain user data and diagnostic files.
foreach ($name in @('TaskbarLyrics.Native.exe','README.md','ui','licenses')) {
    $entry = Join-Path $source $name
    if (-not (Test-Path -LiteralPath $entry)) { throw "Required application file missing: $name" }
    Copy-Item -LiteralPath $entry -Destination $app -Recurse
}
$output=Join-Path $repo 'publish/Release'
$portable=Join-Path $stage 'portable'
Copy-Item -LiteralPath $app -Destination $portable -Recurse
Set-Content -LiteralPath (Join-Path $portable 'portable.flag') -Value 'TaskbarLyrics portable mode' -Encoding utf8
Set-Content -LiteralPath (Join-Path $portable '使用说明.txt') -Value '运行 TaskbarLyrics.Native.exe。保留 ui 目录和 portable.flag。设置、歌词缓存和日志保存在 Data。主界面需要 Microsoft Edge WebView2 Runtime，不需要 .NET。更新时保留 Data。' -Encoding utf8
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath (Join-Path $output "TaskbarLyrics-Native-v$version-win-x64-portable.zip") -Force
if(-not $IsccPath){$IsccPath=@((Join-Path $repo 'artifacts/packaging-tools/InnoSetup/ISCC.exe'),"${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe")|Where-Object{Test-Path -LiteralPath $_}|Select-Object -First 1}
if(-not $IsccPath){throw 'Portable archive built; Inno Setup is required to build the installer'}
& $IsccPath "/DSourceDir=$app" "/DOutputDir=$output" "/DAppVersion=$version" (Join-Path $PSScriptRoot 'installer-native.iss')
if($LASTEXITCODE){throw 'Native installer compilation failed'}
Get-ChildItem -LiteralPath $output -Filter "TaskbarLyrics-Native-v$version*" | Select-Object Name,Length
