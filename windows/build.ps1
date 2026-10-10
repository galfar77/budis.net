# Sestaví Budis Commander pro Windows (samostatná .exe bez nutnosti instalovat .NET).
# Použití v PowerShellu ve složce windows:   .\build.ps1          (x64)
#                                            .\build.ps1 arm64    (Windows ARM, např. Parallels na Apple Silicon)
# Vyžaduje .NET 8 SDK:  winget install Microsoft.DotNet.SDK.8
param([string]$Arch = "x64")

$ErrorActionPreference = "Stop"
$rid = "win-$Arch"
$out = Join-Path $PSScriptRoot "out\$rid"
dotnet publish "$PSScriptRoot\BudisCommander.Win\BudisCommander.Win.csproj" -c Release -r $rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out
Write-Host "Hotovo: $out\BudisCommander.exe"
