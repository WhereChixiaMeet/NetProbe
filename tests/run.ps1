$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    & cmd /c src\build.cmd
    if ($LASTEXITCODE -ne 0 -or !(Test-Path RouteX.exe)) { throw 'Build failed' }
    $testDir = Join-Path $PSScriptRoot '.build'
    New-Item -ItemType Directory -Force $testDir | Out-Null
    Copy-Item RouteX.exe (Join-Path $testDir 'RouteX.exe') -Force
    $sdkRoot = Join-Path $env:ProgramFiles 'dotnet\sdk'
    $sdk = Get-ChildItem -LiteralPath $sdkRoot -Directory | Sort-Object { [version]($_.Name.Split('-')[0]) } -Descending | Select-Object -First 1
    $compiler = Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll'
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $references = @('mscorlib','System','System.Core','System.Drawing','System.Windows.Forms') | ForEach-Object { '-r:' + (Join-Path $framework ($_ + '.dll')) }
    $testExe = Join-Path $testDir 'Regression.exe'
    & dotnet $compiler -nologo -target:exe -nostdlib "-out:$testExe" "-r:$testDir\RouteX.exe" @references (Join-Path $PSScriptRoot 'RoutingEditsTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed' }
} finally { Pop-Location }
