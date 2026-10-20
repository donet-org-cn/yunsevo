param([switch]$Native, [switch]$Browser)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
Push-Location $projectRoot
try {
    dotnet build Yunsevo.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
    & (Join-Path $projectRoot 'tests\verify-update-installer.ps1')
    $storeArgs = @()
    if ($Native) { $storeArgs += '--native' }
    dotnet run --project tests/Yunsevo.StoreTests -c Release --no-build -- @storeArgs
    if ($LASTEXITCODE -ne 0) { throw 'Catalog migration tests failed.' }
    $testArgs = @("--workspace=$projectRoot", '--shell')
    if ($Native) {
        $server = Join-Path $projectRoot 'artifacts\v2\server\Yunsevo.Server.exe'
        $client = Join-Path $projectRoot 'artifacts\v2\windows-client\Yunsevo.Client.exe'
        if (!(Test-Path -LiteralPath $server) -or !(Test-Path -LiteralPath $client)) { throw '请先运行 scripts/publish.ps1。' }
        $testArgs += "--server-exe=$server"
        $testArgs += "--client-exe=$client"
    }
    dotnet run --project tests/Yunsevo.Tests -c Release --no-build -- @testArgs
    if ($LASTEXITCODE -ne 0) { throw '集成测试失败。' }
    $v2Args = @("--workspace=$projectRoot")
    if ($Native) {
        $v2Args += "--server-exe=$server"
        $v2Args += "--gateway-exe=$(Join-Path $projectRoot 'artifacts\v2\server\smb\Yunsevo.SmbGateway.exe')"
        $v2Args += "--desktop-exe=$(Join-Path $projectRoot 'artifacts\v2\desktop\Yunsevo.Desktop.exe')"
        $v2Args += "--headless-exe=$(Join-Path $projectRoot 'artifacts\v2\headless\Yunsevo.Headless.exe')"
    }
    dotnet run --project tests/Yunsevo.V2Tests -c Release --no-build -- @v2Args
    if ($LASTEXITCODE -ne 0) { throw 'V2 protocol and account integration tests failed.' }
    dotnet run --project tests/Yunsevo.V2Tests -c Release --no-build -- @v2Args --tls
    if ($LASTEXITCODE -ne 0) { throw 'HTTPS integration tests failed.' }
    if ($Browser) {
        if (!$Native) { throw 'Browser tests require -Native and a published V2 server.' }
        node tests/browser-smoke.mjs "--server-exe=$server" --v2
        if ($LASTEXITCODE -ne 0) { throw 'Browser tests failed.' }
        node tests/setup-smoke.mjs "--server-exe=$server"
        if ($LASTEXITCODE -ne 0) { throw 'First installation browser tests failed.' }
    }
}
finally { Pop-Location }
