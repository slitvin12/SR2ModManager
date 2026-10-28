<#
.SYNOPSIS
  Сборка SR2 Mod Manager: плагин (работает в игре), лаунчер (окно перед запуском) и тесты.
.PARAMETER GameDir
  Папка Slime Rancher 2 с установленным MelonLoader. Если не указана, ищется автоматически:
  папка над скриптом, затем библиотеки Steam.
.PARAMETER Dotnet
  Путь к dotnet.exe с .NET SDK 8 или новее. По умолчанию: $env:DOTNET_EXE, затем .\.dotnet\dotnet.exe,
  затем dotnet из PATH.
.EXAMPLE
  .\build.ps1                                        # собрать в .\_build
  .\build.ps1 -Test                                  # собрать и прогнать тесты
  .\build.ps1 -Install                               # собрать, установить в папку игры, ярлык на рабочем столе
  .\build.ps1 -Install -GameDir 'E:\Games\Slime Rancher 2'
  .\build.ps1 -Package                               # собрать установщик .\_build\SR2ModManager_Setup.exe
#>
param(
    [switch]$Install,
    [switch]$Test,
    [switch]$Package,
    [string]$GameDir = '',
    [string]$Dotnet = ''
)
$ErrorActionPreference = 'Stop'
$stage = Join-Path $PSScriptRoot '_build'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; $env:DOTNET_NOLOGO = '1'

function Test-GameDir([string]$d) { return [bool]($d -and (Test-Path -LiteralPath (Join-Path $d 'SlimeRancher2.exe'))) }

# Папка игры: над скриптом (копия лежит в <игра>\ModManager) или в одной из библиотек Steam
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

function Test-Sdk8([string]$exe) {
    try { $sdks = & $exe --list-sdks 2>$null } catch { return $false }
    foreach ($s in @($sdks)) { if ("$s" -match '^(\d+)\.' -and [int]$Matches[1] -ge 8) { return $true } }
    return $false
}

function Find-Dotnet {
    if ($Dotnet) {
        if (-not (Test-Path -LiteralPath $Dotnet)) { throw "Не найден файл $Dotnet" }
        if (-not (Test-Sdk8 $Dotnet)) { throw "Для $Dotnet не найден .NET SDK 8 или новее (проверьте dotnet --list-sdks)" }
        return (Resolve-Path -LiteralPath $Dotnet).Path
    }
    $cands = @()
    if ($env:DOTNET_EXE) { $cands += $env:DOTNET_EXE }
    $cands += (Join-Path $PSScriptRoot '.dotnet\dotnet.exe')   # локальный SDK (dotnet-install.ps1 -InstallDir .dotnet)
    $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $cands += $cmd.Source }
    $cands += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    foreach ($c in $cands) { if ($c -and (Test-Path -LiteralPath $c) -and (Test-Sdk8 $c)) { return (Resolve-Path -LiteralPath $c).Path } }
    throw 'Не найден .NET SDK 8 или новее. Установите его (https://dotnet.microsoft.com/download/dotnet/8.0) или укажите путь: -Dotnet <путь к dotnet.exe>'
}

$dotnetExe = Find-Dotnet
$env:DOTNET_ROOT = Split-Path $dotnetExe
function Run([string[]]$a) { & $dotnetExe @a; if ($LASTEXITCODE -ne 0) { throw "dotnet $($a[0]) завершился с ошибкой ($LASTEXITCODE)" } }

