[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Gui,
    [switch]$Apply,
    [switch]$Json,
    [string[]]$ConfigPath = @(),
    [string[]]$KeepPath = @(),
    [string[]]$RemovePath = @(),
    [string]$BackupDirectory = '',
    [switch]$Library
)

$ErrorActionPreference = 'Stop'
if (!$BackupDirectory) { $BackupDirectory = Join-Path $PSScriptRoot 'backups' }
$script:cleanupCommand = $PSCmdlet
$script:cleanupSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$script:syncBase = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager'
$script:namespaceBase = 'Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace'
$script:classBase = 'Software\Classes\CLSID'

function Test-OwnedId([string]$Id) {
    return $Id -match ('^(?:Yunsevo|MyPan|PreviousFixture)!' + [regex]::Escape($script:cleanupSid) + '![0-9a-f]{16,32}$')
}
function Convert-RootPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}
function Test-ProtectedPath([string]$Path, [string[]]$Protected) {
    $pathValue = Convert-RootPath $Path
    if (!$pathValue) { return $false }
    foreach ($item in $Protected) {
        $other = Convert-RootPath $item
        if ($other -and ($pathValue.Equals($other, [StringComparison]::OrdinalIgnoreCase) -or
            $pathValue.StartsWith($other + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $other.StartsWith($pathValue + '\', [StringComparison]::OrdinalIgnoreCase))) { return $true }
    }
    return $false
}
function Get-ProtectedRoots {
    $files = @($ConfigPath)
    foreach ($name in @('YunsevoDesktop', 'MyPanDesktop', 'Yunsevo', 'MyPan')) {
        $folder = Join-Path $env:LOCALAPPDATA $name
        $files += (Join-Path $folder 'preferences.json'), (Join-Path $folder 'client.json')
    }
    if ($env:YUNSEVO_DESKTOP_HOME) { $files += Join-Path $env:YUNSEVO_DESKTOP_HOME 'preferences.json' }
    $result = @($KeepPath)
    foreach ($file in ($files | Select-Object -Unique)) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        # Do not export configurations: they may contain credentials. Fail closed on corruption.
        try { $config = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { throw "无法读取配置，已停止清理：$file。可先恢复该配置的备份。" }
        if ($config.SyncRoot) { $result += $config.SyncRoot }
        if ($config.Local) { $result += $config.Local }
        foreach ($task in $config.Tasks) { if ($task.Local) { $result += $task.Local } }
    }
    return @($result | Where-Object { $_ } | ForEach-Object { Convert-RootPath $_ } | Select-Object -Unique)
}
function Set-EntryDecision($Entry, [string[]]$Protected) {
    $Entry.Protected = Test-ProtectedPath $Entry.Path $Protected
    $Entry.Automatic = $false
    if ($Entry.Protected) { $Entry.Reason = '已配置的同步目录，保留'; return }
    if ($Entry.OtherUsers) { $Entry.Protected = $true; $Entry.Reason = '存在其他用户映射，保留'; return }
    if (!$Entry.Path) { $Entry.Automatic = $true; $Entry.Reason = '空注册 / 已失效入口'; return }
    if ($Entry.Path -match '(?i)[\\/]\.test-data[\\/]') {
        $Entry.Automatic = $true; $Entry.Reason = '测试遗留入口'; return
    }
    # Avoid probing an offline network share. Unknown real directories require an explicit selection.
    if (!$Entry.Path.StartsWith('\\') -and !(Test-Path -LiteralPath $Entry.Path)) {
        $Entry.Automatic = $true; $Entry.Reason = '目标目录不存在'; return
    }
    $Entry.Reason = '目录仍存在，默认保留'
}
function Get-CleanupEntries {
    $protected = @(Get-ProtectedRoots)
    $entries = @{}
    $base = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($script:syncBase)
    try {
        if ($base) {
            foreach ($id in $base.GetSubKeyNames()) {
                if (!(Test-OwnedId $id)) { continue }
                $key = $base.OpenSubKey($id)
                try {
                    $users = $key.OpenSubKey('UserSyncRoots')
                    try {
                        $rootPath = if ($users) { [string]$users.GetValue($script:cleanupSid) } else { '' }
                        $otherUsers = $users -and @($users.GetValueNames() | Where-Object { $_ -and $_ -ne $script:cleanupSid }).Count -gt 0
                    } finally { if ($users) { $users.Dispose() } }
                    $entries[$id] = [pscustomobject]@{ Id=$id; Name=[string]$key.GetValue('DisplayNameResource'); Path=$rootPath;
                        Clsid=[string]$key.GetValue('NamespaceCLSID'); OtherUsers=[bool]$otherUsers; Protected=$false; Automatic=$false; Reason='' }
                } finally { $key.Dispose() }
            }
        }
    } finally { if ($base) { $base.Dispose() } }
    # Include orphan navigation entries even when their SyncRootManager key has disappeared.
    $names = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:namespaceBase)
    try {
        if ($names) {
            foreach ($clsid in $names.GetSubKeyNames()) {
                if ($clsid -notmatch '^\{[0-9a-f-]{36}\}$') { continue }
                $key = $names.OpenSubKey($clsid)
                try { $id = [string]$key.GetValue('') } finally { $key.Dispose() }
                if (!(Test-OwnedId $id)) { continue }
                if (!$entries.ContainsKey($id)) {
                    $entries[$id] = [pscustomobject]@{ Id=$id; Name=''; Path=''; Clsid=$clsid; OtherUsers=$false; Protected=$false; Automatic=$false; Reason='' }
                }
                $entry = $entries[$id]
                if ($entry.Clsid -and $entry.Clsid -ne $clsid) { throw "注册信息不一致，已停止：$id" }
                $entry.Clsid = $clsid
                $class = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:classBase + '\' + $clsid)
                try {
                    if ($class) {
                        if (!$entry.Name) { $entry.Name = [string]$class.GetValue('') }
                        $bag = $class.OpenSubKey('Instance\InitPropertyBag')
                        try {
                            $target = if ($bag) { [string]$bag.GetValue('TargetFolderPath') } else { '' }
                            if (!$entry.Path) { $entry.Path = $target }
                            elseif ($target -and (Convert-RootPath $target) -ne (Convert-RootPath $entry.Path)) { throw "目录映射不一致，已停止：$id" }
                        } finally { if ($bag) { $bag.Dispose() } }
                    }
                } finally { if ($class) { $class.Dispose() } }
            }
        }
    } finally { if ($names) { $names.Dispose() } }
    foreach ($entry in ($entries.Values | Sort-Object Path, Id)) {
        if (!$entry.Name) { $entry.Name = '(空注册)' }
        Set-EntryDecision $entry $protected
        $entry
    }
}
function Get-EntryRegistryPaths($Entry) {
    'HKLM\' + $script:syncBase + '\' + $Entry.Id
    if ($Entry.Clsid -match '^\{[0-9a-f-]{36}\}$') {
        'HKCU\' + $script:namespaceBase + '\' + $Entry.Clsid
        'HKCU\' + $script:classBase + '\' + $Entry.Clsid
    }
}
function New-CleanupBackup([object[]]$Entries) {
    $folder = Join-Path ([IO.Path]::GetFullPath($BackupDirectory)) ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    $index = 0
    foreach ($entry in $Entries) {
        foreach ($key in (Get-EntryRegistryPaths $entry)) {
            $providerPath = if ($key.StartsWith('HKLM\')) { 'Registry::HKEY_LOCAL_MACHINE\' + $key.Substring(5) } else { 'Registry::HKEY_CURRENT_USER\' + $key.Substring(5) }
            if (!(Test-Path -LiteralPath $providerPath)) { continue }
            $index++
            $file = Join-Path $folder ('{0:D3}.reg' -f $index)
            $output = & "$env:SystemRoot\System32\reg.exe" export $key $file /y 2>&1
            if ($LASTEXITCODE -ne 0) { throw "注册表备份失败，尚未清理任何入口：$output" }
        }
    }
    $manifest = [ordered]@{ CreatedUtc=[DateTime]::UtcNow.ToString('o'); UserSid=$script:cleanupSid; Entries=$Entries; RegistryFiles=$index }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $folder 'manifest.json') -Encoding UTF8
    return $folder
}
function Remove-OwnedRegistration($Entry) {
    if (!(Test-OwnedId $Entry.Id)) { throw '拒绝清理其他提供商或其他用户。' }
    $base = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($script:syncBase, $true)
    try {
        if (!$base) { return }
        $key = $base.OpenSubKey($Entry.Id)
        if (!$key) { return }
        try {
            $users = $key.OpenSubKey('UserSyncRoots')
            try {
                if ($users) {
                    if (@($users.GetValueNames() | Where-Object { $_ -and $_ -ne $script:cleanupSid }).Count) { throw '存在其他用户映射，已停止。' }
                    $mapped = [string]$users.GetValue($script:cleanupSid)
                    if ($mapped -and (Convert-RootPath $mapped) -ne (Convert-RootPath $Entry.Path)) { throw '同步目录已改变，已停止。' }
                }
            } finally { if ($users) { $users.Dispose() } }
        } finally { $key.Dispose() }
        # Remove presentation metadata only. OS unregister APIs can delete online-only placeholders.
        # The exact key is constrained to this product and current SID. Never traverse the filesystem.
        $base.DeleteSubKeyTree($Entry.Id, $false)
    } finally { if ($base) { $base.Dispose() } }
}
function Remove-OrphanShellKeys($Entry) {
    if (!$Entry.Clsid) { return }
    if ($Entry.Clsid -notmatch '^\{[0-9a-f-]{36}\}$' -or !(Test-OwnedId $Entry.Id)) { throw '入口标识不合法，已停止。' }
    $names = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:namespaceBase, $true)
    try {
        if ($names) {
            $key = $names.OpenSubKey($Entry.Clsid)
            if ($key) {
                try { $owner = [string]$key.GetValue('') } finally { $key.Dispose() }
                if ($owner -ne $Entry.Id) { throw '入口归属已改变，请重新扫描。' }
                $names.DeleteSubKeyTree($Entry.Clsid, $false)
            }
        }
    } finally { if ($names) { $names.Dispose() } }
    $classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:classBase, $true)
    try {
        if ($classes) {
            $key = $classes.OpenSubKey($Entry.Clsid + '\Instance\InitPropertyBag')
            if ($key) {
                try { $target = [string]$key.GetValue('TargetFolderPath') } finally { $key.Dispose() }
                if ((Convert-RootPath $target) -ne (Convert-RootPath $Entry.Path)) { throw 'CLSID 目录归属已改变，请重新扫描。' }
                $classes.DeleteSubKeyTree($Entry.Clsid, $false)
            }
        }
    } finally { if ($classes) { $classes.Dispose() } }
}
function Update-Explorer {
    if (!('YunsevoCleanup.Shell' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace YunsevoCleanup {
    public static class Shell {
        [DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);
    }
}
'@
    }
    [YunsevoCleanup.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    # Refresh open Explorer windows without terminating explorer.exe.
    try {
        $shell = New-Object -ComObject Shell.Application
        foreach ($window in $shell.Windows()) { if ($window.FullName -like '*\explorer.exe') { try { $window.Refresh() } catch {} } }
    } catch { Write-Warning '可关闭并重新打开资源管理器以更新侧栏。' }
}
function Invoke-EntryCleanup([object[]]$Selected, [bool]$AllowProtected = $false) {
    $current = @(Get-CleanupEntries)
    $approved = @()
    foreach ($old in $Selected) {
        $entry = $current | Where-Object { $_.Id -eq $old.Id } | Select-Object -First 1
        if (!$entry) { continue }
        $explicit = @($RemovePath | Where-Object { (Convert-RootPath $_) -eq (Convert-RootPath $entry.Path) }).Count -gt 0
        if ($entry.OtherUsers) { throw '存在其他用户映射，已停止。' }
        if ($entry.Protected -and !$AllowProtected -and !$explicit) { throw "已保护同步目录：$($entry.Path)" }
        if ($entry.Path -ne $old.Path -or $entry.Clsid -ne $old.Clsid) { throw '注册已改变，请重新扫描。' }
        if (!(Test-OwnedId $entry.Id)) { throw '拒绝清理其他提供商。' }
        if (!$entry.Automatic -and (Get-Process -Name 'Yunsevo.Client','Yunsevo.Desktop','MyPan.Client','MyPan.Desktop' -ErrorAction SilentlyContinue)) {
            throw '请先从托盘退出客户端，再清理手动选择的目录入口。'
        }
        if ($script:cleanupCommand.ShouldProcess(($entry.Name + ' ' + $entry.Path), '移除资源管理器入口，保留文件')) { $approved += $entry }
    }
    if (!$approved.Count) { return [pscustomobject]@{ Removed=0; Failed=0; Backup=''; Results=@() } }
    $backup = New-CleanupBackup $approved
    $results = @()
    foreach ($entry in $approved) {
        try {
            Remove-OwnedRegistration $entry
            Remove-OrphanShellKeys $entry
            $results += [pscustomobject]@{ Id=$entry.Id; Path=$entry.Path; Success=$true; Error='' }
        } catch { $results += [pscustomobject]@{ Id=$entry.Id; Path=$entry.Path; Success=$false; Error=$_.Exception.Message } }
    }
    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'result.json') -Encoding UTF8
    Update-Explorer
    return [pscustomobject]@{ Removed=@($results | Where-Object Success).Count; Failed=@($results | Where-Object { !$_.Success }).Count; Backup=$backup; Results=$results }
}
function Show-CleanupWindow {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [Windows.Forms.Application]::EnableVisualStyles()
    $form = New-Object Windows.Forms.Form
    $form.Text = '沄序 Yunsevo · 云盘入口清理'; $form.Size = New-Object Drawing.Size(980, 600)
    $form.MinimumSize = New-Object Drawing.Size(760, 460); $form.StartPosition = 'CenterScreen'
    $form.Font = New-Object Drawing.Font('Microsoft YaHei UI', 9)
    $form.BackColor = [Drawing.Color]::White
    $layout = New-Object Windows.Forms.TableLayoutPanel
    $layout.Dock = 'Fill'; $layout.Padding = New-Object Windows.Forms.Padding(16); $layout.ColumnCount = 1; $layout.RowCount = 4
    foreach ($height in @(58, -1, 44, 75)) {
        $style = New-Object Windows.Forms.RowStyle
        if ($height -lt 0) { $style.SizeType = 'Percent'; $style.Height = 100 } else { $style.SizeType = 'Absolute'; $style.Height = $height }
        $layout.RowStyles.Add($style) | Out-Null
    }
    $intro = New-Object Windows.Forms.Label
    $intro.Dock = 'Fill'; $intro.Text = "清理资源管理器中重复、测试及失效的云盘入口。`r`n只处理侧栏注册，保留本地和云端文件；同步任务再次启动可能重新显示入口。"
    $list = New-Object Windows.Forms.ListView
    $list.Dock = 'Fill'; $list.View = 'Details'; $list.CheckBoxes = $true; $list.FullRowSelect = $true; $list.GridLines = $true
    foreach ($column in @(@('入口名称',165), @('目录',410), @('处理建议',280))) { $list.Columns.Add($column[0], $column[1]) | Out-Null }
    $actions = New-Object Windows.Forms.FlowLayoutPanel; $actions.Dock = 'Fill'
    $scan = New-Object Windows.Forms.Button; $scan.Text = '重新扫描'; $scan.AutoSize = $true
    $clean = New-Object Windows.Forms.Button; $clean.Text = '清理勾选入口'; $clean.AutoSize = $true
    $clean.BackColor = [Drawing.Color]::FromArgb(16, 171, 181); $clean.ForeColor = [Drawing.Color]::White; $clean.FlatStyle = 'Flat'
    $allow = New-Object Windows.Forms.CheckBox; $allow.Text = '允许勾选已配置目录的入口'; $allow.AutoSize = $true
    $actions.Controls.AddRange(@($scan,$clean,$allow))
    $status = New-Object Windows.Forms.TextBox
    $status.Dock = 'Fill'; $status.Multiline = $true; $status.ReadOnly = $true; $status.ScrollBars = 'Vertical'; $status.BackColor = [Drawing.Color]::FromArgb(242,246,248)
    $layout.Controls.Add($intro,0,0); $layout.Controls.Add($list,0,1); $layout.Controls.Add($actions,0,2); $layout.Controls.Add($status,0,3); $form.Controls.Add($layout)
    $reload = {
        $list.Items.Clear()
        foreach ($entry in @(Get-CleanupEntries)) {
            $row = New-Object Windows.Forms.ListViewItem($entry.Name); $row.Tag = $entry
            $row.SubItems.Add($entry.Path) | Out-Null; $row.SubItems.Add($entry.Reason) | Out-Null
            $row.Checked = $entry.Automatic
            if ($entry.Protected) { $row.ForeColor = [Drawing.Color]::Gray }
            $list.Items.Add($row) | Out-Null
        }
    }
    $list.add_ItemCheck({ param($sender,$event) $entry=$sender.Items[$event.Index].Tag; if ($entry.OtherUsers -or ($entry.Protected -and !$allow.Checked)) { $event.NewValue = [Windows.Forms.CheckState]::Unchecked } })
    $allow.add_CheckedChanged({ if (!$allow.Checked) { foreach ($row in $list.Items) { if ($row.Tag.Protected) { $row.Checked = $false } } } })
    $scan.add_Click({ try { & $reload; $status.Text = '扫描完成。默认勾选测试及失效项；灰色项是已保护的同步目录。' } catch { $status.Text = $_.Exception.Message } })
    $clean.add_Click({
        $clean.Enabled = $false; $scan.Enabled = $false
        try {
            $selected = @($list.CheckedItems | ForEach-Object { $_.Tag })
            $result = Invoke-EntryCleanup $selected -AllowProtected $allow.Checked
            & $reload
            $status.Text = "已清理 $($result.Removed) 项，失败 $($result.Failed) 项。`r`n备份：$($result.Backup)"
            foreach ($failure in @($result.Results | Where-Object { !$_.Success })) { $status.AppendText("`r`n$($failure.Path)：$($failure.Error)") }
        } catch { $status.Text = $_.Exception.Message }
        finally { $clean.Enabled = $true; $scan.Enabled = $true }
    })
    & $reload
    $status.Text = '默认只清理测试和失效入口。自定义配置可在命令行通过 -ConfigPath 或 -KeepPath 指定。'
    $form.ShowDialog() | Out-Null; $form.Dispose()
}

if ($Library) { return }
if ($PSVersionTable.PSEdition -eq 'Core') { throw '请使用系统 Windows PowerShell 5.1（powershell.exe），或双击附带的清理入口。' }
if ($Gui) {
    try { Show-CleanupWindow }
    catch { Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.MessageBox]::Show($_.Exception.Message, '沄序 · 清理未完成', 'OK', 'Error') | Out-Null }
    return
}
$entries = @(Get-CleanupEntries)
if ($Apply) {
    $selected = @(foreach ($entry in $entries) {
        $explicit = @($RemovePath | Where-Object { (Convert-RootPath $_) -eq (Convert-RootPath $entry.Path) }).Count -gt 0
        if ($entry.Automatic -or $explicit) { $entry }
    })
    $result = Invoke-EntryCleanup $selected
    if ($Json) { $result | ConvertTo-Json -Depth 6 } else { $result | Format-List Removed,Failed,Backup; $result.Results | Where-Object { !$_.Success } | Format-Table Path,Error -Wrap }
    if ($result.Failed) { exit 1 }
} elseif ($Json) { ConvertTo-Json -InputObject $entries -Depth 5 }
else {
    $entries | Select-Object Name,Path,Reason | Format-Table -AutoSize -Wrap
    Write-Host '上面仅为预览。双击工具打开窗口，或使用 -Apply 清理默认项；-WhatIf 可预演。'
}
