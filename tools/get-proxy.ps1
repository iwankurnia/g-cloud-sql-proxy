# Downloads Google's cloud-sql-proxy for every target into vendor\<rid>\ so the
# build can bundle the right one into each package. Each file comes from
# Google's own release bucket and is checked against the SHA-256 published in
# the GitHub release notes; a mismatch is deleted, never kept.
#
#   .\tools\get-proxy.ps1                       # the pinned version below, all targets
#   .\tools\get-proxy.ps1 -Version v2.29.0
#   .\tools\get-proxy.ps1 -Targets linux-x64    # one target

param(
    [string]$Version = 'v2.28.0',
    [string[]]$Targets = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-arm64', 'osx-x64')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# .NET runtime identifier -> Google's file name
$files = @{
    'win-x64'     = 'cloud-sql-proxy.x64.exe'
    'linux-x64'   = 'cloud-sql-proxy.linux.amd64'
    'linux-arm64' = 'cloud-sql-proxy.linux.arm64'
    'osx-arm64'   = 'cloud-sql-proxy.darwin.arm64'
    'osx-x64'     = 'cloud-sql-proxy.darwin.amd64'
}

$release = Invoke-RestMethod -Headers @{ 'User-Agent' = 'release-check' } `
    -Uri "https://api.github.com/repos/GoogleCloudPlatform/cloud-sql-proxy/releases/tags/$Version"

foreach ($rid in $Targets) {
    $file = $files[$rid]
    if (-not $file) { throw "Unknown target '$rid'. Known: $($files.Keys -join ', ')" }
    $url = "https://storage.googleapis.com/cloud-sql-connectors/cloud-sql-proxy/$Version/$file"

    $line = ($release.body -split "`n") | Where-Object { $_ -match [regex]::Escape("/$Version/$file") } | Select-Object -First 1
    if ($line -notmatch '\|\s*([0-9a-f]{64})\s*\|') { throw "No published SHA-256 found for $file in release $Version." }
    $expected = $Matches[1]

    $dir = Join-Path $root "vendor\$rid"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $target = Join-Path $dir ('cloud-sql-proxy' + $(if ($rid -like 'win-*') { '.exe' } else { '' }))
    if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected) {
        "OK  $rid already has $file $Version"
        continue
    }

    $temp = "$target.download"
    "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $temp -UseBasicParsing
    $actual = (Get-FileHash $temp -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Remove-Item $temp -Force
        throw "Checksum mismatch for $file $Version. Expected $expected, got $actual. Nothing was kept."
    }
    Move-Item $temp $target -Force
    Set-Content -Path (Join-Path $dir 'version.txt') -Value $Version -Encoding ASCII
    "OK  $rid  $file $Version  {0:N1} MB  sha256 matches the release notes" -f ((Get-Item $target).Length / 1MB)
}
