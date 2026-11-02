# AppGuard

AppGuard is a work-in-progress Windows 11 desktop project intended to help users observe explicitly selected applications and handle lingering processes after windows close. **Monitoring and active termination are not available in this revision.** Do not rely on it to manage processes or protect data.

## Status

The repository contains a WPF shell, a safety decision policy, Windows process/window adapters, settings and activity storage, and isolated unit tests. The desktop shell and these components are not yet a complete monitoring application. No screenshots or performance claims are provided.

## Safety

Dry Run is the default setting. The Windows termination adapter currently refuses termination; no automatic process termination is available. Closing an application window does not establish that data has been saved. Forced termination can cause permanent loss of unsaved data. Windows services, security software, other sessions and ambiguous identities require special protection. See [SECURITY.md](docs/SECURITY.md).

## Requirements and build

Windows 11, .NET 10 SDK, and network access for NuGet restore are required. From the repository root:

```powershell
dotnet restore AppGuard.sln
dotnet build AppGuard.sln --configuration Release
dotnet test AppGuard.sln --configuration Release
```

The desktop application can eventually be started manually with `dotnet run --project src/AppGuard.Desktop` after reviewing the limitations. It has not been launched during this development task, and its UI and real process behavior have not been verified.

## Architecture

- `src/AppGuard.Core`: models, interfaces, and fail-closed decision policy.
- `src/AppGuard.Infrastructure`: Windows adapters and JSON storage.
- `src/AppGuard.Desktop`: WPF user interface.
- `tests`: isolated tests with fakes; these do not validate real monitoring.
- `docs`: architecture, safety, testing and user guide.

See [architecture](docs/ARCHITECTURE.md), [testing](docs/TESTING.md), and [user guide](docs/USER_GUIDE.md). Russian overview: [README.ru.md](README.ru.md).

## Next steps

Connect the grace-period coordinator to event-driven process and window monitoring; complete the tray, localization, settings, and activity interface; independently review process protection; run authorized integration and UI tests. No installer or release is provided.

## License

[MIT](LICENSE).