if ($GameDir) {
    $GameDir = $GameDir.TrimEnd('\', '/')
    if (-not (Test-GameDir $GameDir)) { throw "В папке $GameDir нет SlimeRancher2.exe" }
} else {
    $GameDir = Find-GameDir
}
if ($GameDir) { Write-Host "[build] папка игры: $GameDir" -ForegroundColor DarkGray }
else { Write-Host '[build] папка Slime Rancher 2 не найдена (её можно указать: -GameDir)' -ForegroundColor Yellow }

$common = @('-c', 'Release', '-nologo', '-v', 'minimal', '-p:UseSharedCompilation=false')
$refsOk = [bool]($GameDir -and (Test-Path -LiteralPath (Join-Path $GameDir 'MelonLoader\net6\MelonLoader.dll')) -and
                 (Test-Path -LiteralPath (Join-Path $GameDir 'MelonLoader\Il2CppAssemblies\Il2Cppmscorlib.dll')))
if ($refsOk) {
    Write-Host '[build] плагин SR2ModManager.dll' -ForegroundColor Cyan
    Run (@('build', "$PSScriptRoot\Plugin\ModManagerPlugin.csproj", '-o', "$stage\plugin", "-p:GameDir=$GameDir") + $common)
} else {
    Write-Host '[build] плагин пропущен: нужны сборки MelonLoader из папки игры (установите MelonLoader 0.6+ и один раз запустите игру)' -ForegroundColor Yellow
}
Write-Host '[build] лаунчер SR2ModLauncher.exe' -ForegroundColor Cyan
Run (@('publish', "$PSScriptRoot\Launcher\SR2ModLauncher.csproj", '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true', '-o', "$stage\launcher") + $common)

if ($Test) {
    Write-Host '[test] тесты (на временных копиях, папка игры не меняется)' -ForegroundColor Cyan
    Run (@('build', "$PSScriptRoot\Tests\ModManager.Tests.csproj", '-o', "$stage\tests") + $common)
    $testArgs = @("$stage\tests\ModManager.Tests.dll")
    if ($GameDir -and (Test-Path -LiteralPath (Join-Path $GameDir 'Mods'))) { $testArgs += (Join-Path $GameDir 'Mods') }
    & $dotnetExe @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли' }
}

if ($Package) {
    # Установщик: обычное приложение .NET Framework 4 (есть в любой Windows 10/11), собирается штатным csc.exe.
    # Внутри два лаунчера: компактный (для ПК с .NET 8+) и со встроенной средой .NET — какой ставить, решает установщик.
    if (-not $refsOk) { throw 'Для установщика нужен плагин, а для него — папка игры с MelonLoader 0.6+: укажите -GameDir' }
    Write-Host '[package] лаунчер со встроенной средой .NET' -ForegroundColor Cyan
    Run (@('publish', "$PSScriptRoot\Launcher\SR2ModLauncher.csproj", '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true',
           '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:IntermediateOutputPath=obj\sc\', '-p:OutputPath=bin\sc\',
           '-o', "$stage\launcher-sc") + $common)
    $pkg = Join-Path $stage 'package'
    New-Item -ItemType Directory -Force $pkg | Out-Null
    $ver = ([regex]'<Version>([^<]+)</Version>').Match((Get-Content "$PSScriptRoot\Launcher\SR2ModLauncher.csproj" -Raw)).Groups[1].Value
    [IO.File]::WriteAllText("$pkg\version.txt", $ver, (New-Object Text.UTF8Encoding $false))
    $payload = [ordered]@{
        'SR2ModManager.dll'     = "$stage\plugin\SR2ModManager.dll"
        'SR2ModLauncher.exe'    = "$stage\launcher\SR2ModLauncher.exe"
        'SR2ModLauncher.sc.exe' = "$stage\launcher-sc\SR2ModLauncher.exe"
        'uninstall.ps1'         = "$PSScriptRoot\uninstall.ps1"
        'app.ico'               = "$PSScriptRoot\Launcher\app.ico"
        'version.txt'           = "$pkg\version.txt"
    }
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    if (-not (Test-Path $csc)) { throw 'Не найден csc.exe из .NET Framework 4' }
    $setup = Join-Path $stage 'SR2ModManager_Setup.exe'
    Write-Host '[package] установщик SR2ModManager_Setup.exe' -ForegroundColor Cyan
    $a = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/codepage:65001', "/out:$setup",
           "/win32icon:$PSScriptRoot\Launcher\app.ico", "/win32manifest:$PSScriptRoot\Installer\app.manifest",
           '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:Microsoft.CSharp.dll')
    foreach ($k in $payload.Keys) { $a += "/resource:$($payload[$k]),payload.$k" }
    $a += "$PSScriptRoot\Installer\Setup.cs"
    & $csc @a
    if ($LASTEXITCODE -ne 0) { throw 'Установщик не собрался' }
    Write-Host ('[package] {0} ({1:N1} МБ)' -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
}

if ($Install) {
    if (-not $refsOk) { throw 'Для установки нужна папка игры с MelonLoader 0.6+: укажите -GameDir' }
    $plugins = Join-Path $GameDir 'Plugins'
    $mm = Join-Path $GameDir 'ModManager'
    New-Item -ItemType Directory -Force $plugins, $mm | Out-Null
    $exe = Join-Path $mm 'SR2ModLauncher.exe'
    if (Get-Process -Name SR2ModLauncher -ErrorAction SilentlyContinue) { throw 'Окно SR2 Mod Manager открыто — закройте его перед установкой.' }

    # Плагин. Пока игра запущена, DLL занята, но её можно переименовать: игра доработает со старой
    # копией (SR2ModManager.dll.old удалит лаунчер позже), а при следующем запуске загрузится новая.
    # Если переименовать не вышло — новая версия ждёт в ModManager\update, её поставит лаунчер.
    $dst = Join-Path $plugins 'SR2ModManager.dll'
    $staged = Join-Path $mm 'update\SR2ModManager.dll'
    $new = "$stage\plugin\SR2ModManager.dll"
    if (Test-Path $dst) { Copy-Item $dst "$dst.bak" -Force }
    if (Get-Process -Name SlimeRancher2 -ErrorAction SilentlyContinue) {
        $old = "$dst.old"
        try {
            if (Test-Path $dst) {
                if (Test-Path $old) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
                if (Test-Path $old) { $old = "$dst.old-" + (Get-Date -Format 'yyyyMMddHHmmss') }
                [IO.File]::Move($dst, $old)
            }
            Copy-Item $new $dst -Force
            if (Test-Path $staged) { Remove-Item (Split-Path $staged) -Recurse -Force }
            Write-Host "[install] $dst — игра запущена: новая версия загрузится при следующем запуске игры" -ForegroundColor Green
        } catch {
            if (-not (Test-Path $dst) -and (Test-Path $old)) { [IO.File]::Move($old, $dst) }
            New-Item -ItemType Directory -Force (Split-Path $staged) | Out-Null
            Copy-Item $new $staged -Force
            Write-Host "[install] игра запущена: плагин подготовлен в $staged" -ForegroundColor Yellow
            Write-Host '          он встанет сам после закрытия игры (при запуске лаунчера или перезапуске из панели F5)' -ForegroundColor Yellow
        }
    } else {
        Copy-Item $new $dst -Force
        if (Test-Path $staged) { Remove-Item (Split-Path $staged) -Recurse -Force }
        Get-ChildItem $plugins -Filter 'SR2ModManager.dll.old*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        Write-Host "[install] $dst" -ForegroundColor Green
    }

    if (Test-Path $exe) { Copy-Item $exe "$exe.bak" -Force }
    Copy-Item "$stage\launcher\SR2ModLauncher.exe" $mm -Force
    Copy-Item "$PSScriptRoot\uninstall.ps1" $mm -Force
    Write-Host "[install] $exe" -ForegroundColor Green

    $desktop = [Environment]::GetFolderPath('Desktop')
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut((Join-Path $desktop 'SR2 Mod Manager.lnk'))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $mm
    $lnk.IconLocation = $exe + ',0'
    $lnk.Description = 'Slime Rancher 2 — выбрать моды и запустить игру'
    $lnk.Save()
    Write-Host "[install] ярлык: $desktop\SR2 Mod Manager.lnk" -ForegroundColor Green
}
Write-Host "[build] готово: $stage" -ForegroundColor Green
