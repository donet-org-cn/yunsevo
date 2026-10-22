$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $root 'tools/explorer-cleanup/cleanup-explorer.ps1') -Library
$checks = 0
function Assert($condition, [string]$label) {
    if (!$condition) { throw "FAIL: $label" }; $script:checks++; Write-Host "PASS: $label"
}
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Assert (Test-OwnedId "Yunsevo!$sid!1234567890abcdef") 'Current Yunsevo identity is recognized'
Assert (Test-OwnedId "MyPan!$sid!1234567890abcdef") 'Legacy identity is recognized'
Assert (Test-OwnedId "PreviousFixture!$sid!1234567890abcdef1234567890abcdef") 'Exact test identity is recognized'
Assert (!(Test-OwnedId "OneDrive!$sid!1234567890abcdef")) 'Other cloud providers are excluded'
Assert (!(Test-OwnedId 'Yunsevo!S-1-5-21-999-999-999-999!1234567890abcdef')) 'Other Windows users are excluded'
Assert (!(Test-OwnedId "Yunsevo!$sid!1234567890abcdef\child")) 'Nested registry paths are excluded'
Assert (Test-ProtectedPath 'F:\keep\child' @('f:\KEEP')) 'Child of a configured root is protected'
Assert (Test-ProtectedPath 'F:\keep' @('f:\KEEP\child')) 'Parent of a configured root is protected'
Assert (!(Test-ProtectedPath 'F:\keep-other' @('F:\keep'))) 'Sibling directory is not mistaken for a child'
function Entry([string]$path) { return [pscustomobject]@{ Path=$path; OtherUsers=$false; Protected=$false; Automatic=$false; Reason='' } }
$e = Entry 'F:\isolated\.test-data\example\sync'; Set-EntryDecision $e @(); Assert $e.Automatic 'Test registrations are selected by default'
Set-EntryDecision $e @('F:\isolated'); Assert ($e.Protected -and !$e.Automatic) 'A configured task overrides test-folder classification'
$e = Entry ''; Set-EntryDecision $e @(); Assert $e.Automatic 'Empty registrations are selected'
$e.OtherUsers = $true; Set-EntryDecision $e @(); Assert ($e.Protected -and !$e.Automatic) 'A mapping for another user is preserved'
$e = Entry '\\offline-server\share'; Set-EntryDecision $e @(); Assert (!$e.Automatic -and !$e.Protected) 'Offline shares are not probed or automatically removed'
$entries = @(Get-CleanupEntries)
Assert (@($entries | Where-Object { !(Test-OwnedId $_.Id) }).Count -eq 0) 'Read-only inventory contains only current-user owned identities'
Assert (@($entries | Where-Object { $_.Protected -and $_.Automatic }).Count -eq 0) 'No configured task is automatically selected'
Write-Host "ALL PASSED: $checks checks"
