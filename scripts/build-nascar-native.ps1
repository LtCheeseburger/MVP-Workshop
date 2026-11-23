param([string]$SourceDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -property installationPath
$compiler = Get-ChildItem "$vs/VC/Tools/MSVC/*/bin/Hostx64/x86/cl.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (!$compiler) { throw 'Install Visual C++ x86 build tools.' }
$vc = [IO.Path]::GetFullPath((Join-Path $compiler.DirectoryName '../../..'))
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdkVersion = (Get-ChildItem "$sdk/Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$env:INCLUDE = "$vc/include;$sdk/Include/$sdkVersion/ucrt;$sdk/Include/$sdkVersion/shared;$sdk/Include/$sdkVersion/um"
$env:LIB = "$vc/lib/x86;$sdk/Lib/$sdkVersion/ucrt/x86;$sdk/Lib/$sdkVersion/um/x86"
$native = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory) } else { Join-Path $repo 'src/NASCARWorkshop.Native' }
New-Item "$native/bin" -ItemType Directory -Force | Out-Null
& $compiler.FullName /nologo /O2 /MT /DWIN32 "$native/main.c" "/Fo$native/bin/decoder.obj" "/Fe$native/bin/NASCAR-Decoder.exe" /link /MACHINE:X86 /DYNAMICBASE /NXCOMPAT
if ($LASTEXITCODE) { throw 'Native decoder build failed.' }
