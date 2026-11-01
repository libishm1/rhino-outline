# Builds dist/RhinoOutline.rhp and dist/RhinoOutline.rhi (a zip the Rhino installer understands).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
dotnet build "$root/src/RhinoOutline/RhinoOutline.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item "$root/src/RhinoOutline/bin/Release/RhinoOutline.rhp" $dist -Force

$zip = Join-Path $dist 'RhinoOutline.zip'
$rhi = Join-Path $dist 'RhinoOutline.rhi'
Remove-Item $zip, $rhi -ErrorAction SilentlyContinue
Compress-Archive -Path "$dist/RhinoOutline.rhp", "$root/LICENSE" -DestinationPath $zip
Move-Item $zip $rhi
Get-ChildItem $dist
