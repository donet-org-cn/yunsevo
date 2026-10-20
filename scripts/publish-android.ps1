param(
    [ValidateSet('android-arm64','android-x64')][string]$Runtime='android-arm64',
    [string]$AndroidSdk=$env:ANDROID_HOME,
    [string]$JavaSdk=$env:JAVA_HOME,
    [string]$Output='artifacts/mobile',
    [string]$SigningKeyStore,
    [string]$SigningKeyAlias
)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    $destination=[IO.Path]::GetFullPath((Join-Path $Output $Runtime))
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $properties=@()
    if ($AndroidSdk) { $properties += "-p:AndroidSdkDirectory=$AndroidSdk" }
    if ($JavaSdk) { $properties += "-p:JavaSdkDirectory=$JavaSdk" }
    if ($SigningKeyStore) {
        if (!$SigningKeyAlias -or !$env:YUNSEVO_ANDROID_STORE_PASSWORD -or !$env:YUNSEVO_ANDROID_KEY_PASSWORD) {
            throw 'Set SigningKeyAlias, YUNSEVO_ANDROID_STORE_PASSWORD and YUNSEVO_ANDROID_KEY_PASSWORD for release signing.'
        }
        $properties += @('-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$SigningKeyStore", "-p:AndroidSigningKeyAlias=$SigningKeyAlias",
            '-p:AndroidSigningStorePass=env:YUNSEVO_ANDROID_STORE_PASSWORD','-p:AndroidSigningKeyPass=env:YUNSEVO_ANDROID_KEY_PASSWORD')
    }
    dotnet publish src/Yunsevo.Mobile.Android -c Release -r $Runtime -o $destination --configfile NuGet.Config @properties
    if ($LASTEXITCODE -ne 0) { throw 'Android Mono AOT publish failed.' }
    $signed=Get-ChildItem -LiteralPath $destination -Filter '*-Signed.apk' | Select-Object -First 1
    if (!$signed) { throw 'Signed Android APK was not produced.' }
    $target=Join-Path $destination "yunsevo-$Runtime.apk"
    Copy-Item -LiteralPath $signed.FullName -Destination $target -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($target)
    try {
        $abi=if ($Runtime -eq 'android-arm64') { 'arm64-v8a' } else { 'x86_64' }
        if (!$zip.GetEntry('assets/model-viewer/model-viewer.min.js')) { throw 'Missing offline model viewer.' }
        if ($zip.Entries | Where-Object { $_.FullName -match 'onnxruntime|reconstruction-masks|libyunsevo_reconstruction' }) { throw 'Retired generation runtime is still packaged.' }
        foreach ($assembly in @('Yunsevo.Mobile.Android','Yunsevo.Mobile.Core','Yunsevo.Sync','Yunsevo.Contracts')) {
            $entry=$zip.GetEntry("lib/$abi/libaot-$assembly.dll.so")
            if (!$entry -or $entry.Length -lt 1000) { throw "Missing AOT native library: $assembly ($Runtime)" }
        }
    } finally { $zip.Dispose() }
    & (Join-Path $PSScriptRoot 'verify-android-package.ps1') -Runtime $Runtime -Directory $destination -AndroidSdk $AndroidSdk -JavaSdk $JavaSdk -WriteReport
    Copy-Item -LiteralPath docs/mobile.md -Destination (Join-Path $destination 'README.md')
    $screenshots=Join-Path $destination 'screenshots'
    New-Item -ItemType Directory -Path $screenshots -Force | Out-Null
    Get-ChildItem -LiteralPath docs/screenshots -Filter 'mobile-*.png' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $screenshots -Force
    }
    $hash=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $destination 'SHA256SUMS.txt'), "$hash  yunsevo-$Runtime.apk" + [Environment]::NewLine)
    Write-Output "Android APK: $target"
    Write-Output 'Android uses Mono AOT with AndroidEnableProfiledAot=false. This is distinct from desktop/server Native AOT.'
    if (!$SigningKeyStore) { Write-Output 'The default .NET Android development key was used; use your retained signing key for production distribution.' }
} finally { Pop-Location }
