# Windows distribution, installation, and session limits

CloseLoom is a WPF desktop application for Windows 11 x64. The supported end-user package is the self-contained `win-x64` ZIP attached to an official GitHub Release. It includes the .NET runtime, so a user does not need the .NET SDK or a separately installed runtime. The per-user installer needs no administrator rights. The package is unsigned; Windows SmartScreen may warn about it.

## Install

1. Download `CloseLoom-win-x64.zip` from [official releases](https://github.com/Saidaminov-x7/CloseLoom/releases).
2. Extract it, then double-click `install.cmd`.
3. The installer copies the package to `%LOCALAPPDATA%\Programs\CloseLoom`, registers a quoted absolute executable path in the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key with the `--background` argument, creates a Start Menu shortcut and an Installed Apps uninstall entry where supported, then launches CloseLoom. No admin elevation, Windows service, scheduled task, or system-wide registration is required.

`install.ps1` is available for PowerShell use, including `-NoLaunch` and an alternate `-InstallDir` for isolated tests. The install harness uses `-TestMode` with isolated test registry and shortcut roots; it must not be used to change a normal user's install location. When the scripts are run from a source checkout and no adjacent release payload is present, the installer downloads the latest package only from the official GitHub Releases endpoint. The source archive alone is not runnable; building it requires the .NET 10 SDK and NuGet access.

The package includes `uninstall.cmd` and `uninstall.ps1`. Windows also offers removal through **Settings > Apps > Installed apps > CloseLoom** when registration succeeded. Uninstall removes the app's own Run value, its own shortcuts, Installed Apps registration, and installed files; it stops the CloseLoom process before removing files. User settings and logs in `%LOCALAPPDATA%\CloseLoom` remain by default. `-PurgeData` explicitly requests removal of that data. Installation and removal are intended to be idempotent and to touch only CloseLoom-owned paths and values.

## Startup and monitoring behavior

The Run registration applies to the current Windows account. On sign-in, Windows starts CloseLoom with `--background`, which is intended to start in the notification area without flashing the main window. CloseLoom remains in that user's tray until the user exits it or signs out. If no applications are selected and enabled, the app remains available in the tray but does not monitor any applications. Automatic monitoring on app launch is enabled by default and can be changed in the app; the user can pause or stop monitoring.

This is a logged-in desktop app, not an always-on service. It cannot run when the PC is shut down, asleep or hibernating, or when the user has logged out. A locked screen usually leaves the interactive session active. RDP disconnect, reconnect, session switching, or policy-driven logoff can affect whether the session and its app continue. Windows event delivery can be incomplete or delayed around suspension and session changes. No 24/7 monitoring guarantee is made.

Observation is limited to applications and windows visible to the current interactive user session. Apps running elevated or under another account/session may not be observed. Dry Run is enforced and CloseLoom cannot terminate processes. Never depend on it to save data in another app or to provide security protection.

## Build and validation

From a Windows machine with the .NET 10 SDK and NuGet access:

```powershell
dotnet restore CloseLoom.sln
dotnet build CloseLoom.sln --configuration Release --no-restore -warnaserror
dotnet test CloseLoom.sln --configuration Release --no-build
dotnet format CloseLoom.sln --verify-no-changes --no-restore
powershell -NoProfile -File scripts/publish-win-x64.ps1
```

The publish script writes into `artifacts/` and refuses to overwrite existing outputs unless `-Force` is explicitly supplied. The ZIP is a Windows x64 build, not a claim of portability across Windows 10, Windows Server, ARM64, x86, or every Windows 11 configuration. DPI, security software, language, user policies, RDP, sleep/resume, and other PCs require separate testing. See [testing notes](TESTING.md) and the dated [Windows report](REAL_WINDOWS_TEST_REPORT_2026-11-02.md); these reports cover only their listed environment and cases.
