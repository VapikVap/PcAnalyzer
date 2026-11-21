# Собирает самодостаточную версию приложения (на ПК друга не нужен .NET) и установщик.
# Результат: artifacts\PcAnalyzer-Setup-<версия>.exe
#
#   powershell -ExecutionPolicy Bypass -File .\build-installer.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'Version not found in Directory.Build.props' }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 не найден. Установите: winget install JRSoftware.InnoSetup' }

Write-Host "== Publish $version (self-contained, win-x64)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\PcAnalyzer.App\PcAnalyzer.App.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# PresentMon (MIT, Intel) кладём рядом с приложением: PresentMonProvider находит его в <app>\PresentMon.
$pm = Get-ChildItem (Join-Path $root 'tools\PresentMon') -Filter 'PresentMon*.exe' | Select-Object -First 1
if ($pm) {
    $pmDir = New-Item -ItemType Directory -Force (Join-Path $publish 'PresentMon')
    Copy-Item $pm.FullName $pmDir
    Set-Content -Encoding utf8 (Join-Path $pmDir 'README.txt') @'
PresentMon — Intel Corporation, лицензия MIT.
https://github.com/GameTechDev/PresentMon
Используется PC Performance Analyzer для записи FPS и времени кадра. Запускать вручную не нужно.
'@
} else {
    Write-Warning 'tools\PresentMon\PresentMon*.exe не найден — установщик будет без записи FPS.'
}

Write-Host "== Installer"
& $iscc "/DAppVersion=$version" "/DSourceDir=$publish" (Join-Path $root 'installer\PcAnalyzer.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

$setup = Join-Path $root "artifacts\PcAnalyzer-Setup-$version.exe"
Write-Host "`nГотово: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) МБ)"
