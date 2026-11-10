# Mimic by soybean_alien
# https://github.com/soybean-alien/Mimic
# Licensed under the GNU General Public License v3.0 (see LICENSE).
# Publishes Mimic and packs the small installer.
# Output: %USERPROFILE%\Mimic\build\winui\installer\Mimic_<version>_x64-setup.exe
# -MakeNsis (or the MAKENSIS environment variable) names makensis.exe when NSIS is not on PATH.
param([string]$Configuration = 'Release', [string]$MakeNsis)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$buildRoot = Join-Path $env:USERPROFILE 'Mimic\build\winui'
$publish = Join-Path $buildRoot 'publish'
$outDir = Join-Path $buildRoot 'installer'
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = "$($props.Project.PropertyGroup.Version)".Trim()

$makensis = @(
    $MakeNsis,
    $env:MAKENSIS,
    (Get-Command makensis.exe -ErrorAction SilentlyContinue | ForEach-Object Source),
    "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $makensis) { throw 'makensis.exe was not found. Install NSIS 3 (https://nsis.sourceforge.io) and run this again.' }

if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
dotnet publish (Join-Path $root 'Mimic\Mimic.csproj') -c $Configuration -p:Platform=x64 -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
Get-ChildItem -LiteralPath $publish -Recurse -Filter *.pdb | Remove-Item -Force

# The uninstaller removes exactly the files this installer put down.
$fileList = Join-Path $buildRoot 'uninstall-files.nsh'
$lines = @()
$files = Get-ChildItem -LiteralPath $publish -Recurse -File
foreach ($file in $files) { $lines += 'Delete "$INSTDIR\' + $file.FullName.Substring($publish.Length + 1) + '"' }
$folders = Get-ChildItem -LiteralPath $publish -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending
foreach ($folder in $folders) { $lines += 'RMDir "$INSTDIR\' + $folder.FullName.Substring($publish.Length + 1) + '"' }
Set-Content -LiteralPath $fileList -Value $lines -Encoding utf8

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outFile = Join-Path $outDir "Mimic_${version}_x64-setup.exe"
& $makensis /V2 "/DVERSION=$version" "/DSOURCE=$publish" "/DOUTFILE=$outFile" "/DFILELIST=$fileList" (Join-Path $PSScriptRoot 'Mimic.nsi')
if ($LASTEXITCODE -ne 0) { throw 'makensis failed.' }

# A copy of the installer and the two guides goes to the repo's "Fable Conversion" folder.
$share = Join-Path $root 'Fable Conversion'
New-Item -ItemType Directory -Force -Path $share | Out-Null
Copy-Item -LiteralPath $outFile, (Join-Path $root 'README.md'), (Join-Path $root 'START HERE.md') -Destination $share -Force
if ((Get-FileHash -LiteralPath $outFile).Hash -ne (Get-FileHash -LiteralPath (Join-Path $share (Split-Path -Leaf $outFile))).Hash) { throw 'The copy in Fable Conversion does not match the installer.' }
$sizeMb = [math]::Round((Get-Item -LiteralPath $outFile).Length / 1MB, 2)
$payloadMb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 2)
"Installer: $outFile ($sizeMb MB, $payloadMb MB installed); copied to $share"
