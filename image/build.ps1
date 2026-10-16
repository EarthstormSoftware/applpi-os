<#
.SYNOPSIS
Builds the ApplPi OS image with Docker.

.DESCRIPTION
Runs pi-gen in a privileged Linux container (Docker Desktop on Windows, or any Docker host) and copies the image,
its package list and the build log to deploy\ in the repository. A build takes roughly an hour: everything for the
Pi runs under ARM emulation.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$deploy = Join-Path $repo 'deploy'
$container = 'applpi-build'

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

$version = git -C $repo describe --tags --always --dirty
if ($LASTEXITCODE -ne 0) { throw 'Could not read the version from git.' }

Invoke-Docker build --platform linux/386 --tag applpi-build $PSScriptRoot

if (docker ps --all --quiet --filter "name=^$container$") {
    Invoke-Docker rm --force --volumes $container | Out-Null
}
try {
    Invoke-Docker run --name $container --privileged --env "APPLPI_VERSION=$version" applpi-build ./build-in-container.sh
}
finally {
    New-Item -ItemType Directory -Force $deploy | Out-Null
    docker cp "${container}:/pi-gen/deploy/." $deploy
    docker rm --volumes $container | Out-Null
}
Write-Host "Image built: see $deploy"
