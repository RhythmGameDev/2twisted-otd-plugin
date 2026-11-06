# Builds the 2Twisted output mode and writes dist/TwoTwistedPen.zip, ready for OpenTabletDriver's
# Plugin Manager -> Install plugin from file. The plugin folder holds TwoTwistedPen.dll only: OpenTabletDriver
# supplies its own plugin library.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
dotnet build "$root/src/TwoTwistedPen.csproj" -c Release -nologo -v:q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist 'TwoTwistedPen.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$root/src/bin/Release/net6.0/TwoTwistedPen.dll" -DestinationPath $zip
Write-Output "wrote $zip"
