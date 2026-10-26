<#
.SYNOPSIS
Runs the hardware-free Release checks and packages both portable Windows x64 editions.
.EXAMPLE
./scripts/package-release.ps1
.EXAMPLE
./scripts/package-release.ps1 -Dotnet C:\SDK\dotnet.exe -OutputDirectory C:\Packages
.NOTES
The framework-dependent ZIP needs the .NET 10 Windows Desktop Runtime.
The self-contained ZIP includes that runtime. Extract either ZIP, then run BluetoothCharge.exe.
This script does not start the application, probe hardware, install it, or create a GitHub release.
#>
[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$dotnetExe = (Get-Command $Dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$project = Join-Path $repository 'src\BluetoothCharge\BluetoothCharge.csproj'
$tests = Join-Path $repository 'tests\BluetoothCharge.Tests\BluetoothCharge.Tests.csproj'
$files = @('README.md', 'README.en.md', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE')
$directories = @('docs', 'licenses')

foreach ($relative in @('global.json', 'src\BluetoothCharge\BluetoothCharge.csproj',
        'src\BluetoothCharge\packages.lock.json', 'tests\BluetoothCharge.Tests\BluetoothCharge.Tests.csproj') + $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $repository $relative) -PathType Leaf)) {
        throw "Required repository file is missing: $relative"
    }
}
foreach ($relative in $directories) {
    if (-not (Test-Path -LiteralPath (Join-Path $repository $relative) -PathType Container)) {
        throw "Required repository directory is missing: $relative"
    }
}
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode -or $versionNode.InnerText -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw 'The project must declare a valid release Version.'
}
$version = $versionNode.InnerText

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & $dotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

# A new stage prevents old builds, local configuration, or device diagnostics entering a ZIP.
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stageName = '.package-' + [Guid]::NewGuid().ToString('N')
$stageRoot = [IO.Path]::GetFullPath((Join-Path $outputRoot $stageName))
$expectedParent = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($expectedParent -eq [IO.Path]::GetPathRoot($outputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)) {
    $expectedParent = [IO.Path]::GetPathRoot($outputRoot)
}
if (-not [string]::Equals([IO.Path]::GetDirectoryName($stageRoot), $expectedParent,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The temporary packaging directory must remain directly inside the output directory.'
}
New-Item -ItemType Directory -Path $stageRoot | Out-Null
$artifacts = [Collections.Generic.List[string]]::new()
Push-Location -LiteralPath $repository
try {
    Invoke-Dotnet -Arguments @('restore', $project, '--locked-mode')
    # The test project has no additional PackageReference and pins its Windows SDK version.
    Invoke-Dotnet -Arguments @('run', '--project', $tests, '-c', 'Release')

    $editions = @(
        @{ Name = 'framework-dependent'; SelfContained = 'false' },
        @{ Name = 'self-contained'; SelfContained = 'true' }
    )
    foreach ($edition in $editions) {
        $name = "BluetoothCharge-$version-win-x64-$($edition.Name)"
        $publishDirectory = Join-Path $stageRoot $name
        Invoke-Dotnet -Arguments @('publish', $project, '-c', 'Release', '-r', 'win-x64',
            '--self-contained', $edition.SelfContained, '-p:PublishSingleFile=false',
            '-p:RestoreLockedMode=true', '-o', $publishDirectory)
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'BluetoothCharge.exe') -PathType Leaf)) {
            throw "The $($edition.Name) publish did not produce BluetoothCharge.exe."
        }
        foreach ($relative in $files + $directories) {
            Copy-Item -LiteralPath (Join-Path $repository $relative) -Destination $publishDirectory -Recurse
        }
        # The source-only install.ps1 expects dist/, so portable ZIPs do not include it.
        $temporaryZip = Join-Path $stageRoot "$name.zip"
        [IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $temporaryZip,
            [IO.Compression.CompressionLevel]::Optimal, $false)
        $zip = Join-Path $outputRoot "$name.zip"
        Move-Item -LiteralPath $temporaryZip -Destination $zip -Force
        $artifacts.Add($zip)
    }

    $checksumLines = foreach ($artifact in $artifacts) {
        $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
        '{0}  {1}' -f $hash, [IO.Path]::GetFileName($artifact)
    }
    $checksumPath = Join-Path $outputRoot 'SHA256SUMS.txt'
    [IO.File]::WriteAllLines($checksumPath, [string[]]$checksumLines, [Text.UTF8Encoding]::new($false))
    Write-Host "Portable packages created in $outputRoot"
    $artifacts | ForEach-Object { Write-Host $_ }
    Write-Host $checksumPath
}
finally {
    Pop-Location
    # Delete only this invocation's verified, unique stage; never clean the output directory.
    $resolvedStage = [IO.Path]::GetFullPath($stageRoot)
    if ([string]::Equals([IO.Path]::GetDirectoryName($resolvedStage), $expectedParent,
            [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedStage) -eq $stageName -and
        (Test-Path -LiteralPath $resolvedStage -PathType Container)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
