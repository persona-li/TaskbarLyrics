[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo 'publish/Release'
$stage = Join-Path $output ('PublicSource-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$files = [Collections.Generic.List[string]]::new()
foreach ($name in @('README.md','native/CMakeLists.txt','native/app.manifest','native/README.md',
    'native/ui/package.json','native/ui/package-lock.json','native/ui/tsconfig.json','native/ui/index.html',
    'eng/build-native.ps1','eng/package-native.ps1','eng/installer-native.iss',
    'eng/test-installer-native.ps1','eng/test-native-window.ps1','eng/export-public.ps1',
    'eng/ChineseSimplified.isl','eng/THIRD_PARTY.md')) { $files.Add($name) }
$groups = @{
    'native/src' = @('.cpp','.hpp','.rc')
    'native/assets' = @('.ico')
    'native/licenses' = @('.txt')
    'native/tests' = @('.cpp')
    'native/tests/fixtures' = @('.json','.md')
    'native/ui/src' = @('.tsx','.ts','.css','.svg')
    'native/ui/tests' = @('.mjs')
    'docs/images' = @('.png')
}
foreach ($directory in $groups.Keys) {
    Get-ChildItem -LiteralPath (Join-Path $repo $directory) -File |
        Where-Object Extension -In $groups[$directory] |
        ForEach-Object { $files.Add($directory + '/' + $_.Name) }
}
foreach ($name in $files) {
    $source = Join-Path $repo $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing source file: $name" }
    $target = Join-Path $stage $name
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
@'
# Generated files and dependencies
artifacts/
publish/
node_modules/
dist/
build/
bin/
obj/
*.tsbuildinfo
*.exe
*.dll
*.pdb
*.nupkg
# Personal runtime data, development context and credentials
Data/
Logs/
Cache/
WebView2/
.vs/
.idea/
*.user
*.log
*.tmp
*.local.json
.env
.env.*
*.pfx
*.p12
*.key
AGENTS.md
PROMOT.md
codex-clipboard-*
Thumbs.db
.DS_Store
'@ | Set-Content -LiteralPath (Join-Path $stage '.gitignore') -Encoding utf8
# Fail before creating a public archive if first-party source contains a local
# profile path or a credential-like assignment. Third-party license attributions
# and synthetic test inputs are intentionally retained.
$issues = @()
Get-ChildItem -LiteralPath $stage -Recurse -File |
    Where-Object Extension -In @('.cpp','.hpp','.rc','.tsx','.ts','.css','.json','.ps1','.md','.iss') |
    ForEach-Object {
        $content = Get-Content -LiteralPath $_.FullName -Raw
        if ($content -match '(?i)[A-Z]:[\\/]Users[\\/][^\\/\s]+' -or
            $content -match '(?i)(gh[pousr]_[a-zA-Z0-9]{30,}|github_pat_[a-zA-Z0-9_]{30,}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----)') {
            $issues += [IO.Path]::GetRelativePath($stage, $_.FullName)
        }
    }
if ($issues.Count) { throw ('Sensitive source patterns found in: ' + ($issues -join ', ')) }
$archive = Join-Path $output 'TaskbarLyrics-v1.0.0-source.zip'
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive)
Write-Output $stage
Write-Output $archive
