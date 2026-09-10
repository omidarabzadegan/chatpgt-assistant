[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$base = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$attribution = [IO.File]::ReadAllText((Join-Path $base 'extension/attribution.js')).Replace("`r`n", "`n")
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $actual = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($attribution))).Replace('-', '').ToLowerInvariant()
} finally { $sha.Dispose() }
$expected = 'c1a736c6e975755520066c46213b53859981dd64d2fdc5f15454c8a2d71ef11e'
if ($actual -ne $expected) {
    throw 'Original attribution changed. Preserve the required credit; see LICENSE and AGENTS.md.'
}
$popup = [IO.File]::ReadAllText((Join-Path $base 'extension/popup.html'))
if ($popup -notmatch 'id="developerCredit"' -or $popup -notmatch '<script src="attribution.js"></script>') {
    throw 'Required developer credit or attribution script is missing from the popup.'
}
foreach ($name in @('LICENSE', 'AGENTS.md')) {
    $notice = [IO.File]::ReadAllText((Join-Path $base $name))
    if ($notice -notmatch '09128848707' -or $notice -notmatch 'Omid Arabzadegan') {
        throw "Required author notice is missing: $name"
    }
}
if (Test-Path -LiteralPath (Join-Path $base '.git')) {
    $tracked = & git -C $base ls-files
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect tracked files.' }
    $private = @($tracked | Where-Object {
        $_ -match '(^|/)(\.ssh|\.env[^/]*|id_rsa|id_ed25519|known_hosts|authorized_keys)(/|$)' -or
        $_ -match '^(native/config\.json|native/generated/|native/data/|data/|backup_workspace_|graphify-out/|release/|tests/(fixture/|test-data/|test-config\.generated\.json))' -or
        $_ -match '\.(pem|key|pfx|p12)$'
    })
    if ($private.Count -gt 0) {
        throw ('Private runtime files are tracked; do not publish: ' + ($private -join ', '))
    }
}
Write-Host 'Release checks passed: author credit preserved; private runtime files excluded.'
