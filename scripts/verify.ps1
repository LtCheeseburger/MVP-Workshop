param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = '',
    [string]$GameFolder = '',
    [switch]$SkipDesktop
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository 'test-output-ci' }
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('run-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
$summary = [Collections.Generic.List[string]]::new()
function Run-Dotnet([string]$Name, [string[]]$Arguments) {
    Write-Host "Running $Name"
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $runDirectory ($Name + '.log')) | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
    $summary.Add("PASS $Name")
}
Push-Location $repository
try {
    Run-Dotnet 'solution-build' @('build', 'MVPWorkshop.slnx', '-c', $Configuration)
    $projects = Get-ChildItem tests -Filter '*.csproj' -Recurse | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | Sort-Object FullName
    foreach ($project in $projects) { Run-Dotnet ('build-' + $project.BaseName) @('build', $project.FullName, '-c', $Configuration) }
    foreach ($name in @('Dependencies', 'Diagnostics', 'GameLog', 'InstalledMods', 'Iso', 'MenuSimulation', 'MvpMod', 'Tasks', 'Textures')) {
        Run-Dotnet $name @('run', '--project', "tests/$name", '-c', $Configuration, '--no-build', '--no-restore')
    }
    $fixtures = Join-Path $runDirectory 'fixtures'
    Run-Dotnet 'fixtures' @('run', '--project', 'tests/Fixtures', '-c', $Configuration, '--no-build', '--no-restore', '--', $fixtures)
    Run-Dotnet 'player-editor-synthetic' @('run', '--project', 'tests/PlayerEditor', '-c', $Configuration, '--no-build', '--no-restore', '--', (Join-Path $fixtures 'players'), (Join-Path $runDirectory 'player-output'))
    Run-Dotnet 'loose-textures-synthetic' @('run', '--project', 'tests/LooseTextures', '-c', $Configuration, '--no-build', '--no-restore', '--', 'check', (Join-Path $fixtures 'install-source'))
    Run-Dotnet 'texture-png-roundtrip' @('run', '--project', 'tests/LooseTextures', '-c', $Configuration, '--no-build', '--no-restore', '--', 'roundtrip')
    Run-Dotnet 'install-synthetic' @('run', '--project', 'tests/Install', '-c', $Configuration, '--no-build', '--no-restore', '--', 'install', (Join-Path $fixtures 'install-source'), (Join-Path $runDirectory 'installed'))
    Run-Dotnet 'verify-install-synthetic' @('run', '--project', 'tests/Install', '-c', $Configuration, '--no-build', '--no-restore', '--', 'verify', (Join-Path $runDirectory 'installed'))
    if (!$SkipDesktop) {
        $app = Join-Path $repository "src/MVPWorkshop.App/bin/$Configuration/net9.0-windows/MVP-Workshop.dll"
        foreach ($name in @('shell', 'explorer', 'project', 'options', 'open-mod', 'diagnostics', 'tasks', 'game-log', 'conflicts', 'texture-folder')) {
            $report = Join-Path $runDirectory ($name + '.txt')
            $game = Join-Path $fixtures $(if ($name -eq 'tasks') { 'tasks' } else { 'explorer' })
            $arguments = @($app, '--game', $game, "--$name-selftest", $report)
            $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
            foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.Environment['MVPWORKSHOP_TEST_SETTINGS'] = Join-Path $runDirectory 'settings.json'
            $process = [Diagnostics.Process]::Start($start)
            if (!$process.WaitForExit(120000)) { $process.Kill($true); throw "$name desktop check timed out" }
            if ($process.ExitCode -ne 0) { throw "$name desktop check exited with $($process.ExitCode)" }
            $process.Dispose()
            if (!(Test-Path -LiteralPath $report)) { throw "$name desktop check produced no report" }
            $text = Get-Content -LiteralPath $report -Raw
            Write-Host $text
            if ($text -match '(?m)^FAIL\b' -or $text -notmatch '(?m)^PASS\b') { throw "$name desktop report failed" }
            $summary.Add("PASS desktop-$name")
        }
    }
    if ($GameFolder) {
        Run-Dotnet 'team-assignments-game' @('run', '--project', 'tests/TeamAssignments', '-c', $Configuration, '--no-build', '--no-restore', '--', $GameFolder, (Join-Path $runDirectory 'team-assignments'))
        Run-Dotnet 'player-editor-game' @('run', '--project', 'tests/PlayerEditor', '-c', $Configuration, '--no-build', '--no-restore', '--', $GameFolder, (Join-Path $runDirectory 'player-game-output'))
        Run-Dotnet 'loose-textures-game' @('run', '--project', 'tests/LooseTextures', '-c', $Configuration, '--no-build', '--no-restore', '--', 'check', $GameFolder)
    } else { $summary.Add('NOT RUN TeamAssignments real-game checks: pass -GameFolder with the supported validation install. Synthetic assignment/roster coverage runs in Tasks and Diagnostics.') }
} catch {
    $summary.Add('FAIL ' + $_.Exception.Message)
    throw
} finally {
    $summary | Set-Content -LiteralPath (Join-Path $runDirectory 'summary.txt')
    Write-Host "Reports: $runDirectory"
    Pop-Location
}
