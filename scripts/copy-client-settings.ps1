param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference='Stop'
$sourcePath=[IO.Path]::GetFullPath($Source).TrimEnd([IO.Path]::DirectorySeparatorChar)
$destinationPath=[IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (!(Test-Path -LiteralPath $sourcePath -PathType Container)) {throw 'Source configuration directory is missing.'}
if (Test-Path -LiteralPath $destinationPath) {throw 'Destination must be a new directory; existing settings will not be overwritten.'}
if ($destinationPath.StartsWith($sourcePath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Destination must be outside the source.'}
$files=@(Get-ChildItem -LiteralPath $sourcePath -Recurse -File)
if ($files | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}) {throw 'Configuration migration cannot follow reparse points.'}
New-Item -ItemType Directory -Path $destinationPath | Out-Null
foreach ($file in $files) {
    $relative=$file.FullName.Substring($sourcePath.Length+1)
    $target=[IO.Path]::GetFullPath((Join-Path $destinationPath $relative))
    if (!$target.StartsWith($destinationPath+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Configuration path escaped destination.'}
    New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
    if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {throw 'Configuration verification failed; originals were preserved.'}
}
Write-Output ("Copied and verified {0} configuration files; originals preserved." -f $files.Count)
