[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$NoLaunch,
    [switch]$PurgeData,
    [switch]$TestMode
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$appExe = 'CloseLoom.Desktop.exe'
$appName = 'CloseLoom'
$releaseApi = 'https://api.github.com/repos/Saidaminov-x7/CloseLoom/releases?per_page=10'

function Get-PayloadRoot {
    param([string]$Root)
    if (Test-Path -LiteralPath (Join-Path $Root $appExe)) { return $Root }
    foreach ($candidate in @((Join-Path $Root 'app'))) {
        if (Test-Path -LiteralPath (Join-Path $candidate $appExe)) { return $candidate }
    }
    return $null
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
    if (-not $TestMode -and -not $InstallDir.StartsWith($localAppDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Per-user installation must remain under LOCALAPPDATA. Use -InstallDir only for an isolated test.'
    }

    $payload = Get-PayloadRoot -Root $PSScriptRoot
    if (-not $payload) {
        $downloadRoot = Join-Path ([IO.Path]::GetTempPath()) ('CloseLoom-install-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $downloadRoot | Out-Null
        try {
            $releases = @(Invoke-RestMethod -Uri $releaseApi -Headers @{ 'User-Agent' = 'CloseLoom-Installer' })
            $release = $releases | Where-Object { -not $_.draft } | Select-Object -First 1
            $asset = $release.assets | Where-Object { $_.name -eq 'CloseLoom-win-x64.zip' } | Select-Object -First 1
            if (-not $asset -or $asset.browser_download_url -notmatch '^https://github\.com/Saidaminov-x7/CloseLoom/releases/download/') { throw 'No official self-contained Windows release ZIP was found. Open the official Releases page and download CloseLoom-win-x64.zip.' }
            $zipPath = Join-Path $downloadRoot 'release.zip'
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -Headers @{ 'User-Agent' = 'CloseLoom-Installer' }
            Expand-Archive -LiteralPath $zipPath -DestinationPath (Join-Path $downloadRoot 'payload')
            $payload = Get-PayloadRoot -Root (Join-Path $downloadRoot 'payload')
            if (-not $payload) { throw 'The official release archive does not contain CloseLoom.Desktop.exe.' }
            $archiveInstaller = Join-Path $payload 'install.ps1'
            if (Test-Path -LiteralPath $archiveInstaller) {
                & $archiveInstaller -InstallDir $InstallDir -NoLaunch:$NoLaunch -PurgeData:$PurgeData -TestMode:$TestMode
                if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Installer from release archive failed.' }
                return
            }
        } finally {
            Remove-Item -LiteralPath $downloadRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $sourceExe = Join-Path $payload $appExe
    if (-not (Test-Path -LiteralPath $sourceExe)) { throw "Application executable missing from payload: $sourceExe" }
    $expectedExePath = [IO.Path]::GetFullPath((Join-Path $InstallDir $appExe))
    $existingProc = Get-CimInstance Win32_Process -Filter "Name='$appExe'" -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).Equals($expectedExePath, [StringComparison]::OrdinalIgnoreCase) }
    if ($existingProc) { throw 'CloseLoom is running from the installation folder. Exit it from the tray, then run the installer again.' }

    $runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $uninstallKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloseLoom'
    $shortcutDir = Join-Path $env:APPDATA 'Microsoft/Windows/Start Menu/Programs'
    if ($TestMode) {
        if ([string]::IsNullOrWhiteSpace($env:CLOSELOOM_TEST_REGROOT) -or [string]::IsNullOrWhiteSpace($env:CLOSELOOM_TEST_SHORTCUTDIR)) { throw 'TestMode requires isolated CLOSELOOM_TEST_REGROOT and CLOSELOOM_TEST_SHORTCUTDIR values.' }
        $testRegistryRoot = $env:CLOSELOOM_TEST_REGROOT
        if (-not $testRegistryRoot.StartsWith('HKCU:\Software\CloseLoom\InstallTests\', [StringComparison]::OrdinalIgnoreCase)) { throw 'TestMode registry root must be below HKCU Software\CloseLoom\InstallTests.' }
        $testShortcutPrefix = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'CloseLoom-StartMenu-'))
        $shortcutDir = [IO.Path]::GetFullPath($env:CLOSELOOM_TEST_SHORTCUTDIR)
        if (-not $shortcutDir.StartsWith($testShortcutPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'TestMode shortcut directory must be a CloseLoom-StartMenu folder under TEMP.' }
        $runKeyPath = Join-Path $testRegistryRoot 'Run'
        $uninstallKeyPath = Join-Path $testRegistryRoot 'Uninstall'
    }

    $marker = Join-Path $InstallDir '.closeloom-install.json'
    if ((Test-Path -LiteralPath $InstallDir) -and -not (Test-Path -LiteralPath $marker)) { throw "The destination already exists and is not a CloseLoom installation: $InstallDir" }
    $ownedFiles = @()
    if (Test-Path -LiteralPath $marker) {
        try { $ownedFiles = @((Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json).Files) } catch { throw 'Existing CloseLoom install manifest is invalid; refusing to overwrite.' }
    }
    $files = Get-ChildItem -LiteralPath $payload -File -Recurse
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($payload.Length).TrimStart('\','/')
        $target = Join-Path $InstallDir $relative
        if ((Test-Path -LiteralPath $target) -and $relative -notin $ownedFiles) { throw "Refusing to overwrite an unrelated file in the install directory: $relative" }
    }
    foreach ($name in @('install.ps1','uninstall.ps1','install.cmd','uninstall.cmd')) {
        if ((Test-Path -LiteralPath (Join-Path $InstallDir $name)) -and $name -notin $ownedFiles) { throw "Refusing to overwrite an unrelated file in the install directory: $name" }
    }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($payload.Length).TrimStart('\','/')
        $target = Join-Path $InstallDir $relative
        if ((Test-Path -LiteralPath $target) -and $relative -notin $ownedFiles) { throw "Refusing to overwrite an unrelated file in the install directory: $relative" }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $ownedFiles += $relative
    }
    foreach ($name in @('install.ps1','uninstall.ps1','install.cmd','uninstall.cmd')) {
        $target = Join-Path $InstallDir $name
        if ((Test-Path -LiteralPath $target) -and $name -notin $ownedFiles) { throw "Refusing to overwrite an unrelated file in the install directory: $name" }
        if ((Test-Path -LiteralPath (Join-Path $PSScriptRoot $name)) -and ($name -notin $ownedFiles -or -not (Test-Path -LiteralPath $target))) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $target
            $ownedFiles += $name
        }
    }
    $ownedFiles = @($ownedFiles | Select-Object -Unique) + '.closeloom-install.json'
    @{ Product = 'CloseLoom'; Files = $ownedFiles } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $marker -Encoding UTF8

    $runValue = '"' + (Join-Path $InstallDir $appExe) + '" --background'
    if ($TestMode) {
        New-Item -Path $runKeyPath -Force | Out-Null
        New-ItemProperty -Path $runKeyPath -Name 'CloseLoom' -Value $runValue -PropertyType String -Force | Out-Null
        New-Item -Path $uninstallKeyPath -Force | Out-Null
        New-ItemProperty -Path $uninstallKeyPath -Name 'DisplayName' -Value 'CloseLoom' -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKeyPath -Name 'UninstallString' -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $InstallDir 'uninstall.ps1') + '" -InstallDir "' + $InstallDir + '"') -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKeyPath -Name 'InstallLocation' -Value $InstallDir -PropertyType String -Force | Out-Null
    } else {
        New-Item -Path $runKeyPath -Force | Out-Null
        New-ItemProperty -Path $runKeyPath -Name 'CloseLoom' -Value $runValue -PropertyType String -Force | Out-Null
        New-Item -Path $uninstallKeyPath -Force | Out-Null
        New-ItemProperty -Path $uninstallKeyPath -Name 'DisplayName' -Value 'CloseLoom' -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKeyPath -Name 'DisplayVersion' -Value '1.0' -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKeyPath -Name 'InstallLocation' -Value $InstallDir -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKeyPath -Name 'UninstallString' -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $InstallDir 'uninstall.ps1') + '" -InstallDir "' + $InstallDir + '"') -PropertyType String -Force | Out-Null
    }
    New-Item -ItemType Directory -Path $shortcutDir -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $shortcutDir 'CloseLoom.lnk'))
    $shortcut.TargetPath = Join-Path $InstallDir $appExe
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Save()

    if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $InstallDir $appExe) -ArgumentList '--background' -WorkingDirectory $InstallDir }
    Write-Host "CloseLoom installed to $InstallDir" -ForegroundColor Green
} catch {
    Write-Error ("CloseLoom could not be installed: " + $_.Exception.Message)
    exit 1
}
