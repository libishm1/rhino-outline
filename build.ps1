# Builds the plug-in and its installers.
#
#   build.ps1                         dist\RhinoOutline.rhp + .rhi, networks downloaded on first use
#   build.ps1 -Models <folder>        AI edition: also bundles the networks from <folder>
#                                     (u2netp, IS-Net, MobileSAM) and builds a Yak package
#   -Dist <folder>                    output folder (default dist)
#
# The .rhi is a zip the Rhino installer understands; the .yak is for the Rhino package manager
# and food4Rhino. Both contain the same files.
param([string]$Dist = 'dist', [string]$Models = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
dotnet build "$root/src/RhinoOutline/RhinoOutline.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }
[xml]$proj = Get-Content "$root/src/RhinoOutline/RhinoOutline.csproj"
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

$dist = Join-Path $root $Dist
$stage = Join-Path $dist 'package'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
$bin = "$root/src/RhinoOutline/bin/Release"
$files = @('RhinoOutline.rhp', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll', 'Microsoft.ML.OnnxRuntime.dll',
           'System.Memory.dll', 'System.Buffers.dll', 'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll')
$files | ForEach-Object { Copy-Item "$bin/$_" $stage -Force }
Copy-Item "$root/LICENSE" $stage

$licenses = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
$ort = Get-ChildItem "$env:USERPROFILE/.nuget/packages/microsoft.ml.onnxruntime" | Sort-Object Name | Select-Object -Last 1
Copy-Item "$($ort.FullName)/LICENSE" "$licenses/onnxruntime-LICENSE.txt"
Copy-Item "$($ort.FullName)/ThirdPartyNotices.txt" "$licenses/onnxruntime-ThirdPartyNotices.txt"

$edition = ''
if ($Models) {
  $edition = '-AI'
  $modelDir = Join-Path $stage 'models'
  New-Item -ItemType Directory -Force $modelDir | Out-Null
  'u2netp.onnx', 'isnet-general-use.onnx', 'mobile_sam_image_encoder.onnx', 'sam_mask_decoder_multi.onnx' |
    ForEach-Object { Copy-Item (Join-Path $Models $_) $modelDir }
  Copy-Item "$root/research/segmentation/licenses/Apache-2.0.txt" $licenses
  Copy-Item "$root/research/segmentation/licenses/MODELS.txt" "$licenses/MODELS.txt"
}

$name = "RhinoOutline$edition-$version"
$rhi = Join-Path $dist "$name.rhi"
$zip = Join-Path $dist "$name.zip"
Remove-Item $zip, $rhi -ErrorAction SilentlyContinue
# tar.exe (Windows 10 and later), not Compress-Archive: Windows PowerShell writes backslashes
# into the paths of a zip, which breaks the models folder
Push-Location $stage
& "$env:SystemRoot\System32\tar.exe" -a -c -f $zip *
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "zip failed" }
Pop-Location
Move-Item $zip $rhi

if ($Models) {
  # Yak package for the Rhino package manager and food4Rhino
  $manifest = @"
name: rhino-outline-ai
version: $version
authors:
- Libish Murugesan
description: >
  Rhino Outline AI. Turns a photo or image into a closed outline curve. Finds the object with
  neural segmentation (IS-Net, U2-Net small) or with one click (MobileSAM, a small Segment
  Anything model), ignores cast shadows, then traces it with Potrace and wraps it with an alpha
  shape. Commands Outline, OutlinePicture and OutlineClick. Runs on the CPU, offline.
url: https://github.com/libishm1/rhino-outline
keywords:
- outline
- image
- trace
- vectorize
- potrace
- alpha shape
- segmentation
- background removal
- sam
- ai
"@
  Set-Content -Path (Join-Path $stage 'manifest.yml') -Value $manifest -Encoding UTF8
  Get-ChildItem $dist -Filter '*.yak' | Remove-Item
  Push-Location $stage
  & "C:\Program Files\Rhino 8\System\Yak.exe" build --platform win
  if ($LASTEXITCODE -ne 0) { Pop-Location; throw "yak build failed" }
  Get-ChildItem -Filter '*.yak' | Move-Item -Destination $dist -Force
  Remove-Item 'manifest.yml'
  Pop-Location
}
else {
  Copy-Item "$stage/RhinoOutline.rhp" $dist -Force
}
Get-ChildItem $dist -File
