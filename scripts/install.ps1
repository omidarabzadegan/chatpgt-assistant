[CmdletBinding()]
param(
    [string]$InstallRoot,
    [string]$ChromeProfile = 'Default',
    [switch]$SkipLaunch
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Split-Path -Parent $PSScriptRoot
}
$installPath = [IO.Path]::GetFullPath($InstallRoot)
$sourcePath = Join-Path $installPath 'native\ProjectBridge.cs'
$exePath = Join-Path $installPath 'native\ProjectBridge.agent.exe'
$stagedExePath = Join-Path $installPath 'native\ProjectBridge.agent.next.exe'
$hostManifest = Join-Path $installPath 'native\com.chatgpt_assistant.project_bridge.json'
$legacyHostManifest = Join-Path $installPath 'native\com.asemanyadak.project_bridge.json'
$extensionPath = Join-Path $installPath 'extension'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$registryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.chatgpt_assistant.project_bridge'
$legacyRegistryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.asemanyadak.project_bridge'

foreach ($required in @($sourcePath, $hostManifest, $legacyHostManifest, (Join-Path $extensionPath 'manifest.json'), $compiler, $chrome)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required installation file is missing: $required"
    }
}

Remove-Item -LiteralPath $stagedExePath -Force -ErrorAction SilentlyContinue
& $compiler /nologo /optimize+ /target:exe /out:$stagedExePath /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll $sourcePath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $stagedExePath)) {
    throw 'ProjectBridge.exe compilation failed.'
}

try {
    Copy-Item -LiteralPath $stagedExePath -Destination $exePath -Force -ErrorAction Stop
    Remove-Item -LiteralPath $stagedExePath -Force
    Write-Host 'Native Host updated.' -ForegroundColor Green
} catch [System.IO.IOException] {
    Write-Warning 'Native Host is currently running. The new build was saved as ProjectBridge.agent.next.exe. Close/reload Chrome or stop the current Native Host, then run install.ps1 again.'
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw 'ProjectBridge.agent.exe is missing.'
}

$null = Get-Content -Raw -Encoding utf8 $hostManifest | ConvertFrom-Json
$null = Get-Content -Raw -Encoding utf8 $legacyHostManifest | ConvertFrom-Json
$null = Get-Content -Raw -Encoding utf8 (Join-Path $extensionPath 'manifest.json') | ConvertFrom-Json

$generatedDirectory = Join-Path $installPath 'native\generated'
New-Item -ItemType Directory -Path $generatedDirectory -Force | Out-Null
$configPath = Join-Path $installPath 'native\config.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    $emptyProject = Join-Path $installPath 'data\starter-project'
    New-Item -ItemType Directory -Path $emptyProject -Force | Out-Null
    @{ projectRoot = $emptyProject; dataDirectory = (Join-Path $installPath 'data'); executionEnabled = $false;
       servers = @(); tests = @(); allowedLocalCommands = @(); allowedLocalScripts = @() } |
       ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $configPath -Encoding UTF8
}
foreach ($manifestPath in @($hostManifest, $legacyHostManifest)) {
    $manifestModel = Get-Content -Raw -Encoding utf8 $manifestPath | ConvertFrom-Json
    $manifestModel.path = $exePath
    $manifestModel | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $generatedDirectory (Split-Path -Leaf $manifestPath)) -Encoding UTF8
}

foreach ($registration in @(
    @{ RegistryPath = $registryPath; ManifestPath = (Join-Path $generatedDirectory (Split-Path -Leaf $hostManifest)) },
    @{ RegistryPath = $legacyRegistryPath; ManifestPath = (Join-Path $generatedDirectory (Split-Path -Leaf $legacyHostManifest)) }
)) {
    New-Item -Path $registration.RegistryPath -Force | Out-Null
    Set-Item -Path $registration.RegistryPath -Value $registration.ManifestPath
}

$shell = New-Object -ComObject WScript.Shell
$shortcutTargets = @(
    (Join-Path $installPath 'دستیار چت جی پی تی.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\دستیار چت جی پی تی.lnk')
)
foreach ($shortcutPath in $shortcutTargets) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $chrome
    $shortcut.Arguments = "--profile-directory=`"$ChromeProfile`" --load-extension=`"$extensionPath`" https://chatgpt.com/"
    $shortcut.WorkingDirectory = Split-Path -Parent $chrome
    $shortcut.Description = 'دستیار چت جی پی تی'
    $shortcut.Save()
}

Write-Host "ChatGPT assistant bridge compiled: $exePath"
Write-Host "Native host registered: $registryPath"
Write-Host "Legacy native host registered: $legacyRegistryPath"
Write-Host "Extension ID: migkgobbooelepdmdncelhipfndpbhda"
Write-Host "Chrome profile: $ChromeProfile"

if (-not $SkipLaunch) {
    Start-Process -FilePath $chrome -ArgumentList @("--profile-directory=$ChromeProfile", "--load-extension=$extensionPath", 'https://chatgpt.com/')
}
