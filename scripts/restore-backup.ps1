param(
    [Parameter(Mandatory=$true)][string]$Archive,
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archivePath = [IO.Path]::GetFullPath($Archive)
$targetPath = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (Test-Path -LiteralPath $targetPath) { throw '恢复目标必须是不存在的新目录，不会覆盖已有数据。' }
$parentPath = [IO.Path]::GetDirectoryName($targetPath)
if (!$parentPath -or !(Test-Path -LiteralPath $parentPath -PathType Container)) { throw '请先创建恢复目标的父目录。' }
$stagingPath = [IO.Path]::GetFullPath((Join-Path $parentPath ('.' + [IO.Path]::GetFileName($targetPath) + '.partial-' + [Guid]::NewGuid().ToString('N'))))
$parentPrefix = $parentPath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (!$stagingPath.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase) -or !$targetPath.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '恢复目录越界。' }
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $manifestEntry = $zip.GetEntry('manifest.json')
    if (!$manifestEntry -or $manifestEntry.Length -gt 32MB) { throw '备份缺少有效校验清单。' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open(), [Text.Encoding]::UTF8)
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.formatVersion -ne 1 -or !$manifest.files) { throw '不支持的备份格式。' }
    $declared = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if (!$file.path -or $file.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $file.size -lt 0 -or $declared.ContainsKey($file.path)) { throw '校验清单字段无效或重复。' }
        $declared.Add($file.path, $file)
    }
    foreach ($required in @('data/catalog.json','api-key.txt','RESTORE.txt')) { if (!$declared.ContainsKey($required)) { throw ('备份缺少 ' + $required) } }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName
        if (!$seen.Add($name) -or $name.Contains('\') -or $name.Contains(':') -or ($name.Split('/') | Where-Object {$_ -eq '' -or $_ -eq '.' -or $_ -eq '..'})) { throw '备份包含重复或不安全的路径。' }
        if ($name -eq 'manifest.json') { continue }
        if (!$declared.ContainsKey($name) -or $declared[$name].size -ne $entry.Length) { throw ('备份文件未登记或长度不符：' + $name) }
        if ($name -notmatch '^(api-key\.txt|RESTORE\.txt|data/catalog\.json|data/blobs/[a-fA-F0-9]{32}_[a-zA-Z0-9_-]+|data/audit/audit\.jsonl(\.[1-5])?|data/protocols\.json|data/smb-credentials\.json|data/smb-key\.bin|data/environment-settings\.json|data/environment\.key)$') { throw ('未知备份文件：' + $name) }
        $resolved = [IO.Path]::GetFullPath((Join-Path $stagingPath $name))
        if (!$resolved.StartsWith($stagingPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '备份路径越界。' }
    }
    if ($seen.Count -ne $declared.Count + 1) { throw '备份缺失清单中的文件。' }
    New-Item -ItemType Directory -Path $stagingPath | Out-Null
    if ($PSVersionTable.PSEdition -eq 'Core' -and ![Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        [IO.File]::SetUnixFileMode($stagingPath, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
    }
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -eq 'manifest.json') { continue }
        $outputPath = [IO.Path]::GetFullPath((Join-Path $stagingPath $entry.FullName))
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($outputPath)) -Force | Out-Null
        $inputStream = $entry.Open(); $outputStream = [IO.File]::Open($outputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $inputStream.CopyTo($outputStream); $outputStream.Flush() } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        $actualHash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash
        if ((Get-Item -LiteralPath $outputPath).Length -ne $declared[$entry.FullName].size -or $actualHash -ne $declared[$entry.FullName].sha256) { throw ('校验失败：' + $entry.FullName) }
    }
    New-Item -ItemType Directory -Path (Join-Path $stagingPath 'data/blobs') -Force | Out-Null
    # Both absolute paths were checked against the explicitly named destination parent above.
    Move-Item -LiteralPath $stagingPath -Destination $targetPath
    Write-Output ('恢复及 SHA-256 校验完成：' + $targetPath)
    Write-Output ('MyPan__DataPath=' + (Join-Path $targetPath 'data'))
    Write-Output ('MyPan__ApiKeyFile=' + (Join-Path $targetPath 'api-key.txt'))
}
catch {
    if (Test-Path -LiteralPath $stagingPath) { Write-Warning ('未发布恢复结果；中间目录保留于：' + $stagingPath) }
    throw
}
finally { $zip.Dispose() }
