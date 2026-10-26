param([string]$Dotnet = 'dotnet', [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64', [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $PSScriptRoot 'src\BluetoothCharge\BluetoothCharge.csproj'
$tests = Join-Path $PSScriptRoot 'tests\BluetoothCharge.Tests\BluetoothCharge.Tests.csproj'
$suffix = if ($FrameworkDependent) { "$Runtime-framework-dependent" } else { $Runtime }
$destination = Join-Path $PSScriptRoot "dist\$suffix"
& $Dotnet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) { throw '依赖恢复失败。' }
& $Dotnet run --project $tests -c Release
if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
& $Dotnet publish $project -c Release -r $Runtime --self-contained (!$FrameworkDependent) -p:PublishSingleFile=false -o $destination
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
Write-Host "发布完成：$destination\BluetoothCharge.exe"
