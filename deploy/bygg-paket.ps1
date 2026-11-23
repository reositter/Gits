<#
.SYNOPSIS
  Bygger installationspaketet: dist\Inventering med app\, app\AdkBridge\ och installera.ps1.
.DESCRIPTION
  Kräver .NET 8 SDK. ADK-filerna tas från lib\ om de finns där; annars frågar installationen efter dem.
#>
param([string]$Ut = (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist') 'Inventering'))
$ErrorActionPreference = 'Stop'
$rot = Split-Path $PSScriptRoot -Parent
if (Test-Path $Ut) { Remove-Item $Ut -Recurse -Force }
$app = Join-Path $Ut 'app'

dotnet publish (Join-Path (Join-Path $rot 'src') 'Inventering.Web') -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o $app
if ($LASTEXITCODE) { throw 'Bygget av webbtjänsten misslyckades.' }

$lib = Join-Path $rot 'lib'
if ((Test-Path (Join-Path $lib 'AvSynkLib.dll')) -and (Test-Path (Join-Path $lib 'AdkNet4Wrapper.dll'))) {
    dotnet publish (Join-Path (Join-Path $rot 'src') 'Inventering.AdkBridge') -c Release -o (Join-Path $app 'AdkBridge')
    if ($LASTEXITCODE) { throw 'Bygget av ADK-bryggan misslyckades.' }
} else {
    Write-Warning 'lib\AvSynkLib.dll och lib\AdkNet4Wrapper.dll saknas – ADK-bryggan byggs inte. Kopiera dem till lib\ och bygg igen.'
}

# Installationsskriptet sparas med BOM så att åäö visas rätt i Windows PowerShell 5.1.
$skript = Get-Content (Join-Path $PSScriptRoot 'installera.ps1') -Raw -Encoding UTF8
[System.IO.File]::WriteAllText((Join-Path $Ut 'installera.ps1'), $skript, (New-Object System.Text.UTF8Encoding($true)))
Copy-Item (Join-Path $PSScriptRoot 'LÄS MIG.txt') $Ut
Copy-Item (Join-Path $PSScriptRoot 'Installera.cmd') $Ut
Copy-Item (Join-Path $PSScriptRoot 'Byt certifikat.cmd') $Ut
Write-Host "Paketet finns i $Ut" -ForegroundColor Green
