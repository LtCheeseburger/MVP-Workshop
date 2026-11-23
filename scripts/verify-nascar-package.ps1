param([string]$Directory = $PSScriptRoot, [switch]$WriteManifest)
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
foreach ($required in @('NASCAR-Workshop.exe', 'NASCAR-Workshop.dll', 'NASCAR-Workshop.deps.json', 'NASCAR-Workshop.runtimeconfig.json', 'MVPWorkshop.Core.dll', 'NASCAR-Decoder.exe', 'NASCAR-README.md', 'decoder-source/third_party/LICENSE.txt', 'decoder-source/third_party/ea_madden.c', 'decoder-source/main.c', 'decoder-source/build-nascar-native.ps1')) {
    $file = Join-Path $packageRoot $required
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Missing package file: $required" }
}
$runtime = Get-Content -LiteralPath (Join-Path $packageRoot 'NASCAR-Workshop.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.tfm -ne 'net9.0') { throw 'Unexpected NASCAR target runtime.' }
$manifestPath = Join-Path $packageRoot 'package-manifest.json'
if ($WriteManifest) {
    $files = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object { $_.FullName -ne $manifestPath } | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{ product = 'NASCAR-Workshop'; version = (Get-Item -LiteralPath (Join-Path $packageRoot 'NASCAR-Workshop.exe')).VersionInfo.ProductVersion; builtUtc = [DateTime]::UtcNow.ToString('o'); files = $files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.product -ne 'NASCAR-Workshop' -or @($manifest.files).Count -eq 0) { throw 'Invalid package manifest.' }
$seen = @{}
foreach ($entry in $manifest.files) {
    $file = [IO.Path]::GetFullPath((Join-Path $packageRoot $entry.path))
    if (!$file.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package manifest contains an escaping path.' }
    if ($seen.ContainsKey($file)) { throw "Duplicate manifest path: $($entry.path)" }; $seen[$file] = $true
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Package file missing: $($entry.path)" }
    if ((Get-Item -LiteralPath $file).Length -ne $entry.size -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Package integrity mismatch: $($entry.path)" }
}
foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse) {
    if ($file.FullName -ne $manifestPath -and !$seen.ContainsKey($file.FullName)) { throw "Unlisted package file: $($file.Name)" }
}
Write-Host "Verified NASCAR package: $($manifest.version), $($manifest.files.Count) files"
