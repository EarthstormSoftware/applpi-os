<#
.SYNOPSIS
Makes a deliberately broken copy of a built ApplPi OS image, to check that an OS update to it goes back to the old
slot.

.DESCRIPTION
Adds a service that always fails (applpi-test-broken.service) to the image's root, so the Pi's check of its trial boot
finds a failed unit and restarts into the permanent slot. The copy is deploy\<image>-broken.img.xz, with no .bmap
(the root has changed, so the image's block map no longer fits it): applpi update writes all of its root. Then:

    dotnet run --project src\ApplPi.Cli -- update <name> deploy\<image>-broken.img.xz

With -App, the OS is fine but the app can't start under it (a drop-in for applpi-app.service makes its start fail),
so the trial boot is given up because the app isn't running, as an OS update that broke the app would be. The copy
is then deploy\<image>-broken-app.img.xz.

With -Panic, the kernel crashes 10 seconds into the trial boot (SysRq c), and panic=10 restarts the Pi; with -Hang, the
Pi stops 10 seconds in (a busy loop at a real-time priority on every core, with the kernel's limit on
real-time tasks off). On a Pi 2 that stops the card answering while systemd still runs and feeds the watchdog, so it's
the trial boot's guard (os-update guard) that restarts it, after 15 minutes; a hang that stops systemd too is the
hardware watchdog's. Either way the
trial is spent, so the Pi starts the permanent slot again. Both happen before the Pi's own check of the trial boot
(applpi-os-check.service waits for them), so the slot can't be kept. The copies are -broken-panic and -broken-hang.

.PARAMETER Image
The image to break. Defaults to the newest in deploy\ that isn't already broken.
#>
[CmdletBinding()]
param([string]$Image, [switch]$App, [switch]$Panic, [switch]$Hang)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$deploy = Join-Path $repo 'deploy'
if (-not $Image) {
    $Image = Get-ChildItem $deploy -Filter 'applpi-os-*.img.xz' | Where-Object Name -notlike '*-broken*.img.xz' |
        Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $Image) { throw "No image in ${deploy}: build one with image\build.ps1" }
}
$Image = (Resolve-Path $Image).Path
if (([int][bool]$App + [int][bool]$Panic + [int][bool]$Hang) -gt 1) { throw 'Choose one of -App, -Panic and -Hang.' }
$kind = if ($App) { 'broken-app' } elseif ($Panic) { 'broken-panic' } elseif ($Hang) { 'broken-hang' } else { 'broken' }
$broken = $Image -replace '\.img\.xz$', "-$kind.img.xz"
if ($Panic -or $Hang) {
    $what = if ($Panic) { 'crashes the kernel' } else { 'stops the Pi (the guard or the watchdog restarts it)' }
    $action = if ($Panic) { 'echo c > /proc/sysrq-trigger' } else {
        # Every core busy at a real-time priority, with nothing kept back for other tasks: below the guard's (99)
        @'
echo -1 > /proc/sys/kernel/sched_rt_runtime_us
for cpu in $(seq 0 $(($(nproc) - 1))); do
	taskset -c "$cpu" chrt -f 98 sh -c 'while :; do :; done' &
done
wait
'@
    }
    $breakage = @"
mkdir -p /mnt/root/usr/local/sbin
cat > /mnt/root/usr/local/sbin/applpi-test-fault <<'EOF'
#!/bin/sh
# Test only (image\test\broken-image.ps1 -$($kind -replace 'broken-', '')): $what during the trial boot
sleep 10
$action
EOF
chmod 755 /mnt/root/usr/local/sbin/applpi-test-fault
cat > /mnt/root/etc/systemd/system/applpi-test-fault.service <<'EOF'
# Test only (image\test\broken-image.ps1): $what 10 seconds into every boot, before the Pi checks a trial boot,
# so an OS update to this image goes back
[Unit]
Description=Deliberate fault (ApplPi OS update test)
DefaultDependencies=no
After=local-fs.target
Before=applpi-os-check.service

[Service]
Type=oneshot
ExecStart=/usr/local/sbin/applpi-test-fault

[Install]
WantedBy=multi-user.target
EOF
ln -s /etc/systemd/system/applpi-test-fault.service /mnt/root/etc/systemd/system/multi-user.target.wants/
"@
} elseif ($App) {
    $breakage = @'
mkdir -p /mnt/root/etc/systemd/system/applpi-app.service.d
cat > /mnt/root/etc/systemd/system/applpi-app.service.d/test-broken.conf <<'EOF'
# Test only (image\test\broken-image.ps1 -App): the app can't start under this OS, so an OS update to it goes back
[Service]
ExecStartPre=/bin/false
EOF
'@
} else {
    $breakage = @'
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
'@
}
$breakage = $breakage -replace "`r", ''

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
$breakage
sed -i 's/^VERSION=.*/&-$kind/' /mnt/root/etc/applpi-release
umount /mnt/root
# The release file on the boot partition, which the update copies into the slot, says so too
boot=`$(sfdisk -d /tmp/card.img | awk -F'[=,]' '/card.img1 / {print `$2}')
mcopy -i /tmp/card.img@@`$((boot * 512)) ::applpi-release.txt /tmp/release.txt
sed -i 's/^VERSION=.*/&-$kind/' /tmp/release.txt
mcopy -o -i /tmp/card.img@@`$((boot * 512)) /tmp/release.txt ::applpi-release.txt
mcopy -o -i /tmp/card.img@@`$((boot * 512)) /tmp/release.txt ::slotA/applpi-release.txt
xz -T0 -1 -c /tmp/card.img > '/deploy/$(Split-Path -Leaf $broken)'
"@
if ($LASTEXITCODE -ne 0) { throw "Couldn't make the broken image (exit code $LASTEXITCODE)." }
Write-Host "Made $broken"
