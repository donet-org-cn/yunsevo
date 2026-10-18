$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $projectRoot ('.test-data\installer-' + (Get-Date -Format 'yyyyMMddHHmmssfffffff'))
$scriptDir = Join-Path $fixture 'scripts'
New-Item -ItemType Directory -Path $scriptDir -Force | Out-Null
$installer = Join-Path $scriptDir 'install-update.ps1'
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\install-update.ps1') -Destination $installer
$packages = @('server','desktop','headless','windows-client')
foreach ($package in $packages) {
    $old = Join-Path $fixture ('artifacts\v2\' + $package)
    $new = Join-Path $fixture ('artifacts\v2-update\' + $package)
    New-Item -ItemType Directory -Path $old,$new -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $old 'package.exe'), 'old-' + $package)
    [IO.File]::WriteAllText((Join-Path $new 'package.exe'), 'new-' + $package)
}
$oldServer = Join-Path $fixture 'artifacts\v2\server'
[IO.File]::WriteAllText((Join-Path $oldServer 'api-key.txt'), 'fixture-key-preserve')
[IO.File]::WriteAllText((Join-Path $fixture 'artifacts\v2-update\server\api-key.txt'), 'fixture-key-must-not-install')
foreach ($name in @('server-settings.json','setup-code.txt','server-settings.json.lock')) {
    [IO.File]::WriteAllText((Join-Path $oldServer $name), 'installed-state-preserve')
    [IO.File]::WriteAllText((Join-Path $fixture ('artifacts\v2-update\server\' + $name)), 'build-state-must-not-install')
}
New-Item -ItemType Directory -Path (Join-Path $oldServer 'data') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $oldServer 'data\catalog.json'), 'fixture-cloud-data')
$lock = [IO.File]::Open((Join-Path $fixture 'artifacts\v2\desktop\package.exe'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $blocked = Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $installer + '"')) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $fixture 'blocked.out') -RedirectStandardError (Join-Path $fixture 'blocked.err')
    if ($blocked.ExitCode -eq 0) { throw 'Installer ignored a locked package.' }
}
finally { $lock.Dispose() }
foreach ($package in $packages) {
    if ([IO.File]::ReadAllText((Join-Path $fixture ('artifacts\v2\' + $package + '\package.exe'))) -ne ('old-' + $package)) { throw 'A failed preflight changed a package.' }
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer
if ($LASTEXITCODE -ne 0) { throw 'Fixture install failed.' }
foreach ($package in $packages) {
    if ([IO.File]::ReadAllText((Join-Path $fixture ('artifacts\v2\' + $package + '\package.exe'))) -ne ('new-' + $package)) { throw 'Package was not installed.' }
}
if ([IO.File]::ReadAllText((Join-Path $oldServer 'api-key.txt')) -ne 'fixture-key-preserve') { throw 'Installer replaced the key.' }
if ([IO.File]::ReadAllText((Join-Path $oldServer 'data\catalog.json')) -ne 'fixture-cloud-data') { throw 'Installer changed application data.' }
foreach ($name in @('server-settings.json','setup-code.txt','server-settings.json.lock')) {
    if ([IO.File]::ReadAllText((Join-Path $oldServer $name)) -ne 'installed-state-preserve') { throw 'Installer changed installation settings or setup state.' }
}
$backup = Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts') -Directory -Filter 'v2-backup-*' | Select-Object -First 1
foreach ($package in $packages) {
    if ([IO.File]::ReadAllText((Join-Path $backup.FullName ($package + '\package.exe'))) -ne ('old-' + $package)) { throw 'Old package backup is incomplete.' }
}
Write-Output "PASS: Installer lock preflight, package replacement, backups, key and data preservation. Artifacts: $fixture"
