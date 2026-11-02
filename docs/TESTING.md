# Testing

## Unit checks

Unit tests use fake clocks, mock process/window catalogs and isolated temporary files. They do not establish that real process termination is safe. Verification on a Windows 11 laptop on 2026-11-02: 43 Core plus 20 Infrastructure tests, **63/63 PASS**.

The Release build with warnings-as-errors passed with zero errors and warnings. Earlier formatting verification passed, but format verification after the latest changes could not be repeated; the commands are documented in the root README.

## Interactive Windows tests (explicit opt-in)

Independently verified outside the Codex sandbox on an interactive Windows desktop, authorized tests ran the real WinEvent hooks and monitor with a disposable WinForms process, only in Dry Run. Result **38/38 PASS**. Tested process discovery, two windows, minimizing, final-window close, cancellation on reopen, 3-second grace, one-shot decision after 7.5 seconds, exit detection, pause/resume, stop/restart.

Reproducible source code and manual instructions: [tests/live-windows](../tests/live-windows/README.md). In the restricted Codex sandbox, 11 of 38 checks failed; this environment-specific result is not a production pass. These tests are deliberately excluded from normal CI because an interactive Windows desktop is required.

Separate real WPF smoke tests exercised the main window, all five navigation sections, monitoring Start/Stop with an explicitly allowed disposable EXE, and closing by clicking Yes in the exit confirmation dialog. The GUI test allowlist was removed after the checks. The desktop EXE exited normally, code 0. An extracted self-contained win-x64 portable ZIP also launched a real WPF window and exited cleanly with code 0. Compatibility with other PCs remains unverified. Clean-room restore, build and test passed.

## Unverified functionality and safety boundaries

- Real forced process termination: NOT TESTED and disabled by code, even when a config requests Active.
- Tray menu actions, sleep/wake, third-party antivirus interactions, and production applications: NOT TESTED.
- Remote GitHub Actions: NOT TESTED. GitHub CLI publishing is blocked because it is not authorized.
- A PASS here does not prove safe automatic termination or production readiness. Closing a window does not prove user data was saved.
- All fixture logs and diagnostics remain outside the public repository and can include sensitive local paths. Never publish raw logs.

See [dated test report](REAL_WINDOWS_TEST_REPORT_2026-11-02.md).
