# CloseLoom

CloseLoom is a Windows 11 x64 desktop app that watches only applications you explicitly approve. It currently runs in **Dry Run**: it can record what it observes, but it cannot terminate applications. Keep saving your work normally; CloseLoom is not a backup or a guarantee that an application has saved its data.

## Install on Windows 11

1. Open the project's official [GitHub Releases](https://github.com/Saidaminov-x7/CloseLoom/releases) page and download `CloseLoom-win-x64.zip` from a release.
2. Extract the ZIP to a folder you can find, then double-click `install.cmd`.
3. Windows may ask you to confirm that you want to run the downloaded installer. CloseLoom installs for the current Windows account without administrator rights, opens the app, and adds it to that account's sign-in startup list.

The installer uses PowerShell scripts and respects the account's execution policy. On Windows installations that block unsigned scripts, installation and removal can fail; this package does not change or bypass that policy. A signed installer is needed for a reliable double-click experience under those policies.

The archive is self-contained: it includes the .NET runtime and does not require a separate .NET installation. For source checkout users, `install.cmd` can fetch the latest archive from the official GitHub Releases endpoint. A source checkout by itself is not a ready-to-run app; building from source requires the .NET 10 SDK and NuGet access. Do not run installer files from unofficial mirrors.

The installer creates a Start Menu entry and an Installed Apps uninstall entry where supported. To remove CloseLoom, use **Settings > Apps > Installed apps > CloseLoom > Uninstall**, or double-click `uninstall.cmd` in the extracted release folder. The installed app also includes an uninstall entrypoint. Uninstall stops CloseLoom and removes its startup registration, shortcuts, and installed program files. Your settings and activity history under `%LOCALAPPDATA%\CloseLoom` are kept unless you explicitly request data removal with the uninstall script's `-PurgeData` option.

## Startup and monitoring

At each sign-in, Windows starts CloseLoom in the notification area (system tray) for that user session. It remains available in the tray until you exit it or sign out. This is not an always-on service: it cannot run while the PC is shut down, asleep/hibernating, or when that user is signed out. Locking the screen normally leaves the session running. Remote Desktop behavior depends on Windows session policy and disconnect/logoff state.

Automatic monitoring on app launch is enabled by default. Monitoring starts only when you have selected and enabled applications to observe. With no approved applications, CloseLoom still opens in the tray and waits; it does not discover and monitor all apps automatically. You can pause or stop monitoring in the app. Close behavior follows the setting shown in the app; a normal window close may keep CloseLoom in the tray if that option is selected.

CloseLoom observes windows and processes in the current interactive user session. It may not see elevated applications, applications in another account/session, or every window transition. If Windows misses an event, sleep/resume or RDP changes the session, or another application behaves differently, observation may be delayed or incomplete. Do not rely on it as a security boundary or for uninterrupted monitoring.

## Safety and privacy

Dry Run is enforced and real process termination is disabled. CloseLoom does not close approved applications. Its local settings and activity log are stored in `%LOCALAPPDATA%\CloseLoom`; logs can contain application names, executable paths, and process IDs. Review exported logs before sharing them. See [security notes](docs/SECURITY.md).

## Build and verify from source

These commands require the .NET 10 SDK and NuGet access:

```powershell
dotnet restore CloseLoom.sln
dotnet build CloseLoom.sln --configuration Release --no-restore -warnaserror
dotnet test CloseLoom.sln --configuration Release --no-build
dotnet format CloseLoom.sln --verify-no-changes --no-restore
```

To create a self-contained Windows x64 ZIP, run `powershell -NoProfile -File scripts/publish-win-x64.ps1`. It refuses to replace existing artifacts unless you explicitly pass `-Force`. The archive contains install and uninstall scripts, app files, and documentation. It is an unsigned build, so Windows SmartScreen may display a warning. Do not disable Windows security protections to run it.

## Project and verification

- `src/CloseLoom.Core` — identity, decision policy, and grace-period state.
- `src/CloseLoom.Infrastructure` — Windows adapters, event observation, and JSON persistence.
- `src/CloseLoom.Desktop` — WPF interface and dependency injection.
- `tests` — isolated tests and disposable Windows integration fixtures.
- `docs` — [architecture](docs/ARCHITECTURE.md), [security](docs/SECURITY.md), [testing](docs/TESTING.md), [user guide](docs/USER_GUIDE.md), and [portability notes](docs/PORTABILITY.md).

The Russian overview is in [README.ru.md](README.ru.md); changes are recorded in [CHANGELOG.md](CHANGELOG.md). Prior verification is summarized in [the Windows test report](docs/REAL_WINDOWS_TEST_REPORT_2026-11-02.md). Those results cover the listed test machine and scenarios only, not all Windows PCs or unattended operation. Tray install/uninstall, sign-in startup, and session transitions must be verified for each distributed package before calling them tested.

MIT license: [LICENSE](LICENSE).
