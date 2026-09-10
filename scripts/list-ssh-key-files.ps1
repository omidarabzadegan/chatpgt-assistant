$ErrorActionPreference = 'Stop'

$roots = @(
    (Join-Path $HOME '.ssh'),
    (Join-Path $HOME 'Desktop'),
    (Join-Path $HOME 'Downloads'),
    (Join-Path $HOME 'Documents')
)

$patterns = @('id_*', '*.pem', '*.ppk')

foreach ($root in $roots) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        continue
    }

    foreach ($pattern in $patterns) {
        Get-ChildItem -LiteralPath $root -Filter $pattern -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Extension -ne '.pub' -and
                $_.Name -notmatch 'known_hosts'
            } |
            Select-Object @{
                Name = 'Path'
                Expression = { $_.FullName }
            }, Length, LastWriteTimeUtc
    }
}
