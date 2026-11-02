# Builds dist/RhinoOutline.rhp and dist/RhinoOutline.rhi (a zip the Rhino installer understands),
# together with ONNX Runtime, which Detect=AI and OutlineClick use.
param([string]$Dist = 'dist')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
dotnet build "$root/src/RhinoOutline/RhinoOutline.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$dist = Join-Path $root $Dist
New-Item -ItemType Directory -Force $dist | Out-Null
$bin = "$root/src/RhinoOutline/bin/Release"
$files = @('RhinoOutline.rhp', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll', 'Microsoft.ML.OnnxRuntime.dll',
           'System.Memory.dll', 'System.Buffers.dll', 'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll')
$files | ForEach-Object { Copy-Item "$bin/$_" $dist -Force }

$zip = Join-Path $dist 'RhinoOutline.zip'
$rhi = Join-Path $dist 'RhinoOutline.rhi'
Remove-Item $zip, $rhi -ErrorAction SilentlyContinue
Compress-Archive -Path (($files | ForEach-Object { "$dist/$_" }) + "$root/LICENSE") -DestinationPath $zip
Move-Item $zip $rhi
Get-ChildItem $dist
