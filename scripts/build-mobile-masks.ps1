param([string]$Python='python', [string]$Output='artifacts/reconstruction-native/masks')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repo
try {
    $cache=Join-Path $repo '.test-data/mobile-mask-build'
    New-Item -ItemType Directory -Force $cache | Out-Null
    $commit='f706ad9c4eb7f219c00d9050e46328518ffb65d2'
    $weights=Join-Path $cache 'mobile_sam.pt'
    if (!(Test-Path -LiteralPath $weights)) {
        Invoke-WebRequest -Uri "https://raw.githubusercontent.com/ChaoningZhang/MobileSAM/$commit/weights/mobile_sam.pt" -OutFile ($weights+'.partial')
        Move-Item -LiteralPath ($weights+'.partial') -Destination $weights
    }
    if ((Get-FileHash -LiteralPath $weights -Algorithm SHA256).Hash.ToLowerInvariant() -ne '6dbb90523a35330fedd7f1d3dfc66f995213d81b29a5ca8108dbcdd4e37d6c2f') { throw 'MobileSAM weights checksum mismatch.' }
    $license=Join-Path $cache 'LICENSE-MobileSAM.txt'
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/ChaoningZhang/MobileSAM/$commit/LICENSE" -OutFile $license
    $venv=Join-Path $cache 'venv'
    if (!(Test-Path "$venv/Scripts/python.exe")) { & $Python -m venv $venv; if ($LASTEXITCODE) {throw 'Python 3.13 virtualenv creation failed.'} }
    $runner=Join-Path $venv 'Scripts/python.exe'
    & $runner -m pip install torch==2.14.1 torchvision==0.29.1 --index-url https://download.pytorch.org/whl/cpu
    if ($LASTEXITCODE) {throw 'CPU export dependencies failed.'}
    & $runner -m pip install timm==1.0.30 onnx==1.20.0 pillow==13.0.0 numpy==2.5.4 opencv-python-headless==5.0.0.93 piexif==1.1.3 "https://github.com/ChaoningZhang/MobileSAM/archive/$commit.zip"
    if ($LASTEXITCODE) {throw 'MobileSAM export dependencies failed.'}
    & $runner tools/reconstruction/export_mobile_masks.py $weights $Output --license $license
    if ($LASTEXITCODE) {throw 'Foreground model export failed.'}
    $expected=@{'encoder.onnx'='3827ec18dbc724715494955d132f934f896a568ff42d5ee2328aad1a0af895f7';'decoder.onnx'='cab1588bff5c6eaae85e73c012485f83affd74494e611ca062f40fbe73320116'}
    foreach ($file in $expected.Keys) { if ((Get-FileHash -LiteralPath (Join-Path $Output $file) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[$file]) {throw "Export differs from reviewed foreground model: $file"} }
    Write-Output "Verified foreground assets: $Output"
} finally { Pop-Location }
