param(
    [string]$XrayDirectory = 'D:\Applications\NetProbe\xray',
    [string]$SingBoxDirectory = 'D:\Applications\NetProbe\singbox'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    & cmd /c src\build.cmd
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $testDir = Join-Path $PSScriptRoot '.build'
    $fakeDir = Join-Path $testDir 'fake-core'
    New-Item -ItemType Directory -Force $fakeDir | Out-Null
    Copy-Item RouteX.exe (Join-Path $testDir 'RouteX.exe') -Force
    $sdk = Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles 'dotnet\sdk') -Directory | Sort-Object { [version]($_.Name.Split('-')[0]) } -Descending | Select-Object -First 1
    $compiler = Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll'
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $references = @('mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Web.Extensions') | ForEach-Object { '-r:' + (Join-Path $framework ($_ + '.dll')) }
    & dotnet $compiler -nologo -target:exe -nostdlib "-out:$fakeDir\sing-box.exe" @references (Join-Path $PSScriptRoot 'FakeSingBox.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Fake core compilation failed' }
    & dotnet $compiler -nologo -target:exe -nostdlib "-out:$testDir\ConfigRoutingTests.exe" "-r:$testDir\RouteX.exe" @references (Join-Path $PSScriptRoot 'ConfigRoutingTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
    Push-Location $testDir
    try {
        & .\ConfigRoutingTests.exe $XrayDirectory $SingBoxDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Config routing tests failed' }
        foreach ($configFile in Get-ChildItem -Filter '*.json') {
            if ($configFile.Name -eq 'sample-xray.json') { continue }
            & (Join-Path $SingBoxDirectory 'sing-box.exe') check -c $configFile.FullName
            if ($LASTEXITCODE -ne 0) { throw ('sing-box rejected ' + $configFile.Name) }
        }
        $previousAsset = $env:XRAY_LOCATION_ASSET
        $env:XRAY_LOCATION_ASSET = $XrayDirectory
        try {
            & (Join-Path $XrayDirectory 'xray.exe') run -test -c sample-xray.json
            if ($LASTEXITCODE -ne 0) { throw 'Xray rejected sample config' }
        } finally { if ($null -eq $previousAsset) { Remove-Item Env:XRAY_LOCATION_ASSET } else { $env:XRAY_LOCATION_ASSET = $previousAsset } }
    } finally { Pop-Location }
} finally { Pop-Location }
