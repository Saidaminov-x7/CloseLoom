[CmdletBinding()]
param([string]$InstallDir, [switch]$PurgeData, [switch]$TestMode)
$ErrorActionPreference = 'Stop'

function Get-CloseLoomRunValue {
    param([string]$RegistrySubKey)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($RegistrySubKey, $false)
    if (-not $key) { return $null }
    try { return $key.GetValue('CloseLoom', $null) } finally { $key.Dispose() }
}

function Test-RegistrationPresent {
    param([string]$RunSubKey, [string]$UninstallPath, [string]$ShortcutPath)
    return ($null -ne (Get-CloseLoomRunValue -RegistrySubKey $RunSubKey)) -or (Test-Path -LiteralPath $UninstallPath) -or (Test-Path -LiteralPath $ShortcutPath)
}

try {
    if ($TestMode -and $PurgeData) { throw 'TestMode cannot purge real user data.' }
    if ([string]::IsNullOrWhiteSpace($InstallDir)) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs/CloseLoom' }
    $InstallDir = [IO.Path]::GetFullPath($InstallDir)
    $localAppDataRoot = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'
    if ($TestMode) {
        $testInstallPrefix = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'CloseLoom-smoke-'))
        if (-not $InstallDir.StartsWith($testInstallPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'TestMode install directory must be below a CloseLoom-smoke folder in TEMP.' }
    }
    if (-not $TestMode -and -not $InstallDir.StartsWith($localAppDataRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Per-user uninstaller cannot remove a folder outside LOCALAPPDATA.' }

    $runSubKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
    $runPath = 'HKCU:\' + $runSubKey
    $uninstallPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloseLoom'
    $shortcutPath = Join-Path $env:APPDATA 'Microsoft/Windows/Start Menu/Programs/CloseLoom.lnk'
    if ($TestMode) {
        $registryRoot = $env:CLOSELOOM_TEST_REGROOT
        $shortcutDir = $env:CLOSELOOM_TEST_SHORTCUTDIR
        if ([string]::IsNullOrWhiteSpace($registryRoot) -or -not $registryRoot.StartsWith('HKCU:\Software\CloseLoom\InstallTests\', [StringComparison]::OrdinalIgnoreCase)) { throw 'TestMode registry root must be below HKCU Software\CloseLoom\InstallTests.' }
        $testShortcutPrefix = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'CloseLoom-StartMenu-'))
        if ([string]::IsNullOrWhiteSpace($shortcutDir) -or -not ([IO.Path]::GetFullPath($shortcutDir)).StartsWith($testShortcutPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'TestMode shortcut directory must be a CloseLoom-StartMenu folder under TEMP.' }
        $runPath = Join-Path $registryRoot 'Run'
        $uninstallPath = Join-Path $registryRoot 'Uninstall'
        $shortcutPath = Join-Path $shortcutDir 'CloseLoom.lnk'
    }

    $marker = Join-Path $InstallDir '.closeloom-install.json'
    if (-not (Test-Path -LiteralPath $InstallDir)) {
        if (Test-RegistrationPresent -RunSubKey $runSubKey -UninstallPath $uninstallPath -ShortcutPath $shortcutPath) { throw 'CloseLoom files are missing but startup or uninstall registration remains; refusing to alter unverified state.' }
        Write-Host 'CloseLoom is already removed.' -ForegroundColor Green
        return
    }
    if (-not (Test-Path -LiteralPath $marker)) {
        if (Test-RegistrationPresent -RunSubKey $runSubKey -UninstallPath $uninstallPath -ShortcutPath $shortcutPath) { throw 'CloseLoom install marker is missing while registrations remain; refusing to alter unverified state.' }
        Write-Host 'CloseLoom is already removed; preserving the remaining unverified folder.' -ForegroundColor Green
        return
    }
    $markerContent = Get-Content -LiteralPath $marker -Raw
    $manifest = $markerContent | ConvertFrom-Json
    if ($manifest.Product -ne 'CloseLoom' -or -not $manifest.Files) { throw 'CloseLoom install manifest is invalid.' }

    $expectedExePath = [IO.Path]::GetFullPath((Join-Path $InstallDir 'CloseLoom.Desktop.exe'))
    $processes = Get-CimInstance Win32_Process -Filter "Name='CloseLoom.Desktop.exe'" -ErrorAction Stop | Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).Equals($expectedExePath, [StringComparison]::OrdinalIgnoreCase) }
    foreach ($process in $processes) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 100
        $stillRunning = Get-CimInstance Win32_Process -Filter "Name='CloseLoom.Desktop.exe'" -ErrorAction Stop | Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).Equals($expectedExePath, [StringComparison]::OrdinalIgnoreCase) }
        if (-not $stillRunning) { break }
    }
    if ($stillRunning) { throw 'CloseLoom is still running and its files were left in place. Close the app and retry.' }

    $ownedPaths = @()
    foreach ($relative in $manifest.Files) {
        $file = [IO.Path]::GetFullPath((Join-Path $InstallDir $relative))
        if (-not $file.StartsWith($InstallDir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe path in install manifest: $relative" }
        if ($file -ne [IO.Path]::GetFullPath($marker)) { $ownedPaths += $file }
    }
    foreach ($file in $ownedPaths) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
        if (Test-Path -LiteralPath $file) { throw "Could not remove CloseLoom file; retry uninstall after closing programs that may be using it: $file" }
    }

    $directories = Get-ChildItem -LiteralPath $InstallDir -Directory -Recurse | Sort-Object FullName -Descending
    foreach ($directory in $directories) {
        if (-not (Get-ChildItem -LiteralPath $directory.FullName -Force)) {
            Remove-Item -LiteralPath $directory.FullName -Force
            if (Test-Path -LiteralPath $directory.FullName) { throw "Could not remove empty CloseLoom folder: $($directory.FullName)" }
        }
    }
    if ($PurgeData) {
        $dataDir = Join-Path $env:LOCALAPPDATA 'CloseLoom'
        if (Test-Path -LiteralPath $dataDir) {
            Remove-Item -LiteralPath $dataDir -Recurse -Force
            if (Test-Path -LiteralPath $dataDir) { throw 'Could not completely remove CloseLoom user data; retry uninstall.' }
        }
    }

    if ($TestMode) {
        foreach ($path in @($runPath, $uninstallPath)) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force
                if (Test-Path -LiteralPath $path) { throw "Could not remove isolated test registration: $path" }
            }
        }
        if (Test-Path -LiteralPath $registryRoot) {
            Remove-Item -LiteralPath $registryRoot -Recurse -Force
            if (Test-Path -LiteralPath $registryRoot) { throw "Could not remove isolated test registry root: $registryRoot" }
        }
        if (Test-Path -LiteralPath $shortcutDir) {
            Remove-Item -LiteralPath $shortcutDir -Recurse -Force
            if (Test-Path -LiteralPath $shortcutDir) { throw "Could not remove isolated test shortcut folder: $shortcutDir" }
        }
    } else {
        if (Test-Path -LiteralPath $runPath) {
            $runValue = Get-CloseLoomRunValue -RegistrySubKey $runSubKey
            if ($null -ne $runValue) { Remove-ItemProperty -Path $runPath -Name 'CloseLoom' -Force }
            if ($null -ne (Get-CloseLoomRunValue -RegistrySubKey $runSubKey)) { throw 'Could not remove the CloseLoom logon entry.' }
        }
        if (Test-Path -LiteralPath $uninstallPath) {
            Remove-Item -LiteralPath $uninstallPath -Recurse -Force
            if (Test-Path -LiteralPath $uninstallPath) { throw 'Could not remove CloseLoom Installed Apps registration.' }
        }
        if (Test-Path -LiteralPath $shortcutPath) {
            Remove-Item -LiteralPath $shortcutPath -Force
            if (Test-Path -LiteralPath $shortcutPath) { throw 'Could not remove the CloseLoom Start Menu shortcut.' }
        }
    }

    Remove-Item -LiteralPath $marker -Force
    if (Test-Path -LiteralPath $marker) { throw 'Could not remove the CloseLoom install marker.' }
    if (-not (Get-ChildItem -LiteralPath $InstallDir -Force)) {
        try {
            Remove-Item -LiteralPath $InstallDir -Force
            if (Test-Path -LiteralPath $InstallDir) { throw 'Could not remove the empty CloseLoom installation folder.' }
        } catch {
            if (Test-Path -LiteralPath $InstallDir) {
                try { Set-Content -LiteralPath $marker -Value $markerContent -Encoding UTF8 -ErrorAction Stop } catch { }
            }
            throw
        }
    }
    Write-Host 'CloseLoom was removed.' -ForegroundColor Green
} catch {
    Write-Error ("CloseLoom could not be removed completely: " + $_.Exception.Message)
    exit 1
}
