# AppGuard

AppGuard is a Windows 11 WPF application under development for observing applications explicitly selected by the user. It is designed to notice the disappearance of an observed user window and recheck a process after a grace period. **Only Dry Run is available. Active termination is deliberately disabled.**

## Current status

The code includes process discovery for the current session, Windows window-event hooks with bounded reconciliation, per-instance grace-period coordination, an allowlist, local JSON settings and activity log, and a desktop interface with tray controls. The monitoring implementation has not been exercised against Windows processes, and the UI and tray have not been launched or visually tested. Unit tests use fakes and do not establish end-to-end behavior. Do not rely on this development version to protect data or manage processes.

The interface provides application selection and per-application settings, dashboard, activity and log views, search and export, theme and language settings, and monitoring controls. Feature behavior and translations require actual UI verification. Some dynamic event reasons remain in English in Russian mode, and the navigation sidebar remains dark in light theme.

## Safety and privacy

Dry Run is enforced, including when an older settings file requests Active mode. The Windows termination adapter refuses all termination requests. A future Active mode would require explicit user consent and a reviewed means of proving process identity, window state, session membership and protection status. There is currently no reliable classification of every third-party security product. Closing a window does not establish that application data has been saved: forced termination may permanently lose unsaved data.

Application names, paths and process identifiers may be sensitive. Settings and activity logs are stored locally under the user's local application data directory. The log does not intentionally record window contents. Review exported logs before sharing. See [security](docs/SECURITY.md).

## Requirements and build

Windows 11, .NET 10 SDK, and access to NuGet for restore are required:

```powershell
dotnet restore AppGuard.sln
dotnet build AppGuard.sln --configuration Release --no-restore -warnaserror
dotnet test AppGuard.sln --configuration Release --no-build
dotnet format AppGuard.sln --verify-no-changes --no-restore
```

For future manual use, after reviewing the limitations, run `dotnet run --project src/AppGuard.Desktop`. AppGuard has not been launched as part of this development task. No installer, service, autostart entry or scheduled task is created by this repository.

## Architecture and documentation

- `src/AppGuard.Core` — identities, decision policy and grace-period state.
- `src/AppGuard.Infrastructure` — Windows adapters, event observation, and JSON persistence.
- `src/AppGuard.Desktop` — WPF user interface and dependency injection.
- `tests` — unit tests using fake process and window dependencies.
- `docs` — [architecture](docs/ARCHITECTURE.md), [security](docs/SECURITY.md), [testing](docs/TESTING.md), and [user guide](docs/USER_GUIDE.md).

A Russian overview is available in [README.ru.md](README.ru.md). See [CHANGELOG.md](CHANGELOG.md) for changes. There are no fabricated screenshots or performance measurements.

## Next steps

Review and test the Windows adapters and complete the UI under explicit authorization; resolve the remaining process-protection and event-delivery safety questions before considering Active mode. [MIT license](LICENSE).
