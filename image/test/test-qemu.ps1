<#
.SYNOPSIS
Boots a built ApplPi OS image in QEMU and checks it over SSH.

.DESCRIPTION
Boots the image twice on QEMU's generic ARM machine with Debian's generic ARM kernel (QEMU's Pi 2 model can't read
the SD card with the Pi kernel). This checks first boot (root B and the data partition), applpi.json, SSH by key,
the read-only root and what listens on the network, and that the host key stays the same on the second boot; not
the Pi's own kernel, firmware or Wi-Fi. Takes 30-60 minutes. Results go to deploy\test\.

.PARAMETER Image
The image to test. Defaults to the newest in deploy\.
#>
[CmdletBinding()]
param([string]$Image)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$deploy = Join-Path $repo 'deploy'
if (-not $Image) {
    $Image = Get-ChildItem $deploy -Filter '*.img.xz' | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $Image) { throw "No image in ${deploy}: build one with image\build.ps1" }
}
$Image = (Resolve-Path $Image).Path
$volume = 'applpi-test'
$results = Join-Path $deploy 'test'

function Invoke-Step([string[]]$DockerArgs, [string[]]$Command) {
    # Docker writes progress to stderr, which Windows PowerShell turns into errors when output is redirected
    $ErrorActionPreference = 'Continue'
    docker run --rm -v "${volume}:/work" -v "${PSScriptRoot}:/test:ro" @DockerArgs bash /test/qemu-virt.sh @Command
    if ($LASTEXITCODE -ne 0) { throw "Test step '$($Command[0])' failed (exit code $LASTEXITCODE)" }
}

Write-Host "Testing $Image"
Invoke-Step @('--privileged', '-v', "$(Split-Path $Image):/image:ro", 'debian:trixie') @('unpack', "/image/$(Split-Path -Leaf $Image)")
Invoke-Step @('--platform', 'linux/arm/v7', 'debian:trixie') @('kernel')
try {
    Invoke-Step @('--privileged', 'debian:trixie') @('boot')
}
finally {
    New-Item -ItemType Directory -Force $results | Out-Null
    $ErrorActionPreference = 'Continue'
    docker run --rm -v "${volume}:/work" -v "${results}:/results" debian:trixie sh -c 'cp /work/out/* /results/'
    docker volume rm $volume | Out-Null
}
Get-Content (Join-Path $results 'summary.log')
Write-Host "Results: $results"
