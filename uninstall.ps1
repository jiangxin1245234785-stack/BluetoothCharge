param([switch]$KeepData)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\BluetoothCharge'))
$allowed = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs')) + [IO.Path]::DirectorySeparatorChar
if (!$target.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($target) -ne 'BluetoothCharge') { throw '卸载目标不在预期目录。' }
if (Get-Process -Name BluetoothCharge -ErrorAction SilentlyContinue) { throw '请先从托盘退出程序，再卸载。' }
$exe = Join-Path $target 'BluetoothCharge.exe'
if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe -ArgumentList '--unregister' -WindowStyle Hidden -Wait }
Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name BluetoothCharge -ErrorAction SilentlyContinue
$link = Join-Path ([Environment]::GetFolderPath('Programs')) '蓝牙充电提醒.lnk'
if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link }
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
if (!$KeepData) {
    $data = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'BluetoothCharge'))
    $parent = [IO.Path]::GetFullPath($env:LOCALAPPDATA) + [IO.Path]::DirectorySeparatorChar
    if (!$data.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($data) -ne 'BluetoothCharge') { throw '数据目标不在预期目录。' }
    if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
}
Write-Host '卸载完成。'
