<#
.SYNOPSIS
  Builds QuickMail and packages it as an MSI.
.EXAMPLE
  ./build.ps1 -Version 1.2.0
  # -> artifacts/QuickMail-1.2.0-x64.msi
#>
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$publish = Join-Path $root "artifacts/publish"
$out = Join-Path $root "artifacts"

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish "$root/src/QuickMail/QuickMail.csproj" -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

dotnet build "$root/installer/QuickMail.Installer.wixproj" -c Release `
    -p:Version=$Version -p:AppPublishDir="$publish/" -o $out
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

Get-ChildItem $out -Filter *.msi | ForEach-Object { "Built $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
