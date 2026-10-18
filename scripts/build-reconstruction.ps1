param(
    [ValidateSet('win-x64','android-arm64','android-x64')][string]$Runtime='win-x64',
    [string]$AndroidNdk,
    [string]$CMake
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repo
try {
    $deps=Join-Path $repo '.test-data/reconstruction'
    New-Item -ItemType Directory -Force $deps | Out-Null
    function Fetch([string]$Name,[string]$Url,[string]$Sha) {
        $path=Join-Path $deps $Name
        if (!(Test-Path -LiteralPath $path)) { Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile ($path+'.partial'); Move-Item -LiteralPath ($path+'.partial') -Destination $path }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha) { throw "Dependency checksum mismatch: $Name" }
        return $path
    }
    $shader=Fetch 'glslang.zip' 'https://github.com/KhronosGroup/glslang/releases/download/16.6.0/glslang-16.6.0-windows-x86_64-release.zip' '82bf434e69b9bb4829de7e2b4bc2c5e7a7861e53d66cf75e5cc70f5f694a8d9b'
    if (!(Test-Path "$deps/glslang/bin/glslang.exe")) { Expand-Archive -LiteralPath $shader -DestinationPath "$deps/glslang" }
    $headers=Fetch 'vulkan-headers.zip' 'https://github.com/KhronosGroup/Vulkan-Headers/archive/refs/tags/v1.3.280.zip' '0b8fc45aa970825b74ee588b233d7773fb12efe1df1f79e830e7c49d6c6fc8ce'
    if (!(Test-Path "$deps/Vulkan-Headers-1.3.280/include/vulkan/vulkan.h")) { Expand-Archive -LiteralPath $headers -DestinationPath $deps }
    if (!$CMake) {
        $command=Get-Command cmake -ErrorAction SilentlyContinue
        if ($command) { $CMake=$command.Source } else {
            $vs=& "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -property installationPath
            $CMake=Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
        }
    }
    if (!(Test-Path -LiteralPath $CMake)) { throw 'Install CMake and a C++ build toolchain.' }
    $arguments=@('-S','native/Yunsevo.Reconstruction',"-DVULKAN_INCLUDE_DIR=$deps/Vulkan-Headers-1.3.280/include","-DGLSLANG_VALIDATOR=$deps/glslang/bin/glslang.exe")
    $build=Join-Path $repo "artifacts/reconstruction-native/$Runtime-build"
    if ($Runtime -eq 'win-x64') {
        $sdk=Fetch 'opencv-4.13.0-windows.exe' 'https://github.com/opencv/opencv/releases/download/4.13.0/opencv-4.13.0-windows.exe' 'f0e98c302464d6860777a7015065e11b9b271b5394e6ba92663f0cf1fc303f2c'
        if (!(Test-Path "$deps/windows/opencv/build/OpenCVConfig.cmake")) { $p=Start-Process -FilePath $sdk -ArgumentList "-o$deps/windows",'-y' -WindowStyle Hidden -Wait -PassThru; if ($p.ExitCode) { throw 'OpenCV extraction failed.' } }
        $arguments+=@('-B',$build,'-A','x64',"-DOpenCV_DIR=$deps/windows/opencv/build")
        $destination=Join-Path $repo 'artifacts/reconstruction-native/win-x64'
    } else {
        $sdk=Fetch 'opencv-4.13.0-android-sdk.zip' 'https://github.com/opencv/opencv/releases/download/4.13.0/opencv-4.13.0-android-sdk.zip' 'edfda20fdf65d0bd45391d168ec5261dd30b600b00279c4d910d7f1c3e020f0f'
        if (!(Test-Path "$deps/android/OpenCV-android-sdk/sdk/native/jni/OpenCVConfig.cmake")) { Expand-Archive -LiteralPath $sdk -DestinationPath "$deps/android" }
        if (!$AndroidNdk) { $AndroidNdk=Join-Path $repo '.test-data/android/sdk/ndk/29.0.14206865' }
        if (!(Test-Path "$AndroidNdk/build/cmake/android.toolchain.cmake")) { throw 'Install Android NDK 29.0.14206865, or specify -AndroidNdk.' }
        $abi=if ($Runtime -eq 'android-arm64') { 'arm64-v8a' } else { 'x86_64' }
        $ninja=Join-Path (Split-Path (Split-Path (Split-Path $CMake))) 'Ninja/ninja.exe'
        if (!(Test-Path $ninja)) { $ninja=(Get-Command ninja -ErrorAction Stop).Source }
        $arguments+=@('-B',$build,'-G','Ninja',"-DCMAKE_MAKE_PROGRAM=$ninja","-DCMAKE_TOOLCHAIN_FILE=$AndroidNdk/build/cmake/android.toolchain.cmake","-DANDROID_ABI=$abi",'-DANDROID_PLATFORM=android-26','-DANDROID_STL=c++_shared',"-DOpenCV_DIR=$deps/android/OpenCV-android-sdk/sdk/native/jni",'-DCMAKE_BUILD_TYPE=Release')
        $destination=Join-Path $repo "artifacts/reconstruction-native/android/$Runtime"
    }
    & $CMake @arguments
    if ($LASTEXITCODE) { throw 'Native reconstruction configure failed.' }
    & $CMake --build $build --config Release --parallel 4
    if ($LASTEXITCODE) { throw 'Native reconstruction build failed.' }
    New-Item -ItemType Directory -Force $destination | Out-Null
    if ($Runtime -eq 'win-x64') {
        Copy-Item -LiteralPath "$build/Release/yunsevo-reconstruct.exe","$build/Release/yunsevo_reconstruction.dll","$deps/windows/opencv/build/x64/vc16/bin/opencv_world4130.dll" -Destination $destination -Force
    } else {
        Copy-Item -LiteralPath "$build/libyunsevo_reconstruction.so" -Destination $destination -Force
        $triple=if ($Runtime -eq 'android-arm64') { 'aarch64-linux-android' } else { 'x86_64-linux-android' }
        Copy-Item -LiteralPath "$AndroidNdk/toolchains/llvm/prebuilt/windows-x86_64/sysroot/usr/lib/$triple/libc++_shared.so" -Destination $destination -Force
        & "$AndroidNdk/toolchains/llvm/prebuilt/windows-x86_64/bin/llvm-strip.exe" --strip-debug "$destination/libyunsevo_reconstruction.so"
        if ($LASTEXITCODE) { throw 'Native strip failed.' }
    }
    $notices=Join-Path $destination 'Notices'; New-Item -ItemType Directory -Force $notices | Out-Null
    Copy-Item -LiteralPath "$deps/Vulkan-Headers-1.3.280/LICENSE.md" -Destination "$notices/Vulkan-Headers-LICENSE.md" -Force
    $opencvLicense=if ($Runtime -eq 'win-x64') { "$deps/windows/opencv/build/LICENSE" } else { "$deps/android/OpenCV-android-sdk/LICENSE" }
    Copy-Item -LiteralPath $opencvLicense -Destination "$notices/OpenCV-LICENSE" -Force
    $sdkLicenses=if ($Runtime -eq 'win-x64') { "$deps/windows/opencv/build/etc/licenses" } else { "$deps/android/OpenCV-android-sdk/sdk/etc/licenses" }
    Copy-Item -LiteralPath $sdkLicenses -Destination (Join-Path $notices 'OpenCV-sdk-licenses') -Recurse -Force
    $sources=Fetch 'opencv-sources.zip' 'https://github.com/opencv/opencv/archive/refs/tags/4.13.0.zip' '431cb489d275da26526581c6735d7d7c0b00f190fe940b3217d386b77aef52c7'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=[IO.Compression.ZipFile]::OpenRead($sources)
    try {
        foreach ($entry in $archive.Entries) {
            if (!$entry.FullName.StartsWith('opencv-4.13.0/3rdparty/') -or $entry.Name -notmatch '^(LICENSE|COPYING|COPYRIGHT|NOTICE|README)') { continue }
            $relative=$entry.FullName.Substring('opencv-4.13.0/3rdparty/'.Length)
            $target=Join-Path $notices ('OpenCV-third-party/'+$relative)
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$true)
        }
    } finally { $archive.Dispose() }
    if ($Runtime -ne 'win-x64') {
        foreach ($name in @('NOTICE','NOTICE.toolchain')) { if (Test-Path "$AndroidNdk/$name") { Copy-Item -LiteralPath "$AndroidNdk/$name" -Destination "$notices/Android-NDK-$name" -Force } }
    }
    Get-ChildItem -LiteralPath $destination -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$_.Name } | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding ASCII
    Write-Output "Reconstruction runtime: $destination"
} finally { Pop-Location }
