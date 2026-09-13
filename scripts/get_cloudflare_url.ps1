param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LogPath)) {
    exit 1
}

$match = Select-String -Path $LogPath -Pattern 'https://[-a-zA-Z0-9]+\.trycloudflare\.com' -ErrorAction SilentlyContinue |
    Select-Object -Last 1

if ($match -and $match.Matches.Count -gt 0) {
    Write-Output $match.Matches[0].Value
    exit 0
}

exit 1
