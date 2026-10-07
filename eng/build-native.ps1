param([switch]$Restore,[switch]$SkipUi,[switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$deps = Join-Path $repo 'artifacts/native-deps'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Install Visual Studio Desktop development with C++ and Windows SDK first.' }
$cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
$ctest = Join-Path (Split-Path $cmake) 'ctest.exe'
if ($Restore) {
    New-Item -ItemType Directory -Force (Join-Path $deps 'json/nlohmann') | Out-Null
    Invoke-WebRequest 'https://raw.githubusercontent.com/nlohmann/json/v3.12.0/single_include/nlohmann/json.hpp' -OutFile (Join-Path $deps 'json/nlohmann/json.hpp')
    Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.4258.31/microsoft.web.webview2.1.0.4258.31.nupkg' -OutFile (Join-Path $deps 'webview.zip')
    Expand-Archive (Join-Path $deps 'webview.zip') (Join-Path $deps 'webview') -Force
}
if (-not (Test-Path (Join-Path $deps 'webview/build/native/include/WebView2.h'))) { throw 'Run this script with -Restore to restore native dependencies.' }
if (-not $SkipUi) {
    Push-Location (Join-Path $repo 'native/ui')
    try {
        if (-not (Test-Path node_modules)) { & npm.cmd ci --no-audit --no-fund; if ($LASTEXITCODE) { throw 'npm ci failed' } }
        & npm.cmd run build; if ($LASTEXITCODE) { throw 'React build failed' }
        if(-not $SkipTests){ & node --experimental-strip-types --test tests/typography.test.mjs; if($LASTEXITCODE){throw 'React typography and lyric timing checks failed'} }
    } finally { Pop-Location }
}
$build = Join-Path $repo 'artifacts/native-build'
& $cmake -S (Join-Path $repo 'native') -B $build -A x64
if ($LASTEXITCODE) { throw 'CMake configure failed' }
& $cmake --build $build --config Release --parallel 4
if ($LASTEXITCODE) { throw 'C++ build failed' }
if (-not $SkipTests) { & $ctest --test-dir $build -C Release --output-on-failure; if ($LASTEXITCODE) { throw 'Native invariant checks failed' } }
$output = Join-Path $repo 'publish/Release/Native'
if (Test-Path (Join-Path $output 'TaskbarLyrics.Native.exe')) {
    $old = Join-Path $repo ('artifacts/native-backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force $old | Out-Null
    Copy-Item (Join-Path $output 'TaskbarLyrics.Native.exe') $old
    if (Test-Path (Join-Path $output 'ui')) { Copy-Item (Join-Path $output 'ui') $old -Recurse }
}
New-Item -ItemType Directory -Force $output | Out-Null
New-Item -ItemType File -Force (Join-Path $output 'portable.flag') | Out-Null
Copy-Item (Join-Path $build 'Release/TaskbarLyrics.Native.exe') $output -Force
$uiOutput = Join-Path $output 'ui'
if (Test-Path $uiOutput) {
    $resolvedUi = (Resolve-Path -LiteralPath $uiOutput).Path
    $resolvedOutput = (Resolve-Path -LiteralPath $output).Path
    if (-not $resolvedUi.StartsWith($resolvedOutput + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'UI output path escapes Native output directory' }
    Remove-Item -LiteralPath $resolvedUi -Recurse -Force
}
Copy-Item (Join-Path $repo 'native/ui/dist') $uiOutput -Recurse
Copy-Item (Join-Path $repo 'native/README.md') (Join-Path $output 'README.md') -Force
Copy-Item (Join-Path $repo 'native/licenses') $output -Recurse -Force
Get-ChildItem $output -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/]Data[\\/]' } | Measure-Object Length -Sum | Select-Object Count,@{n='ProgramMiB';e={[math]::Round($_.Sum/1MB,2)}}
Write-Output (Join-Path $output 'TaskbarLyrics.Native.exe')
