param([string]$Destination)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if (!$Destination) { $Destination = Join-Path (Split-Path -Parent $source) 'Yunsevo' }
$target = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
if ($source -eq [IO.Path]::GetPathRoot($source).TrimEnd('\') -or $target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\')) { throw 'A workspace cannot be a volume root.' }
if (!(Test-Path -LiteralPath (Join-Path $source 'Yunsevo.slnx'))) { throw 'Source is not the Yunsevo workspace.' }
if ([string]::Equals($source, $target, [StringComparison]::OrdinalIgnoreCase)) { Write-Host "Workspace already at $target"; return }
if (Test-Path -LiteralPath $target) { throw 'Destination already exists; nothing was moved.' }
if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source cannot be a link.' }
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count) { throw 'Stop workspace programs, builds and emulators before renaming.' }
$parent = Split-Path -Parent $source
Set-Location -LiteralPath $parent
[Environment]::CurrentDirectory = $parent
# Both absolute endpoints are checked above. No files are deleted or merged.
Move-Item -LiteralPath $source -Destination $target
$utf8 = [Text.UTF8Encoding]::new($false)
$settings = Join-Path $target 'artifacts/v2/server/server-settings.json'
if (Test-Path -LiteralPath $settings) {
    $configuration = Get-Content -Raw -Encoding UTF8 -LiteralPath $settings | ConvertFrom-Json
    if ($configuration.dataPath -and $configuration.dataPath.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $settings -Destination ($settings + '.before-relocation')
        $configuration.dataPath = $target + $configuration.dataPath.Substring($source.Length)
        [IO.File]::WriteAllText($settings, ($configuration | ConvertTo-Json -Depth 8), $utf8)
    }
}
$avd = Join-Path $target '.test-data/android/avds/yunsevo-test.ini'
if (Test-Path -LiteralPath $avd) {
    $ini = [IO.File]::ReadAllText($avd).Replace('path=' + $source + '\', 'path=' + $target + '\')
    [IO.File]::WriteAllText($avd, $ini, $utf8)
}
Write-Host "Workspace moved to $target. Reopen this folder in your editor."
