<#
.SYNOPSIS
Makes a deliberately broken copy of a built ApplPi OS image, to check that an OS update to it goes back to the old
slot.

.DESCRIPTION
Adds a service that always fails (applpi-test-broken.service) to the image's root, so the Pi's check of its trial boot
finds a failed unit and restarts into the permanent slot. The copy is deploy\<image>-broken.img.xz, with no .bmap
(the root has changed, so the image's block map no longer fits it): applpi update writes all of its root. Then:

    dotnet run --project src\ApplPi.Cli -- update <name> deploy\<image>-broken.img.xz

.PARAMETER Image
The image to break. Defaults to the newest in deploy\ that isn't already broken.
#>
[CmdletBinding()]
param([string]$Image)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$deploy = Join-Path $repo 'deploy'
if (-not $Image) {
    $Image = Get-ChildItem $deploy -Filter 'applpi-os-*.img.xz' | Where-Object Name -notlike '*-broken.img.xz' |
        Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $Image) { throw "No image in ${deploy}: build one with image\build.ps1" }
}
$Image = (Resolve-Path $Image).Path
$broken = $Image -replace '\.img\.xz$', '-broken.img.xz'

# Docker writes progress to stderr, which Windows PowerShell turns into errors when output is redirected
$ErrorActionPreference = 'Continue'
docker build --quiet --tag applpi-os-tester --file (Join-Path $PSScriptRoot 'tester.Dockerfile') $PSScriptRoot | Out-Null
Write-Host "Breaking $(Split-Path -Leaf $Image)"
docker run --rm --privileged -v "$(Split-Path $Image):/deploy" applpi-os-tester bash -c @"
set -euo pipefail
xz -dc '/deploy/$(Split-Path -Leaf $Image)' > /tmp/card.img
start=`$(sfdisk -d /tmp/card.img | awk -F'[=,]' '/card.img2 / {print `$2}')
mkdir -p /mnt/root
mount -o loop,offset=`$((start * 512)) /tmp/card.img /mnt/root
cat > /mnt/root/etc/systemd/system/applpi-test-broken.service <<'EOF'
# Test only (image\test\broken-image.ps1): fails at every boot, so an OS update to this image goes back
[Unit]
Description=Deliberately broken (ApplPi OS update test)

[Service]
Type=oneshot
ExecStart=/bin/false

[Install]
WantedBy=multi-user.target
EOF
ln -s /etc/systemd/system/applpi-test-broken.service /mnt/root/etc/systemd/system/multi-user.target.wants/
sed -i 's/^VERSION=.*/&-broken/' /mnt/root/etc/applpi-release
umount /mnt/root
# The release file on the boot partition, which the update copies into the slot, says so too
boot=`$(sfdisk -d /tmp/card.img | awk -F'[=,]' '/card.img1 / {print `$2}')
mcopy -i /tmp/card.img@@`$((boot * 512)) ::applpi-release.txt /tmp/release.txt
sed -i 's/^VERSION=.*/&-broken/' /tmp/release.txt
mcopy -o -i /tmp/card.img@@`$((boot * 512)) /tmp/release.txt ::applpi-release.txt
mcopy -o -i /tmp/card.img@@`$((boot * 512)) /tmp/release.txt ::slotA/applpi-release.txt
xz -T0 -1 -c /tmp/card.img > '/deploy/$(Split-Path -Leaf $broken)'
"@
if ($LASTEXITCODE -ne 0) { throw "Couldn't make the broken image (exit code $LASTEXITCODE)." }
Write-Host "Made $broken"
