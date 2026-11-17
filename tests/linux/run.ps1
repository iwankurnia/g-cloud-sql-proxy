# Runs the Linux build inside a container with a virtual display: controller
# tests, then the window driven by xdotool with screenshots saved to -OutDir.
# Needs Docker Desktop running, and .\build.ps1 -Targets linux-x64 done first.
#
#   .\tests\linux\run.ps1 -OutDir $env:TEMP\proxy-linux

param([Parameter(Mandatory = $true)][string]$OutDir)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$image = 'cloudsql-proxy-manager-test'
$work = Join-Path $OutDir 'work'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force "$work\app", "$work\tests", "$work\out" | Out-Null

"== Linux test binaries"
dotnet publish (Join-Path $root 'tests\Tests.csproj') -c Release -r linux-x64 -o "$work\tests" --nologo -v q 2>&1 | Where-Object { "$_" -match 'error' }
if ($LASTEXITCODE -ne 0) { throw 'Publishing the tests for linux-x64 failed.' }
dotnet publish (Join-Path $root 'tests\FakeProxy\FakeProxy.csproj') -c Release -r linux-x64 -o "$work\fake" --nologo -v q 2>&1 | Where-Object { "$_" -match 'error' }
if ($LASTEXITCODE -ne 0) { throw 'Publishing the stand-in proxy for linux-x64 failed.' }
Move-Item "$work\fake\fakeproxy" "$work\tests\fakeproxy" -Force
Remove-Item "$work\fake" -Recurse -Force
Copy-Item (Join-Path $root 'dist\linux-x64\CloudSqlProxyManager') "$work\app\"
Copy-Item (Join-Path $PSScriptRoot 'inside.sh') "$work\inside.sh"
# The script must have Unix line endings inside the container.
[System.IO.File]::WriteAllText("$work\inside.sh", ([System.IO.File]::ReadAllText("$work\inside.sh") -replace "`r`n", "`n"))

"== test image"
docker build -q -t $image $PSScriptRoot | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Building the test image failed.' }

"== run"
docker run --rm -v "${work}:/work" $image sh /work/inside.sh
$code = $LASTEXITCODE
Copy-Item "$work\out\*" $OutDir -Force
"screenshots and logs in $OutDir"
exit $code
