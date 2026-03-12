param(
    [string]$ExecutablePath,
    [switch]$Unregister
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-DefaultExecutablePath {
    $candidates = @(
        (Join-Path $PSScriptRoot 'SplatViewer_VR.exe'),
        (Join-Path $PSScriptRoot '..\projects\Splatviewer_VR\Release\1.4\SplatViewer_VR.exe'),
        (Join-Path $PSScriptRoot '..\projects\Splatviewer_VR\Release\1.3\SplatViewer_VR.exe')
    )

    foreach ($candidate in $candidates) {
        $fullPath = [System.IO.Path]::GetFullPath($candidate)
        if (Test-Path -LiteralPath $fullPath) {
            return $fullPath
        }
    }

    throw 'Could not find SplatViewer_VR.exe automatically. Pass -ExecutablePath explicitly.'
}

function Set-RegistryKeyDefaultValue {
    param(
        [Parameter(Mandatory = $true)][string]$SubKey,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($SubKey)
    if ($null -eq $key) {
        throw "Could not create registry key: HKCU\\$SubKey"
    }

    try {
        $key.SetValue('', $Value, [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $key.Dispose()
    }
}

$basePath = 'Software\Classes'
$progId = 'Splatviewer_VR.splat'

if ($Unregister) {
    foreach ($subKey in @(
        "$basePath\\.ply",
        "$basePath\\.spz",
        "$basePath\\.spx",
        "$basePath\.sog",
        "$basePath\\$progId"
    )) {
        $keyPath = "HKCU:\$subKey"
        if (Test-Path -LiteralPath $keyPath) {
            Remove-Item -LiteralPath $keyPath -Recurse -Force
        }
    }

    Write-Host 'Removed Splatviewer_VR file associations for .ply, .spz, .spx, and .sog.'
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Resolve-DefaultExecutablePath
} else {
    $ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
}

if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "Executable not found: $ExecutablePath"
}

$command = ('"{0}" "%1"' -f $ExecutablePath)
$icon = ('{0},0' -f $ExecutablePath)

Set-RegistryKeyDefaultValue -SubKey "$basePath\\.ply" -Value $progId
Set-RegistryKeyDefaultValue -SubKey "$basePath\\.spz" -Value $progId
Set-RegistryKeyDefaultValue -SubKey "$basePath\\.spx" -Value $progId
Set-RegistryKeyDefaultValue -SubKey "$basePath\.sog" -Value $progId
Set-RegistryKeyDefaultValue -SubKey "$basePath\\$progId" -Value 'Splatviewer Gaussian Splat'
Set-RegistryKeyDefaultValue -SubKey "$basePath\\$progId\\DefaultIcon" -Value $icon
Set-RegistryKeyDefaultValue -SubKey "$basePath\\$progId\\shell" -Value 'open'
Set-RegistryKeyDefaultValue -SubKey "$basePath\\$progId\\shell\\open" -Value 'Open with Splatviewer_VR'
Set-RegistryKeyDefaultValue -SubKey "$basePath\\$progId\\shell\\open\\command" -Value $command

Write-Host "Registered .ply, .spz, .spx, and .sog to open with: $ExecutablePath"
Write-Host 'If Explorer does not update immediately, restart Explorer or sign out and back in.'