# Live Windows integration tests (explicit opt-in)

These executable tests use real Windows 11 WinEvent hooks and a disposable WinForms fixture.
They exercise the AppGuard monitoring components in Dry Run only: minimization,
two open windows, final-window close, cancellation on reopen, the three-second grace
period, one-shot decision after 7.5 seconds, process exit, pause/resume, stop/restart,
and local JSON event logs.

Safety: This does not run AppGuard.Desktop.exe, enable real process termination,
install AppGuard or write user allowlists. It creates only an isolated test fixture
process launched by the test runner. The runner closes the fixture after testing.
Tests require an interactive Windows desktop session.

Requirements: Windows 11, .NET 10 SDK, desktop session access. Admin rights not required.

Run from the repository root in PowerShell:

    dotnet build tests/live-windows/Fixture/LiveFixture.csproj -c Release
    dotnet build tests/live-windows/Runner/LiveRunner.csproj -c Release
    $fixture = (Resolve-Path tests/live-windows/Fixture/bin/Release/net10.0-windows/LiveFixture.exe).Path
    $runner = (Resolve-Path tests/live-windows/Runner/bin/Release/net10.0-windows/LiveRunner.exe).Path
    $report = Join-Path $env:TEMP ("AppGuard-Live-" + [guid]::NewGuid().ToString("N"))
    & $runner $report $fixture
    $LASTEXITCODE

Reports (summary.json, activity.jsonl and fixture configuration) are written only to
$report; DO NOT upload these files publicly. They contain local paths or process IDs.

Live tests are not included in GitHub Actions because CI runners do not guarantee
an interactive desktop window session.

Limitations: forced process termination, production applications, third-party security
products, Windows sleep/resume, and tray menus are not verified by this harness.
A passing live test does not imply that Active mode is safe.
