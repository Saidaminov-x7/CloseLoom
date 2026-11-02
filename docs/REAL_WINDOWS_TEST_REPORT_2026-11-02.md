# AppGuard — Real Windows 11 integration report

Date: **2026-11-02**. Scope: locally authorized interactive testing with a disposable test application. All monitoring operates in **Dry Run**.

## Summary

| Scenario | Result | Details |
|---|---|---|
| Release compilation, warnings-as-errors | PASS | No warnings or errors |
| Core unit tests | PASS | 43/43 |
| Infrastructure unit tests | PASS | 20/20 |
| Live Windows integration using disposable WinForms windows | PASS | 38/38 |
| WinEvent hooks and process discovery | PASS | Tested in active desktop session |
| Minimize/restore and simultaneous windows | PASS | No false grace while a visible main window remained |
| Final window closes and 3-second grace | PASS | Dry Run logged; fixture process remained alive |
| Reopening cancels pending delay | PASS | No early decision |
| Stable background-only state after a Dry Run decision | PASS | Exactly one Dry Run over additional 7.5 seconds |
| Exit observation, Pause/Resume and Stop/Restart | PASS | Process-exit event and lifecycle transitions checked |
| WPF main window and five navigation pages | PASS | Real Windows UI Automation |
| WPF monitoring Start/Stop with a test allowlist | PASS | Status changed as requested |
| WPF exit confirmation | PASS | Yes confirmed; clean exit with code 0 |
| Actual forced process termination | NOT TESTED | Explicitly disabled |
| System tray menu | NOT TESTED | No direct interaction verification |
| Sleep/resume, security software and production applications | NOT TESTED | Outside the safe fixture scope |
| Remote GitHub Actions | NOT TESTED | No remote workflow result |
| Public GitHub push | BLOCKED | GitHub CLI authorization unavailable |

## Defects corrected

1. Invalid HWND after a DESTROY event could stop monitoring for an unrelated window. Added remembered HWND ownership for known windows. Unknown DESTROY events are ignored rather than attributed to a monitored process; unknown SHOW/HIDE still fail closed. This change is evaluated only under forced Dry Run and needs further review before any future Active mode.
2. Missing process snapshots did not reliably record confirmed process exit. Added independent process-absence verification without treating access errors as proof of exit.
3. Reentrant WPF Close() inside Closing caused an error dialog instead of shutdown. Deferred completion until after the event; verified clean exit.
4. Reconciliation could cause repeated Dry Run decisions on the same windowless process. Added a one-shot latch that resets only once a new eligible main window is observed, with two extra unit regression tests.

## Safety, cleanup and reproducibility

No ordinary user processes were force-terminated; no app autostart, system service or scheduled task was created; and Windows security settings were left unchanged. AppGuard executable was run only for temporary GUI tests. The temporary allowlist was removed and no resident AppGuard/test processes remained after checks.

Repeatable live test source: [tests/live-windows](../tests/live-windows/README.md). Raw logs and fixture configuration are stored only in temporary local paths outside the repository. Do not publish raw logs because they may contain paths and PIDs.

**Verdict:** The listed **Dry Run** tests PASS. Real Active-mode termination is still blocked and AppGuard is not certified or ready to force-close production applications.
