<#
.SYNOPSIS
Builds the Juggle OS image with Docker.

.DESCRIPTION
Runs pi-gen in a privileged Linux container (Docker Desktop on Windows, or any Docker host) and copies the image,
its package list and the build log to deploy\ in the repository. A full build takes about 20 minutes (everything for
the Pi runs under ARM emulation); -Quick about 6.
#>
[CmdletBinding()]
param(
    # Reuse the base system (pi-gen's stages 0 and 1) from the last build: about 6 minutes instead of 20. Use full
    # builds for releases.
    [switch]$Quick
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$deploy = Join-Path $repo 'deploy'
$container = 'juggle-os-build'

function Invoke-Docker {
    # Docker writes progress to stderr, which Windows PowerShell turns into errors when output is redirected:
    # only the exit code counts
    $ErrorActionPreference = 'Continue'
    docker @args
    if ($LASTEXITCODE -ne 0) { throw "docker $($args[0]) failed (exit code $LASTEXITCODE)" }
}

$ErrorActionPreference = 'Continue'
docker info --format '{{.ServerVersion}}' 2>&1 | Out-Null
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE -ne 0) { throw 'Docker is not running: start Docker Desktop and try again.' }

# The version is the latest release tag (vX.Y.Z), with the commits since and -dirty for uncommitted changes when it
# isn't a release build: v0.1.0-3-g1ad84dc-dirty
$version = git -C $repo describe --tags --match 'v[0-9]*' --always --dirty
$commit = git -C $repo rev-parse --short HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not read the version from git.' }
# When it was built, in UTC: 20261016-2249
$buildStamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmm')
Write-Host "Building Juggle OS $version (build $buildStamp UTC)"

Invoke-Docker build --platform linux/386 --tag juggle-os-builder $PSScriptRoot

if (docker ps --all --quiet --filter "name=^$container$") {
    Invoke-Docker rm --force --volumes $container | Out-Null
}
try {
    # pi-gen's work folder is kept in a volume between builds, for -Quick
    Invoke-Docker run --name $container --privileged --volume juggle-os-work:/pi-gen/work `
        --env "JUGGLE_VERSION=$version" --env "JUGGLE_COMMIT=$commit" --env "JUGGLE_BUILD_STAMP=$buildStamp" `
        --env "JUGGLE_QUICK=$([int]$Quick.IsPresent)" juggle-os-builder ./build-in-container.sh
}
finally {
    New-Item -ItemType Directory -Force $deploy | Out-Null
    docker cp "${container}:/pi-gen/deploy/." $deploy
    docker rm --volumes $container | Out-Null
}
Write-Host "Image built: see $deploy"
