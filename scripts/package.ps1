[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$base = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
& (Join-Path $PSScriptRoot 'verify-release.ps1')
$release = Join-Path $base 'release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$staging = Join-Path $release ('source-' + [Guid]::NewGuid().ToString('N') + '\chatgpt-assistant')
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$files = @('README.md', '.gitignore', 'LICENSE', 'AGENTS.md', 'native/ProjectBridge.cs',
    'native/com.chatgpt_assistant.project_bridge.json', 'native/com.asemanyadak.project_bridge.json',
    'scripts/install.ps1', 'scripts/uninstall.ps1', 'scripts/test.ps1', 'scripts/package.ps1',
    'scripts/list-ssh-key-files.ps1', 'scripts/verify-release.ps1',
    'tests/background.test.cjs', 'tests/popup.test.cjs', 'tests/attribution.test.cjs')
$files += Get-ChildItem -LiteralPath (Join-Path $base 'extension') -File |
    Where-Object { $_.Extension -in @('.js', '.css', '.html', '.json') } |
    ForEach-Object { 'extension/' + $_.Name }
$files += Get-ChildItem -LiteralPath (Join-Path $base 'extension/icons') -File -Filter '*.png' |
    ForEach-Object { 'extension/icons/' + $_.Name }
foreach ($relative in $files) {
    $destination = Join-Path $staging $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $base $relative) -Destination $destination
}
$archive = Join-Path $release 'chatgpt-assistant-1.7.0.zip'
Compress-Archive -LiteralPath $staging -DestinationPath $archive -Force
Write-Host "Shareable archive: $archive"
Write-Host "Clean source: $staging"
