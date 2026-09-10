[CmdletBinding()]
param(
    [string]$InstallRoot,
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Split-Path -Parent $PSScriptRoot
}
$installPath = [IO.Path]::GetFullPath($InstallRoot)
$registryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.chatgpt_assistant.project_bridge'
$legacyRegistryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.asemanyadak.project_bridge'
$shortcuts = @(
    (Join-Path $installPath 'دستیار چت جی پی تی.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\دستیار چت جی پی تی.lnk')
)

foreach ($path in @($registryPath, $legacyRegistryPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}
foreach ($shortcut in $shortcuts) {
    if (Test-Path -LiteralPath $shortcut) {
        Remove-Item -LiteralPath $shortcut -Force
    }
}

if ($RemoveFiles) {
    Write-Warning 'Automatic folder deletion is disabled. Remove the installation folder manually after keeping any project data you need.'
}

Write-Host 'ChatGPT assistant bridge registration and shortcuts were removed.'
