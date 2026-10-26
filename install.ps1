param([switch]$AutoStart, [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot "dist\$Runtime"
if (!(Test-Path -LiteralPath (Join-Path $source 'BluetoothCharge.exe'))) { throw '请先构建程序，或使用包含 dist 的分发包。' }
$target = Join-Path $env:LOCALAPPDATA 'Programs\BluetoothCharge'
if (Get-Process -Name BluetoothCharge -ErrorAction SilentlyContinue) { throw '请先从托盘退出蓝牙充电提醒，再安装或更新。' }
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
foreach ($notice in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'README.en.md', 'licenses', 'docs')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $notice) -Destination $target -Recurse -Force
}
$exe = Join-Path $target 'BluetoothCharge.exe'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) '蓝牙充电提醒.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $target
$shortcut.Save()
if ($AutoStart) {
    New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Force | Out-Null
    New-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name BluetoothCharge -Value ('"' + $exe + '" --background') -PropertyType String -Force | Out-Null
}
Start-Process -FilePath $exe -WindowStyle Hidden
Write-Host "安装完成：$target。托盘双击可打开设置。"
