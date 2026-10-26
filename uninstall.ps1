<#
.SYNOPSIS
  Удаляет SR2 Mod Manager (плагин, лаунчер, ярлык). Сами моды не удаляются.
.PARAMETER EnableAll
  Вернуть все выключенные моды из Mods\Disabled обратно в Mods (включить всё).
.PARAMETER GameDir
  Папка Slime Rancher 2. Если не указана: папка над скриптом (он лежит в <игра>\ModManager), затем библиотеки Steam.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File uninstall.ps1
  powershell -ExecutionPolicy Bypass -File uninstall.ps1 -EnableAll
#>
param(
    [switch]$EnableAll,
    [string]$GameDir = ''
)
$ErrorActionPreference = 'Stop'

function Test-GameDir([string]$d) { return [bool]($d -and (Test-Path -LiteralPath (Join-Path $d 'SlimeRancher2.exe'))) }

function Find-GameDir {
    $up = Split-Path $PSScriptRoot -Parent
    if (Test-GameDir $up) { return $up }
    $roots = @()
    foreach ($k in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $p = Get-ItemProperty -Path $k -ErrorAction SilentlyContinue
        if ($p -and $p.SteamPath) { $roots += ($p.SteamPath -replace '/', '\') }
        if ($p -and $p.InstallPath) { $roots += $p.InstallPath }
    }
    $libs = @()
    foreach ($r in $roots) {
        $libs += $r
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += ($m.Groups[1].Value -replace '\\\\', '\') }
        }
    }
    $libs += 'C:\Program Files (x86)\Steam', 'D:\SteamLibrary', 'E:\SteamLibrary'
    foreach ($l in ($libs | Select-Object -Unique)) {
        $d = Join-Path $l 'steamapps\common\Slime Rancher 2'
        if (Test-GameDir $d) { return $d }
    }
    return ''
}

if ($GameDir) { $GameDir = $GameDir.TrimEnd('\', '/') } else { $GameDir = Find-GameDir }
if (-not (Test-GameDir $GameDir)) { throw 'Не найдена папка Slime Rancher 2 — укажите её: -GameDir "<папка игры>"' }
Write-Host "Папка игры: $GameDir"

if (Get-Process -Name SlimeRancher2 -ErrorAction SilentlyContinue) { throw 'Сначала закройте Slime Rancher 2.' }
if (Get-Process -Name SR2ModLauncher -ErrorAction SilentlyContinue) { throw 'Сначала закройте окно SR2 Mod Manager.' }
$plugin = Join-Path $GameDir 'Plugins\SR2ModManager.dll'
foreach ($f in @($plugin, "$plugin.bak")) { if (Test-Path $f) { Remove-Item $f -Force; Write-Host "удалён $f" } }
Get-ChildItem (Join-Path $GameDir 'Plugins') -Filter 'SR2ModManager.dll.old*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'SR2 Mod Manager.lnk'
if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "удалён ярлык $lnk" }
$pending = Join-Path $GameDir 'UserData\ModManager\pending.json'
if (Test-Path $pending) { Remove-Item $pending -Force; Write-Host 'отменены запланированные изменения' }
$disabled = Join-Path $GameDir 'Mods\Disabled'
if ($EnableAll -and (Test-Path $disabled)) {
    Get-ChildItem $disabled -Filter *.dll | ForEach-Object {
        $to = Join-Path $GameDir ('Mods\' + $_.Name)
        if (Test-Path $to) { Write-Host "пропуск $($_.Name): в Mods уже есть такой файл" }
        else { Move-Item $_.FullName $to; Write-Host "включён $($_.Name)" }
    }
} elseif (Test-Path $disabled) {
    $left = @(Get-ChildItem $disabled -Filter *.dll)
    if ($left.Count -gt 0) { Write-Host "В Mods\Disabled остались выключенные моды ($($left.Count)) — запустите с -EnableAll, чтобы вернуть их, или перенесите вручную." -ForegroundColor Yellow }
}
# Лаунчер удаляем последним (скрипт может лежать в той же папке)
$mm = Join-Path $GameDir 'ModManager'
if (Test-Path $mm) {
    $upd = Join-Path $mm 'update'
    if (Test-Path $upd) { Remove-Item $upd -Recurse -Force -ErrorAction SilentlyContinue; Write-Host 'удалено подготовленное обновление плагина' }
    foreach ($f in @('SR2ModLauncher.exe', 'SR2ModLauncher.exe.bak')) { Remove-Item (Join-Path $mm $f) -Force -ErrorAction SilentlyContinue }
    Write-Host "удалён лаунчер из $mm (папку можно удалить вручную)"
}
Write-Host 'Готово. Настройки наборов остались в UserData\ModManager (можно удалить).' -ForegroundColor Green
