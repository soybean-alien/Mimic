# Mimic by soybean_alien
# https://github.com/soybean-alien/Mimic
# Licensed under the GNU General Public License v3.0 (see LICENSE).
# Checks for the two Microsoft runtimes Mimic needs. It installs nothing: setup opens
# Microsoft's download in the browser and waits.
# Exit codes: 0 nothing missing, 10 .NET missing, 20 Windows App Runtime missing, 30 both.
$ErrorActionPreference = 'Stop'

$appRuntimeMinimum = [version]'2.6.5.0'

function Test-DotNet {
    # Setup is a 32-bit program, where ProgramFiles means "Program Files (x86)"; the x64 runtime is under ProgramW6432.
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $roots = @("$programFiles\dotnet")
    if ($env:DOTNET_ROOT) { $roots += $env:DOTNET_ROOT }
    foreach ($root in $roots) {
        if (Get-ChildItem -LiteralPath (Join-Path $root 'shared\Microsoft.NETCore.App') -Directory -Filter '10.*' -ErrorAction SilentlyContinue) {
            return $true
        }
    }
    return $false
}

function Test-AppRuntime {
    $found = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2' -ErrorAction SilentlyContinue |
        Where-Object { "$($_.Architecture)" -eq 'X64' -and [version]$_.Version -ge $appRuntimeMinimum }
    return [bool]$found
}

exit ([int](-not (Test-DotNet)) * 10 + [int](-not (Test-AppRuntime)) * 20)
