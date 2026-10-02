<#
  Builds a single, self-contained ReelKeep.exe in .\dist (no .NET install needed on the target PC).
  Usage:  powershell -ExecutionPolicy Bypass -File build\publish.ps1 [-Version 1.2.3]
#>
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'dist'
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue

$publishArgs = @(
  'publish', (Join-Path $root 'src\ReelKeep\ReelKeep.csproj'),
  '-c', 'Release', '-r', 'win-x64',
  '--self-contained', 'true',
  '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:EnableCompressionInSingleFile=true',
  '-p:DebugType=None',
  '-o', $out
)
if ($Version) { $publishArgs += "-p:Version=$Version" }   # e.g. from the release tag
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Get-ChildItem $out -Filter *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue
$exe = Join-Path $out 'ReelKeep.exe'
"{0}  ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)
