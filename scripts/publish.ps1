param(
    [ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime = 'win-x64',
    [string]$Output = 'artifacts/v2'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
Push-Location $projectRoot
try {
    $destination = [IO.Path]::GetFullPath($Output)
    foreach ($item in @(@('src/Yunsevo.Server','server'), @('src/Yunsevo.Desktop','desktop'), @('src/Yunsevo.Headless','headless'))) {
        dotnet publish $item[0] -c Release -r $Runtime -o (Join-Path $destination $item[1]) --configfile NuGet.Config
        if ($LASTEXITCODE -ne 0) { throw ($item[1] + ' Native AOT publish failed.') }
    }
    dotnet publish src/Yunsevo.SmbGateway -c Release -r $Runtime --self-contained true -p:PublishAot=false -o (Join-Path $destination 'server/smb') --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'SMB gateway publish failed.' }
    if ($Runtime.StartsWith('win-')) {
        dotnet publish src/Yunsevo.Client -c Release -r $Runtime -o (Join-Path $destination 'windows-client') --configfile NuGet.Config
        if ($LASTEXITCODE -ne 0) { throw 'Legacy Windows AOT client publish failed.' }
    }
    if ($Runtime.StartsWith('osx-')) {
        $bundle = Join-Path $destination 'Yunsevo.app/Contents'
        New-Item -ItemType Directory -Force (Join-Path $bundle 'MacOS') | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $destination 'desktop') | Copy-Item -Destination (Join-Path $bundle 'MacOS') -Recurse -Force
        Copy-Item -LiteralPath packaging/macos/Info.plist -Destination (Join-Path $bundle 'Info.plist')
    }
    Copy-Item -LiteralPath packaging/release/CLIENT.md -Destination (Join-Path $destination 'desktop/README.md')
    Copy-Item -LiteralPath packaging/release/HEADLESS.md -Destination (Join-Path $destination 'headless/README.md')
    Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination (Join-Path $destination 'headless')
    Copy-Item -LiteralPath packaging/linux/client.example.json,packaging/linux/client.env.example,packaging/linux/yunsevo-sync.service -Destination (Join-Path $destination 'headless')
    if ($Runtime.StartsWith('linux-')) { Copy-Item -LiteralPath packaging/linux/yunsevo.desktop -Destination (Join-Path $destination 'desktop') }
    if ($Runtime.StartsWith('osx-')) { Copy-Item -LiteralPath packaging/release/CLIENT.md -Destination (Join-Path $destination 'Yunsevo.app/Contents/MacOS/README.md') }
    & (Join-Path $PSScriptRoot 'package-clients.ps1') -Runtime $Runtime -Output $destination
    Write-Host "Published to $destination ($Runtime). Server/desktop/headless: Native AOT. SMB gateway: self-contained managed .NET."
}
finally { Pop-Location }
