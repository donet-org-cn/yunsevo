param()
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $projectRoot 'artifacts\v2-update'
$destination = Join-Path $projectRoot 'artifacts\v2'
$packages = @('server', 'desktop', 'headless', 'windows-client')
foreach ($package in $packages) {
    if (!(Test-Path -LiteralPath (Join-Path $source $package))) { throw "Missing update package: $package" }
}
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count -gt 0) {
    $names = ($running | ForEach-Object { $_.ProcessName + ' (PID ' + $_.Id + ')' }) -join ', '
    throw "Exit the current Yunsevo client from its tray menu and stop the server before installing. Running: $names"
}
$files = @()
foreach ($package in $packages) {
    $base = Join-Path $source $package
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File) {
        # Credentials and application data belong to the installation, not the update package.
        if ($file.Name -in @('api-key.txt','server-settings.json','setup-code.txt') -or $file.Extension -eq '.lock' -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        $relative = $file.FullName.Substring($source.Length + 1)
        $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
        if (!$target.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Update path escaped its destination.' }
        if (Test-Path -LiteralPath $target) {
            for ($attempt = 0; $attempt -lt 25; $attempt++) {
                try {
                    $lease = [IO.File]::Open($target, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                    $lease.Dispose()
                    break
                }
                catch [IO.IOException] {
                    if ($attempt -eq 24) { throw }
                    Start-Sleep -Milliseconds 200
                }
            }
        }
        $files += [pscustomobject]@{Source=$file.FullName; Target=$target; Relative=$relative}
    }
}
$backup = Join-Path $projectRoot ('artifacts\v2-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
foreach ($file in $files) {
    if (Test-Path -LiteralPath $file.Target) {
        $old = Join-Path $backup $file.Relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $old) -Force | Out-Null
        Copy-Item -LiteralPath $file.Target -Destination $old
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $file.Target) -Force | Out-Null
    Copy-Item -LiteralPath $file.Source -Destination $file.Target -Force
}
$verification = Join-Path $source 'verification.json'
if (Test-Path -LiteralPath $verification) {
    $report = Get-Content -Raw -Encoding UTF8 -LiteralPath $verification | ConvertFrom-Json
    foreach ($binary in $report.Binaries) {
        if ($binary.Path.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $binary.Path = Join-Path $destination $binary.Path.Substring($source.Length + 1)
        }
    }
    if ($report.SmbGateway -and $report.SmbGateway.Path.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
        $report.SmbGateway.Path = Join-Path $destination $report.SmbGateway.Path.Substring($source.Length + 1)
    }
    $json = $report | ConvertTo-Json -Depth 16
    [IO.File]::WriteAllText((Join-Path $destination 'verification.json'), $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $projectRoot 'artifacts\verification.json'), $json, [Text.UTF8Encoding]::new($false))
}
Write-Host "Updated Yunsevo. Previous package files: $backup"
Write-Host 'The server data, API key and client configuration were preserved.'
