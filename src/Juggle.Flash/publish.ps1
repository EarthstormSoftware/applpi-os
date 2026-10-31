<#
.SYNOPSIS
Publishes Juggle Flash for Windows or Linux (x64): one self-contained juggle-flash, with juggle beside it (started with
administrator rights to write a card), packed as juggle-flash-<version>-<runtime>.zip (Windows) or .tar.gz (Linux).

.DESCRIPTION
The version comes from the latest release tag, as for the image and juggle. Run it on the system it's for: Windows
gives the program its icon (which needs Windows), and Linux keeps the programs' executable bits in the .tar.gz.

.EXAMPLE
.\src\Juggle.Flash\publish.ps1 -Runtime win-x64
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$Runtime,

    # Where the folder and its archive go (deploy\ is git-ignored)
    [string]$Output = 'deploy'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$version = git -C $root describe --tags --match 'v[0-9]*' --always --dirty
$name = "juggle-flash-$version-$Runtime"
$folder = Join-Path $Output $name
if (Test-Path $folder) {
    Remove-Item -Recurse -Force $folder
}

$publish = @('-r', $Runtime, '--self-contained', '-c', 'Release', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-o', $folder)
foreach ($project in 'Juggle.Flash', 'Juggle.Cli') {
    dotnet publish (Join-Path $root "src\$project") @publish
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

# Only the two programs: not what the app's reference to juggle copied beside it
$programs = if ($Runtime -like 'win-*') { 'juggle-flash.exe', 'juggle.exe' } else { 'juggle-flash', 'juggle' }
Get-ChildItem $folder | Where-Object Name -NotIn $programs | Remove-Item -Recurse -Force
foreach ($program in $programs) {
    if (-not (Test-Path (Join-Path $folder $program))) {
        throw "$program wasn't published."
    }
}

if ($Runtime -like 'win-*') {
    $archive = "$folder.zip"
    Compress-Archive -Path (Join-Path $folder '*') -DestinationPath $archive -Force
}
else {
    $archive = "$folder.tar.gz"
    tar -czf $archive -C $folder $programs
}
Get-ChildItem $folder, $archive | Format-Table Name, @{ Name = 'MiB'; Expression = { [math]::Round($_.Length / 1MB, 1) } }
