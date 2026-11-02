param(
    [Parameter(Mandatory)][string]$GameFolder,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$EncoderDirectory = 'test-output-research/upstream'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$probe = Join-Path $repository 'src/MVPWorkshop.Probe/bin/Release/net9.0/MVPWorkshop.Probe.dll'
function Probe([string[]]$Arguments) {
    & dotnet $probe @Arguments
    if ($LASTEXITCODE) { throw 'Research probe failed' }
}
Probe @('runtime-research', $GameFolder, $OutputDirectory)
Probe @('microtalk-study', $GameFolder, $OutputDirectory, (Join-Path $EncoderDirectory 'utkencode.exe'), (Join-Path $EncoderDirectory 'mt-reference.exe'))
Probe @('microtalk-study', $GameFolder, $OutputDirectory, (Join-Path $EncoderDirectory 'utkencode-rev3.exe'), (Join-Path $EncoderDirectory 'mt-reference.exe'), '--rev3')
$native = @(Get-Content (Join-Path $OutputDirectory 'native-reference.json') -Raw | ConvertFrom-Json)
if (!$native.Count -or @($native | Where-Object { !$_.SameSampleCount -or $_.MaxDelta -gt 1 }).Count) { throw 'Native independent decode disagrees with Workshop' }
foreach ($file in @('encoding-study.json', 'encoding-study-rev3.json')) {
    $rows = @(Get-Content (Join-Path $OutputDirectory $file) -Raw | ConvertFrom-Json)
    if (!$rows.Count -or @($rows | Where-Object IndependentDecoderMaxDelta -gt 1).Count) { throw 'Encoded independent decode disagrees with Workshop' }
    if ($file -like '*-rev3*' -and @($rows | Where-Object { $_.NativeBlocksOverCapacity -eq 0 -and (!$_.NativeRepackMatchesIndependentPcm -or !$_.NativeFramingPreserved) }).Count) { throw 'Native repack verification failed' }
}
Write-Output 'PASS independent codec dimensions/rounding and every fitting native repack. Signal-quality metrics are measurements, not acceptance gates.'
