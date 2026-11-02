param([string]$OutputDirectory = 'test-output-research/upstream', [string]$Compiler = 'gcc')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$encoderCommit = 'e0b13fac7a75fc5ef16851c165d3f31f0fe220b1'
$decoderCommit = 'd29ffa6e19a41efb54bfd9f68bd65f93c3d2ab25'
foreach ($name in @('utkencode.c', 'UNLICENSE')) {
    Invoke-WebRequest "https://raw.githubusercontent.com/daddesio/utkencode/$encoderCommit/$name" -OutFile (Join-Path $OutputDirectory $name)
}
foreach ($name in @('utkdec.c', 'utkdec.h')) {
    Invoke-WebRequest "https://raw.githubusercontent.com/vgmstream/vgmstream/$decoderCommit/src/coding/libs/$name" -OutFile (Join-Path $OutputDirectory $name)
}
# Research variant: emit the native revision-3 marker and byte-align each compressed frame.
# Keep the 32-byte UTM0 envelope only as an experiment transport; this is not a valid Maxis file.
$source = [IO.File]::ReadAllText((Join-Path $OutputDirectory 'utkencode.c')).Replace("`r`n", "`n")
$header = 'write_data(outfp, utk_header, 32);'
$endFrame = "`t`tbwc_flush(&bwc, outfp);`n`t}"
if (!$source.Contains($header) -or !$source.Contains($endFrame)) { throw 'Pinned encoder shape changed; review adaptation.' }
$frameReplacement = "`t`tbwc_pad(&bwc);`n`t`tbwc_flush(&bwc, outfp);`n`t`tprintf(""%ld\n"", ftell(outfp));`n`t`tif (bytes_remaining) fputc(0, outfp);`n`t}"
$adapted = $source.Replace($header, "$header`n`tfputc(0, outfp);").Replace($endFrame, $frameReplacement)
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'utkencode-rev3.c'), $adapted)
foreach ($name in @('utkencode', 'utkencode-rev3')) {
    & $Compiler -O2 -o (Join-Path $OutputDirectory "$name.exe") (Join-Path $OutputDirectory "$name.c")
    if ($LASTEXITCODE) { throw "$name compile failed" }
}
& $Compiler -O2 -I $OutputDirectory (Join-Path $PSScriptRoot 'microtalk-decode.c') (Join-Path $OutputDirectory 'utkdec.c') -o (Join-Path $OutputDirectory 'mt-reference.exe')
if ($LASTEXITCODE) { throw 'Independent decoder compile failed' }
[ordered]@{ EncoderCommit=$encoderCommit; DecoderCommit=$decoderCommit; Files=@(Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object Extension -in '.c','.h' | ForEach-Object { @{Name=$_.Name;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} }) } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'provenance.json')
