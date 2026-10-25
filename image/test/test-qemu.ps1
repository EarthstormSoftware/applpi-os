<#
.SYNOPSIS
Boots a built ApplPi OS image in QEMU and checks it over SSH.

.DESCRIPTION
Boots the image on QEMU's generic ARM machine with Debian's generic ARM kernel (QEMU's Pi 2 model can't read the SD
card with the Pi kernel). This checks first boot (root B and the data partition), applpi.json, SSH by key, the
read-only root and what listens on the network. The full test then writes an OS update (the same image, into slot B)
and boots slot B, as tryboot would: the host key must stay the same and the root's changes be gone. Not the Pi's own
kernel, firmware (os_prefix, tryboot) or Wi-Fi. -Quick (one boot) takes about 20 minutes.
Results go to deploy\test\.

.PARAMETER Image
The image to test. Defaults to the newest in deploy\.

.PARAMETER Quick
Boot once instead of twice. Use the full test for releases.
#>
[CmdletBinding()]
param([string]$Image, [switch]$Quick)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$deploy = Join-Path $repo 'deploy'
if (-not $Image) {
    $Image = Get-ChildItem $deploy -Filter 'applpi-os-*.img.xz' | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $Image) { throw "No image in ${deploy}: build one with image\build.ps1" }
}
$Image = (Resolve-Path $Image).Path
$volume = 'applpi-os-test'
$results = Join-Path $deploy 'test'

function Invoke-Docker {
    # Docker writes progress to stderr, which Windows PowerShell turns into errors when output is redirected
    $ErrorActionPreference = 'Continue'
    docker @args
    if ($LASTEXITCODE -ne 0) { throw "docker $($args[0]) failed (exit code $LASTEXITCODE)" }
}

function Invoke-Step([string[]]$DockerArgs, [string[]]$Command) {
    Invoke-Docker run --rm -v "${volume}:/work" -v "${PSScriptRoot}:/test:ro" @DockerArgs bash /test/qemu-virt.sh @Command
}

# The test tools are kept in two images, made once (and again only when their Dockerfiles change)
Invoke-Docker build --quiet --tag applpi-os-tester --file (Join-Path $PSScriptRoot 'tester.Dockerfile') $PSScriptRoot | Out-Null
Invoke-Docker build --quiet --platform linux/arm/v7 --tag applpi-os-test-kernel `
    --file (Join-Path $PSScriptRoot 'kernel.Dockerfile') $PSScriptRoot | Out-Null

Write-Host "Testing $Image$(if ($Quick) { ' (quick: one boot)' })"
New-Item -ItemType Directory -Force $results | Out-Null
Get-ChildItem $results | Remove-Item -Recurse -Force
try {
    Invoke-Step @('--privileged', '-v', "$(Split-Path $Image):/image:ro", 'applpi-os-tester') @('unpack', "/image/$(Split-Path -Leaf $Image)")
    Invoke-Step @('--platform', 'linux/arm/v7', 'applpi-os-test-kernel') @('kernel')
    Invoke-Step @('--privileged', '-v', "$(Split-Path $Image):/image:ro", 'applpi-os-tester') `
        @('boot', $(if ($Quick) { 'quick' } else { 'full' }), "/image/$(Split-Path -Leaf $Image)")
}
finally {
    $ErrorActionPreference = 'Continue'
    docker run --rm -v "${volume}:/work" -v "${results}:/results" applpi-os-tester sh -c 'cp /work/out/* /results/ 2>/dev/null'
    docker volume rm $volume | Out-Null
}
Get-Content (Join-Path $results 'summary.log')
Write-Host "Results: $results"
