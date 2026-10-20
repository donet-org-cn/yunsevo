param()
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $projectRoot 'artifacts\v2\desktop\Yunsevo.Desktop.exe'
if (!(Test-Path -LiteralPath $executable)) { throw 'Publish or install the Yunsevo desktop package first.' }
Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable)
