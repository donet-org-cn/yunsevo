param(
    [Parameter(Mandatory=$true)][ValidateSet('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Runtime,
    [Parameter(Mandatory=$true)][string]$PublishRoot,
    [string]$Destination='artifacts/release'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish = [IO.Path]::GetFullPath($PublishRoot)
$destinationPath = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
$extension = if ($Runtime.StartsWith('win-')) { '.zip' } else { '.tar.gz' }

function New-RuntimeArchive([string]$Source, [string]$Target, [string]$EntryPattern) {
    $stage = Join-Path $destinationPath ('.stage-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    try {
        $files = @(Get-ChildItem -LiteralPath $Source -File -Recurse | Where-Object {
            $relative = $_.FullName.Substring($Source.Length + 1).Replace('\','/')
            $relative -notmatch '(^|/)(data|credentials|state|activity|downloads|[^/]+\.dSYM)(/|$)' -and
            ($relative -match '(^|/)Notices/' -or $_.Name -match $EntryPattern -or
             $_.Name -match '^(.*\.(dll|so(\.[0-9.]+)?|dylib|runtimeconfig\.json|deps\.json)|THIRD-PARTY-NOTICES\.md|MIGRATION\.md|restore-backup\.ps1)$')
        })
        if ($files.Count -eq 0) { throw "No release runtime files: $Source" }
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($Source.Length + 1)
            $targetFile = Join-Path $stage $relative
            New-Item -ItemType Directory -Path (Split-Path $targetFile) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $targetFile
        }
        if ($EntryPattern.Contains('Server')) {
            Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging/release/SERVER.md') -Destination (Join-Path $stage 'README.md')
            Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $stage
            New-Item -ItemType Directory -Path (Join-Path $stage 'downloads') -Force | Out-Null
            foreach ($prefix in @('yunsevo-drive', 'yunsevo-headless')) {
                Copy-Item -LiteralPath (Join-Path $publish "server/downloads/$prefix-$Runtime$extension") -Destination (Join-Path $stage 'downloads')
            }
        }
        if ($extension -eq '.zip') {
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $Target -Force
        } else {
            & tar -czf $Target -C $stage .
            if ($LASTEXITCODE -ne 0) { throw "Archive failed: $Target" }
        }
    } finally {
        $resolvedStage = [IO.Path]::GetFullPath($stage)
        if (!$resolvedStage.StartsWith($destinationPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw 'Unsafe staging directory' }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

New-RuntimeArchive (Join-Path $publish 'server') (Join-Path $destinationPath "yunsevo-server-$Runtime$extension") '^MyPan\.(Server|SmbGateway)(\.exe)?$'
foreach ($prefix in @('yunsevo-drive','yunsevo-headless')) {
    Copy-Item -LiteralPath (Join-Path $publish "server/downloads/$prefix-$Runtime$extension") -Destination $destinationPath -Force
}
if ($Runtime.StartsWith('win-')) {
    New-RuntimeArchive (Join-Path $publish 'windows-client') (Join-Path $destinationPath "yunsevo-legacy-client-$Runtime.zip") '^MyPan\.Client(\.exe)?$'
}
Get-ChildItem -LiteralPath $destinationPath -File | Where-Object { $_.Name -like "*-$Runtime$extension" } | Select-Object Name,Length
