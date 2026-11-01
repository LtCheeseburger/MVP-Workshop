param(
    [string]$PortRoot = 'E:\Recomp Source\MVP06-NCAABB\MVP06-Base\MVP06-XBERecomp',
    [switch]$BuildPort
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$port = [IO.Path]::GetFullPath($PortRoot)
if ($port.Contains('"') -or $port.Contains('%') -or $port.Contains("`n")) { throw 'Unsupported characters in port path.' }
$run = Join-Path $repository ('test-output-port/run-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$testSource = Join-Path $repository 'tests/NativePortReload/main.cpp'
$executable = Join-Path $run 'NativePortReload.exe'
$environmentScript = Join-Path $port 'env.cmd'
if (!(Test-Path -LiteralPath $environmentScript)) { throw 'Port env.cmd was not found.' }
Push-Location $port
try {
    if ($BuildPort) {
        & "$env:SystemRoot/System32/cmd.exe" /d /c "call `"$environmentScript`" cmake --build --preset release --target MVP06 MVP06_code -j 4" 2>&1 | Tee-Object -FilePath (Join-Path $run 'port-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Port build failed.' }
    }
    & "$env:SystemRoot/System32/cmd.exe" /d /c "call `"$environmentScript`" clang++ -std=c++20 -DNDEBUG -I sdk/runtime sdk/runtime/kernel/mods.cpp sdk/runtime/kernel/inflate.cpp `"$testSource`" -o `"$executable`"" 2>&1 | Tee-Object -FilePath (Join-Path $run 'compile.log')
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $executable)) { throw 'Native regression harness did not compile.' }
    $output = & $executable (Join-Path $run 'fixtures') 2>&1
    $exitCode = $LASTEXITCODE
    $output | Tee-Object -FilePath (Join-Path $run 'results.txt')
    if ($exitCode -ne 0 -or $output -match '^FAIL\b' -or !($output -match '^PASS\b')) { throw 'Native port reload checks failed.' }
    if ($BuildPort -and ([IO.File]::ReadAllText((Join-Path $port 'sdk/runtime/app/main.cpp'))).Contains('--workshop-render-selftest')) {
        $renderFixtures = Join-Path $run 'render'
        & dotnet run --project (Join-Path $repository 'tests/LiveTextures') -- $renderFixtures 2>&1 | Tee-Object -FilePath (Join-Path $run 'texture-plan.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Live texture plan tests failed.' }
        $portExecutable = Join-Path $port 'out/build/release/MVP06.exe'
        $quotedFixtures = [char]34 + $renderFixtures + [char]34
        $process = Start-Process -FilePath $portExecutable -ArgumentList @('--workshop-render-selftest',$quotedFixtures) -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(30000) -or $process.ExitCode -ne 0) { throw 'Native renderer self-test failed or timed out.' }
        $renderReport = Get-Content -LiteralPath (Join-Path $renderFixtures 'live-render.txt')
        $renderReport | Write-Host
        if ($renderReport -match '^FAIL\b' -or !($renderReport -match '^PASS\b')) { throw 'Native renderer report failed.' }
    }
    Write-Host "Native port reports: $run"
} finally { Pop-Location }
