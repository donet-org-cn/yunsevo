param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime,
    [string]$Output = 'artifacts/v2'
)
$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath($Output)
$downloads = Join-Path $destination 'server/downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($kind in @('desktop', 'headless')) {
    $source = Join-Path $destination $kind
    if (!(Test-Path -LiteralPath $source -PathType Container)) { throw "Missing published client: $source" }
    $prefix = if ($kind -eq 'desktop') { 'yunsevo-drive' } else { 'yunsevo-headless' }
    $extension = if ($Runtime.StartsWith('win-')) { '.zip' } else { '.tar.gz' }
    $target = Join-Path $downloads "$prefix-$Runtime$extension"
    $temporary = "$target.partial"
    $archiveRoot = $source
    if ($Runtime.StartsWith('osx-') -and $kind -eq 'desktop') { $archiveRoot = $destination; $source = Join-Path $destination 'Yunsevo.app' }
    $files = @(Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object {
        $relative = $_.FullName.Substring($source.Length + 1).Replace('\', '/')
        # Only runtime files and release notices; never settings, credentials or debug symbols.
        $relative -notmatch '(^|/)(data|credentials|state|activity|[^/]+\.dSYM)(/|$)' -and
        ($relative -match '(^|/)Notices/' -or $_.Name -match '^(Yunsevo\.(Desktop|Headless)(\.exe)?|README\.md|client\.example\.json|client\.env\.example|yunsevo-sync\.service|yunsevo\.desktop|THIRD-PARTY-NOTICES\.md|.*\.(dll|so(\.[0-9.]+)?|dylib|plist|icns|runtimeconfig\.json|deps\.json))$')
    })
    if ($files.Count -eq 0) { throw "No runtime files in $source" }
    try {
        if ($extension -eq '.zip') {
            $stream = [IO.File]::Open($temporary, [IO.FileMode]::Create)
            try {
                $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
                try {
                    foreach ($file in $files) {
                        $relative = $file.FullName.Substring($archiveRoot.Length + 1).Replace('\', '/')
                        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
                    }
                } finally { $zip.Dispose() }
            } finally { $stream.Dispose() }
        } else {
            $manifest = "$target.files"
            [IO.File]::WriteAllLines($manifest, @($files | ForEach-Object { $_.FullName.Substring($archiveRoot.Length + 1).Replace('\', '/') }), (New-Object Text.UTF8Encoding($false)))
            try { & tar -czf $temporary -C $archiveRoot -T $manifest; if ($LASTEXITCODE -ne 0) { throw 'Client archive failed.' } }
            finally { Remove-Item -LiteralPath $manifest -ErrorAction SilentlyContinue }
        }
        Move-Item -LiteralPath $temporary -Destination $target -Force
        Write-Host "Client download: $target"
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
