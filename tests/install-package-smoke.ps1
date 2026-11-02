[CmdletBinding()]
param([string]$PackageZip = (Join-Path $PSScriptRoot '..\artifacts\CloseLoom-win-x64.zip'))
$ErrorActionPreference = 'Stop'
$PackageZip = [IO.Path]::GetFullPath($PackageZip)
if (-not (Test-Path -LiteralPath $PackageZip)) { throw "Package not found: $PackageZip. Publish it first or pass -PackageZip." }

$id = [guid]::NewGuid().ToString('N')
$root = Join-Path ([IO.Path]::GetTempPath()) "CloseLoom-smoke-$id"
$package = Join-Path $root 'package'
$installDir = Join-Path $root 'installed'
$registryRoot = "HKCU:\Software\CloseLoom\InstallTests\$id"
$shortcutDir = Join-Path ([IO.Path]::GetTempPath()) "CloseLoom-StartMenu-$id"
$actualRunPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$beforeActual = (Get-ItemProperty -Path $actualRunPath -Name CloseLoom -ErrorAction SilentlyContinue).CloseLoom
try {
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    Expand-Archive -LiteralPath $PackageZip -DestinationPath $package
    $payload = Get-ChildItem -LiteralPath $package -Filter 'CloseLoom.Desktop.exe' -File -Recurse | Select-Object -First 1
    if (-not $payload) { throw 'ZIP does not contain CloseLoom.Desktop.exe.' }
    $payloadRoot = $payload.Directory.FullName
    if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot 'install.ps1'))) { throw 'ZIP does not contain install.ps1 beside the executable.' }
    $env:CLOSELOOM_TEST_REGROOT = $registryRoot
    $env:CLOSELOOM_TEST_SHORTCUTDIR = $shortcutDir
    & (Join-Path $payloadRoot 'install.ps1') -InstallDir $installDir -NoLaunch -TestMode
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Installer returned a failure code.' }
    if (-not (Test-Path -LiteralPath (Join-Path $installDir 'CloseLoom.Desktop.exe'))) { throw 'Installed executable is missing.' }
    if (-not (Test-Path -LiteralPath (Join-Path $installDir 'uninstall.ps1'))) { throw 'Installed uninstaller is missing.' }
    $runValue = (Get-ItemProperty -Path (Join-Path $registryRoot 'Run') -Name CloseLoom).CloseLoom
    $expectedRun = '"' + (Join-Path $installDir 'CloseLoom.Desktop.exe') + '" --background'
    if ($runValue -ne $expectedRun) { throw "Isolated Run value mismatch: $runValue" }
    $uninstallKey = Get-ItemProperty -Path (Join-Path $registryRoot 'Uninstall')
    if ($uninstallKey.DisplayName -ne 'CloseLoom' -or $uninstallKey.InstallLocation -ne $installDir -or $uninstallKey.UninstallString -notlike ('*uninstall.ps1* -InstallDir "' + $installDir + '"')) { throw 'Isolated Installed Apps uninstaller registration is incomplete.' }
    if (-not (Test-Path -LiteralPath (Join-Path $shortcutDir 'CloseLoom.lnk'))) { throw 'Isolated Start Menu shortcut is missing.' }
    $afterActual = (Get-ItemProperty -Path $actualRunPath -Name CloseLoom -ErrorAction SilentlyContinue).CloseLoom
    if ($afterActual -ne $beforeActual) { throw 'The actual per-user Windows Run entry changed during TestMode.' }

    $powershellExe = (Get-Process -Id $PID).Path
    $lockedExe = [IO.File]::Open((Join-Path $installDir 'CloseLoom.Desktop.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $uninstallArgs = @('-NoProfile', '-File', ('"{0}"' -f (Join-Path $installDir 'uninstall.ps1')), '-InstallDir', ('"{0}"' -f $installDir), '-TestMode')
        $failedUninstall = Start-Process -FilePath $powershellExe -ArgumentList $uninstallArgs -PassThru -Wait -WindowStyle Hidden
        if ($failedUninstall.ExitCode -eq 0) { throw 'Uninstaller reported success while a package file was locked.' }
        if (-not (Test-Path -LiteralPath (Join-Path $installDir '.closeloom-install.json')) -or -not (Test-Path -LiteralPath $registryRoot) -or -not (Test-Path -LiteralPath (Join-Path $shortcutDir 'CloseLoom.lnk'))) { throw 'Failed uninstall removed its retry marker or registrations.' }
    } finally { $lockedExe.Dispose() }
    & (Join-Path $payloadRoot 'uninstall.ps1') -InstallDir $installDir -TestMode
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Uninstaller returned a failure code.' }
    if (Test-Path -LiteralPath $installDir) { throw 'Uninstall left files in the isolated installation directory.' }
    if (Test-Path -LiteralPath $registryRoot) { throw 'Uninstall left isolated registry entries.' }
    if (Test-Path -LiteralPath $shortcutDir) { throw 'Uninstall left the isolated shortcut directory.' }
    & (Join-Path $payloadRoot 'uninstall.ps1') -InstallDir $installDir -TestMode
    $leftoverDir = Join-Path $root 'unrelated-leftover-folder'
    New-Item -ItemType Directory -Path $leftoverDir | Out-Null
    $leftoverSentinel = Join-Path $leftoverDir 'preserve.txt'
    Set-Content -LiteralPath $leftoverSentinel -Value 'preserve unverified content'
    & (Join-Path $payloadRoot 'uninstall.ps1') -InstallDir $leftoverDir -TestMode
    if ((Get-Content -LiteralPath $leftoverSentinel -Raw) -ne "preserve unverified content`r`n") { throw 'Repeat uninstall altered an unverified leftover folder.' }
    $unrelatedDir = Join-Path $root 'unrelated-existing-folder'
    New-Item -ItemType Directory -Path $unrelatedDir | Out-Null
    $sentinel = Join-Path $unrelatedDir 'keep.txt'
    Set-Content -LiteralPath $sentinel -Value 'preserve this unrelated file'
    $negativeArgs = @('-NoProfile', '-File', ('"{0}"' -f (Join-Path $payloadRoot 'install.ps1')), '-InstallDir', ('"{0}"' -f $unrelatedDir), '-NoLaunch', '-TestMode')
    $negativeRun = Start-Process -FilePath $powershellExe -ArgumentList $negativeArgs -PassThru -Wait -WindowStyle Hidden
    if ($negativeRun.ExitCode -eq 0 -or -not (Test-Path -LiteralPath $sentinel)) { throw 'Installer did not safely refuse an unmarked existing directory or altered its contents.' }
    $markedDir = Join-Path $root 'marked-existing-folder'
    New-Item -ItemType Directory -Path $markedDir | Out-Null
    @{ Product = 'CloseLoom'; Files = @('.closeloom-install.json') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $markedDir '.closeloom-install.json')
    $collisionSentinel = Join-Path $markedDir 'CloseLoom.Desktop.exe'
    Set-Content -LiteralPath $collisionSentinel -Value 'preserve unrelated executable data'
    $collisionArgs = @('-NoProfile', '-File', ('"{0}"' -f (Join-Path $payloadRoot 'install.ps1')), '-InstallDir', ('"{0}"' -f $markedDir), '-NoLaunch', '-TestMode')
    $collisionRun = Start-Process -FilePath $powershellExe -ArgumentList $collisionArgs -PassThru -Wait -WindowStyle Hidden
    if ($collisionRun.ExitCode -eq 0 -or (Get-Content -LiteralPath $collisionSentinel -Raw) -ne "preserve unrelated executable data`r`n") { throw 'Installer overwrote an unrelated payload-name collision in a marked directory.' }
    $afterActual = (Get-ItemProperty -Path $actualRunPath -Name CloseLoom -ErrorAction SilentlyContinue).CloseLoom
    if ($afterActual -ne $beforeActual) { throw 'The actual per-user Windows Run entry changed during uninstall.' }
    Write-Output 'PASS: package extracted, installed, registered in isolated test locations, and uninstalled cleanly.'
} finally {
    if (Test-Path -LiteralPath $registryRoot) { Remove-Item -LiteralPath $registryRoot -Recurse -Force }
    if (Test-Path -LiteralPath $shortcutDir) { Remove-Item -LiteralPath $shortcutDir -Recurse -Force }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    Remove-Item Env:CLOSELOOM_TEST_REGROOT -ErrorAction SilentlyContinue
    Remove-Item Env:CLOSELOOM_TEST_SHORTCUTDIR -ErrorAction SilentlyContinue
}
