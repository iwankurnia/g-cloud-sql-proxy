# Builds Cloud SQL Proxy Manager for Windows, Linux and macOS into dist\.
# Needs the .NET 10 SDK (the one on this laptop) and PowerShell 7.
#
#   .\build.ps1                       # all targets
#   .\build.ps1 -Targets linux-x64    # one target
#   .\build.ps1 -Test                 # also runs the controller tests on this machine
#
# Each package carries its own .NET runtime and Google's cloud-sql-proxy for
# that system (fetched by tools\get-proxy.ps1), so the target machine needs
# nothing installed.
#
#   dist\CloudSqlProxyManager-win-x64.exe              single file
#   dist\CloudSqlProxyManager-linux-x64.tar.gz         binary + .desktop + icon + install.sh
#   dist\CloudSqlProxyManager-linux-arm64.tar.gz
#   dist\CloudSqlProxyManager-osx-arm64.tar.gz         "Cloud SQL Proxy Manager.app"  (Apple Silicon)
#   dist\CloudSqlProxyManager-osx-x64.tar.gz           "Cloud SQL Proxy Manager.app"  (Intel)

param(
    [string[]]$Targets = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-arm64', 'osx-x64'),
    [switch]$Test,
    [switch]$SkipPackages
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
New-Item -ItemType Directory -Force $dist | Out-Null

# --- Icons, regenerated when the drawing code changes ---
$iconSource = Get-Item (Join-Path $root 'tools\IconFactory.cs')
$icns = Join-Path $root 'assets\app.icns'
if (-not (Test-Path $icns) -or (Get-Item $icns).LastWriteTimeUtc -lt $iconSource.LastWriteTimeUtc) {
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\make-icons.ps1') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Generating the icons failed.' }
}

function Publish($project, $rid, $out) {
    dotnet publish $project -c Release -r $rid -o $out --nologo -v q 2>&1 | ForEach-Object {
        $line = "$_"
        if ($line -match 'error|warning MSB|No proxy at') { Write-Output $line }
    }
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project for $rid failed." }
}

function ThisRid {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($IsWindows) { return "win-$arch" }
    if ($IsMacOS) { return "osx-$arch" }
    return "linux-$arch"
}

# --- tar.gz writer that keeps Unix permissions (zip made on Windows would not) ---
function Write-TarGz([string]$archive, [string]$sourceDir, [string[]]$executables) {
    $fs = [System.IO.File]::Create($archive)
    $gz = New-Object System.IO.Compression.GZipStream($fs, [System.IO.Compression.CompressionLevel]::Optimal)
    $tar = New-Object System.Formats.Tar.TarWriter($gz, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
    try {
        $files = Get-ChildItem $sourceDir -Recurse -File | Sort-Object FullName
        foreach ($f in $files) {
            $rel = $f.FullName.Substring($sourceDir.Length).TrimStart('\', '/').Replace('\', '/')
            $entry = New-Object System.Formats.Tar.PaxTarEntry([System.Formats.Tar.TarEntryType]::RegularFile, $rel)
            $exec = $executables | Where-Object { $rel -eq $_ -or $rel -like "*/$_" }
            $entry.Mode = if ($exec) { [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute' } `
                          else      { [System.IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead' }
            $entry.DataStream = [System.IO.File]::OpenRead($f.FullName)
            $tar.WriteEntry($entry)
            $entry.DataStream.Dispose()
        }
    }
    finally { $tar.Dispose(); $gz.Dispose(); $fs.Dispose() }
}

foreach ($rid in $Targets) {
    $out = Join-Path $dist $rid
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    "== $rid"
    Publish (Join-Path $root 'src\App\App.csproj') $rid $out
    Get-ChildItem $out -Recurse -File | Where-Object { $_.Name -like '*.pdb' } | Remove-Item -Force
    if ($SkipPackages) { continue }

    if ($rid -like 'win-*') {
        Copy-Item (Join-Path $out 'CloudSqlProxyManager.exe') (Join-Path $dist "CloudSqlProxyManager-$rid.exe") -Force
    }
    elseif ($rid -like 'linux-*') {
        $stage = Join-Path $dist "stage-$rid\cloudsql-proxy-manager"
        if (Test-Path (Split-Path $stage)) { Remove-Item (Split-Path $stage) -Recurse -Force }
        New-Item -ItemType Directory -Force $stage | Out-Null
        Copy-Item (Join-Path $out 'CloudSqlProxyManager') $stage
        Copy-Item (Join-Path $root 'assets\app-256.png') (Join-Path $stage 'cloudsql-proxy-manager.png')
        Copy-Item (Join-Path $root 'packaging\linux\cloudsql-proxy-manager.desktop') $stage
        Copy-Item (Join-Path $root 'packaging\linux\install.sh') $stage
        Copy-Item (Join-Path $root 'packaging\linux\README.txt') $stage
        Write-TarGz (Join-Path $dist "CloudSqlProxyManager-$rid.tar.gz") (Split-Path $stage) @('CloudSqlProxyManager', 'install.sh')
        Remove-Item (Split-Path $stage) -Recurse -Force
    }
    elseif ($rid -like 'osx-*') {
        $stageRoot = Join-Path $dist "stage-$rid"
        $app = Join-Path $stageRoot 'Cloud SQL Proxy Manager.app'
        if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
        New-Item -ItemType Directory -Force "$app\Contents\MacOS", "$app\Contents\Resources" | Out-Null
        Copy-Item (Join-Path $out 'CloudSqlProxyManager') "$app\Contents\MacOS\"
        Copy-Item (Join-Path $root 'assets\app.icns') "$app\Contents\Resources\app.icns"
        (Get-Content (Join-Path $root 'packaging\macos\Info.plist') -Raw).Replace('{VERSION}', $version) |
            Set-Content "$app\Contents\Info.plist" -Encoding UTF8 -NoNewline
        Copy-Item (Join-Path $root 'packaging\macos\README.txt') $stageRoot
        Write-TarGz (Join-Path $dist "CloudSqlProxyManager-$rid.tar.gz") $stageRoot @('CloudSqlProxyManager')
        Remove-Item $stageRoot -Recurse -Force
    }
}

if ($Test) {
    $rid = ThisRid
    $testOut = Join-Path $dist "tests-$rid"
    if (Test-Path $testOut) { Remove-Item $testOut -Recurse -Force }
    "== tests ($rid)"
    Publish (Join-Path $root 'tests\Tests.csproj') $rid $testOut
    # The stand-in proxy is one self-contained file; it must not share the runner's runtime files.
    Publish (Join-Path $root 'tests\FakeProxy\FakeProxy.csproj') $rid "$testOut\fake"
    Move-Item "$testOut\fake\fakeproxy*" $testOut -Force
    Remove-Item "$testOut\fake" -Recurse -Force
    & (Join-Path $testOut ('ControllerTests' + $(if ($IsWindows) { '.exe' } else { '' })))
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }
}

''
Get-ChildItem $dist -File | ForEach-Object { "{0,-44} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
