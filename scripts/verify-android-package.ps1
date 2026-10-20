param(
    [Parameter(Mandatory=$true)][ValidateSet('android-arm64','android-x64')][string]$Runtime,
    [Parameter(Mandatory=$true)][string]$Directory,
    [Parameter(Mandatory=$true)][string]$AndroidSdk,
    [string]$JavaSdk=$env:JAVA_HOME,
    [string]$ExpectedCommit,
    [string]$ExpectedCertificate,
    [switch]$WriteReport
)
$ErrorActionPreference='Stop'
$apk=Join-Path ([IO.Path]::GetFullPath($Directory)) "yunsevo-$Runtime.apk"
$abi=if($Runtime -eq 'android-arm64'){'arm64-v8a'}else{'x86_64'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($apk)
try {
    if(!$zip.GetEntry('assets/model-viewer/model-viewer.min.js')){throw 'Missing offline model viewer.'}
    if($zip.Entries | Where-Object {$_.FullName -match 'onnxruntime|reconstruction-masks|libyunsevo_reconstruction'}){throw 'Retired generation runtime is packaged.'}
    foreach($assembly in @('Yunsevo.Mobile.Android','Yunsevo.Mobile.Core','Yunsevo.Sync','Yunsevo.Contracts')){
        $entry=$zip.GetEntry("lib/$abi/libaot-$assembly.dll.so")
        if(!$entry -or $entry.Length -lt 1000){throw "Missing Mono AOT library: $assembly"}
    }
} finally {$zip.Dispose()}
$buildTools=Join-Path $AndroidSdk 'build-tools/36.0.0'
$signer=Join-Path $buildTools $(if($env:OS -eq 'Windows_NT'){'apksigner.bat'}else{'apksigner'})
$aapt=Join-Path $buildTools $(if($env:OS -eq 'Windows_NT'){'aapt2.exe'}else{'aapt2'})
$previousJava=$env:JAVA_HOME
try {
    if($JavaSdk){$env:JAVA_HOME=$JavaSdk}
    $certificate=& $signer verify --print-certs $apk
    if($LASTEXITCODE -ne 0){throw 'APK signature verification failed.'}
    $badging=& $aapt dump badging $apk
    if($LASTEXITCODE -ne 0){throw 'APK manifest cannot be read.'}
} finally {$env:JAVA_HOME=$previousJava}
$fingerprint=($certificate | Select-String 'Signer #1 certificate SHA-256 digest: (.+)').Matches[0].Groups[1].Value.Trim()
if($fingerprint -notmatch '^[0-9a-f]{64}$'){throw 'APK signing certificate fingerprint is missing.'}
if($ExpectedCertificate -and $fingerprint -ne $ExpectedCertificate){throw 'Retained Android update signing certificate does not match.'}
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project=[xml](Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'src/Yunsevo.Mobile.Android/Yunsevo.Mobile.Android.csproj'))
$version=[string]$project.Project.PropertyGroup.ApplicationDisplayVersion
$versionCode=[int]$project.Project.PropertyGroup.ApplicationVersion
if(!($badging | Select-String -SimpleMatch "package: name='app.yunsevo.drive' versionCode='$versionCode' versionName='$version'")){throw 'APK package or version does not match the release source.'}
$sha256=(Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant()
$reportPath=Join-Path (Split-Path -Parent $apk) "verification-$Runtime.json"
if($WriteReport){
    $commit=(git -C $projectRoot rev-parse HEAD)
    if($LASTEXITCODE -ne 0){throw 'Source commit unavailable.'}
    $report=@{rid=$Runtime;commit=$commit;version=$version;versionCode=$versionCode;sha256=$sha256;certificateSha256=$fingerprint
        checks=@('Mono AOT libraries present','Offline viewer present','Retired generation runtime absent','Manifest version verified','APK signature verified')
        signing=$(if($fingerprint -eq '5ed85003849d1b286d14505eeea10a004744588a029b0e02741bb7c4134f7c0c'){'Retained internal-test certificate; not a Play Store production release'}else{'Custom signing certificate; distribution classification is not verified'})}
    [IO.File]::WriteAllText($reportPath,($report | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
}
if($ExpectedCommit){
    $report=Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    if($report.commit -ne $ExpectedCommit -or $report.sha256 -ne $sha256 -or $report.certificateSha256 -ne $fingerprint -or $report.rid -ne $Runtime -or $report.version -ne $version -or $report.versionCode -ne $versionCode){throw 'Android build record does not match this release.'}
}
Write-Output "Verified $Runtime $version (code $versionCode), APK signature, Mono AOT and offline model viewer."
