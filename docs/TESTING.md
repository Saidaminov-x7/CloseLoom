# Testing

Run `dotnet restore AppGuard.sln`, `dotnet build AppGuard.sln --configuration Release`, and `dotnet test AppGuard.sln --configuration Release`. Unit tests use fake process dependencies and temporary settings files; they do not exercise actual process termination or validate a running application. CI runs on Windows and does not launch the desktop app.

UI behavior, real process and window monitoring, tray behavior, and end-to-end safety remain NOT TESTED. Passing isolated tests does not establish those behaviors.
