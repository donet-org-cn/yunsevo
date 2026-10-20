param(
    [int]$Port = 5080,
    [string]$DataPath,
    [string]$ListenAddress = '0.0.0.0'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$httpAddress = if ($ListenAddress.Contains(':')) { "[$ListenAddress]" } else { $ListenAddress }
$env:ASPNETCORE_URLS = "http://${httpAddress}:$Port"
if ($DataPath) { $env:Yunsevo__DataPath = [IO.Path]::GetFullPath($DataPath) }
$env:Yunsevo__DefaultDataPath = Join-Path $projectRoot 'data'
$published = Join-Path $projectRoot 'artifacts\v2\server\Yunsevo.Server.exe'
$previousKey = Join-Path $projectRoot 'artifacts\server\api-key.txt'
$newKey = Join-Path (Split-Path $published) 'api-key.txt'
if ((Test-Path -LiteralPath $published) -and (Test-Path -LiteralPath $previousKey) -and !(Test-Path -LiteralPath $newKey) -and !$env:Yunsevo__ApiKey -and !$env:Yunsevo__ApiKeyFile) {
    Copy-Item -LiteralPath $previousKey -Destination $newKey
}
Push-Location $projectRoot
try {
    if (Test-Path -LiteralPath $published) { & $published }
    else { dotnet run --project src/Yunsevo.Server -c Release --no-launch-profile }
    if ($LASTEXITCODE -ne 0) { throw "服务端退出，代码 $LASTEXITCODE" }
}
finally { Pop-Location }
