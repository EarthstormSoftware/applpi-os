<#
.SYNOPSIS
Tests a built Juggle OS image on a real Pi: updates the Pi to it over SSH, then checks it.

.DESCRIPTION
The day-to-day test: about 7 minutes, against the 20 to 45 of the QEMU test, and on the Pi's own kernel, firmware
(os_prefix, tryboot) and Wi-Fi. The update goes into the slot that isn't running, so each run also switches slots.
Then the checks the QEMU test runs (checks.sh) run on the Pi: they must all pass. The log goes to deploy\test-pi\.
The Pi's data partition (apps, settings) is left as it is. A Pi on an image from before A/B updates must be flashed
once first (juggle flash).

What only a first boot does (laying out the card, juggle.json) isn't tested here: the QEMU test does that.

.PARAMETER Name
The Pi's name. Defaults to the last Pi set up (%APPDATA%\Juggle\devices.json).

.PARAMETER Image
The image to test. Defaults to the newest in deploy\ that isn't a broken test copy.

.PARAMETER NoUpdate
Only run the checks, on what the Pi runs now.
#>
[CmdletBinding()]
param([string]$Name, [string]$Image, [switch]$NoUpdate)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$deploy = Join-Path $repo 'deploy'
$results = Join-Path $deploy 'test-pi'
$started = Get-Date

if (-not $Name) {
    $devices = Join-Path $env:APPDATA 'Juggle\devices.json'
    if (Test-Path $devices) { $Name = (Get-Content $devices -Raw | ConvertFrom-Json).LastDevice }
    if (-not $Name) { throw 'Name the Pi: no Pi has been set up on this PC yet.' }
}
$target = "juggle@$Name.local"

# Native programs' stderr isn't an error here: their exit codes say how they went
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force $results | Out-Null

if (-not $NoUpdate) {
    if (-not $Image) {
        $Image = Get-ChildItem $deploy -Filter 'juggle-os-*.img.xz' | Where-Object Name -notlike '*-broken*.img.xz' |
            Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
        if (-not $Image) { throw "No image in ${deploy}: build one with image\build.ps1" }
    }
    Write-Host "Updating $Name to $(Split-Path -Leaf $Image)"
    dotnet run --project (Join-Path $repo 'src\Juggle.Cli') -- update $Name $Image --yes
    if ($LASTEXITCODE -ne 0) { throw "The update failed (exit code $LASTEXITCODE): see above." }
}

Write-Host "Checking $Name"
$log = Join-Path $results 'checks.log'
scp -q (Join-Path $PSScriptRoot 'checks.sh') "${target}:/tmp/juggle-checks.sh"
if ($LASTEXITCODE -ne 0) { throw "Couldn't reach $Name over SSH." }
ssh -o ConnectTimeout=15 $target 'bash /tmp/juggle-checks.sh; status=$?; rm -f /tmp/juggle-checks.sh; exit $status' |
    Out-File -Encoding utf8 $log
$status = $LASTEXITCODE
Get-Content $log | Where-Object { $_ -match '^(ok|FAILED) ' } | ForEach-Object {
    Write-Host $_ -ForegroundColor $(if ($_ -like 'FAILED*') { 'Red' } else { 'Gray' })
}
$minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
if ($status -ne 0) {
    Write-Host "Failed after $minutes minutes: see $log" -ForegroundColor Red
    exit 1
}
Write-Host "All checks passed on $Name, in $minutes minutes. The log: $log" -ForegroundColor Green
