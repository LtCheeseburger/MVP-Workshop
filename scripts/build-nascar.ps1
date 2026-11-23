param([string]$OutputDirectory = '', [switch]$Verify, [string]$GameFolder = '', [switch]$NoRestore, [switch]$Zip)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'release/NASCAR-Workshop' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
if ($OutputDirectory -eq $repo -or $repo.StartsWith($OutputDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be a dedicated NASCAR package folder.' }
if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1) -and !(Test-Path -LiteralPath (Join-Path $OutputDirectory 'NASCAR-Workshop.exe'))) { throw 'Existing output is not a NASCAR package folder.' }
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $repo '.tmp/nascar/packages'))
$stage = [IO.Path]::GetFullPath((Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))))
if (!$stage.StartsWith($stagingRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging folder.' }
New-Item -Path $stage -ItemType Directory -Force | Out-Null
$restoreArgs = @(); if ($NoRestore) { $restoreArgs = @('--no-restore') }
& (Join-Path $PSScriptRoot 'build-nascar-native.ps1')
if ($Verify) {
    & dotnet build "$repo/tests/Nascar" -c Release -m:1 @restoreArgs
    if ($LASTEXITCODE) { throw 'NASCAR validation build failed.' }
    & dotnet "$repo/tests/Nascar/bin/Release/net9.0/Nascar.dll"
    if ($LASTEXITCODE) { throw 'NASCAR fixture validation failed.' }
    if ($GameFolder) {
        foreach ($mode in @('--model-textures', '--export-test', '--catalog-test')) {
            & dotnet "$repo/tests/Nascar/bin/Release/net9.0/Nascar.dll" $GameFolder $mode
            if ($LASTEXITCODE) { throw "NASCAR validation failed: $mode" }
        }
    }
}
& dotnet publish "$repo/src/NASCARWorkshop.App" -c Release -m:1 @restoreArgs -o $stage
if ($LASTEXITCODE) { throw 'NASCAR-Workshop publish failed.' }
$licenses = Join-Path $stage 'decoder-source'
New-Item $licenses -ItemType Directory -Force | Out-Null
Copy-Item "$repo/src/NASCARWorkshop.Native/third_party" $licenses -Recurse -Force
Copy-Item "$repo/src/NASCARWorkshop.Native/main.c" $licenses -Force
Copy-Item "$repo/scripts/build-nascar-native.ps1" $licenses -Force
Copy-Item "$repo/NASCAR-README.md" $stage -Force
Copy-Item "$repo/scripts/verify-nascar-package.ps1" (Join-Path $stage 'Verify-Package.ps1') -Force
# Every package runs the export smoke test using the packaged binaries.
$testReport = Join-Path $stage 'release-check.txt'
$savedSettings = $env:MVPWORKSHOP_TEST_SETTINGS
try {
    $env:MVPWORKSHOP_TEST_SETTINGS = Join-Path $stagingRoot ('settings-' + [Guid]::NewGuid().ToString('N') + '.json')
    $arguments = @('--nascar-release-selftest', ('"' + $testReport + '"'))
    if ($GameFolder -and $Verify) { $arguments += @('--game', ('"' + [IO.Path]::GetFullPath($GameFolder) + '"')) }
    $process = Start-Process -FilePath (Join-Path $stage 'NASCAR-Workshop.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'Packaged application self-test timed out.' }
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $testReport) -or (Select-String -LiteralPath $testReport -Pattern '^FAIL')) { throw "Packaged application self-test failed: $testReport" }
} finally { $env:MVPWORKSHOP_TEST_SETTINGS = $savedSettings }
# Reports and exported test scenes stay in staging; ship only application resources.
$testResult = Get-Content -LiteralPath $testReport -Raw
Move-Item -LiteralPath $testReport -Destination (Join-Path $stagingRoot ('check-' + [Guid]::NewGuid().ToString('N') + '.txt'))
# The scene test creates one uniquely named sibling directory; move it outside the package.
foreach ($testScene in Get-ChildItem -LiteralPath $stage -Directory -Filter 'scene-export-*') {
    $sceneTarget = [IO.Path]::GetFullPath((Join-Path $stagingRoot $testScene.Name))
    if (!$testScene.FullName.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase) -or !$sceneTarget.StartsWith($stagingRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test scene path.' }
    Move-Item -LiteralPath $testScene.FullName -Destination $sceneTarget
}
& (Join-Path $PSScriptRoot 'verify-nascar-package.ps1') -Directory $stage -WriteManifest
# Keep the last build for rollback. Replace the stable folder only after verification.
$backup = $OutputDirectory + '.previous-' + [Guid]::NewGuid().ToString('N')
New-Item -Path ([IO.Path]::GetDirectoryName($OutputDirectory)) -ItemType Directory -Force | Out-Null
if (!(Test-Path -LiteralPath $stage -PathType Container) -or [IO.Path]::GetDirectoryName($backup) -ne [IO.Path]::GetDirectoryName($OutputDirectory)) { throw 'Invalid package replacement paths.' }
if (Test-Path -LiteralPath $OutputDirectory) { Move-Item -LiteralPath $OutputDirectory -Destination $backup }
try { Move-Item -LiteralPath $stage -Destination $OutputDirectory }
catch { if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $OutputDirectory }; throw }
if ($Zip) {
    [xml]$project = Get-Content "$repo/src/NASCARWorkshop.App/NASCARWorkshop.App.csproj"
    $zipPath = Join-Path ([IO.Path]::GetDirectoryName($OutputDirectory)) ("NASCAR-Workshop-" + $project.Project.PropertyGroup.Version + '.zip')
    Compress-Archive -LiteralPath $OutputDirectory -DestinationPath $zipPath -Force
    Write-Host "Package ZIP: $zipPath"
}
Write-Host $testResult
Write-Host "NASCAR-Workshop: $OutputDirectory/NASCAR-Workshop.exe"
